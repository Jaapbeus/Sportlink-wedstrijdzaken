using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using FunctionApp.Tests.Email.TestDoubles;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Vrije modelvelden worden begrensd voordat ze in een antwoord belanden (#1673) en berichten van
/// automatische afzenders worden overgeslagen (#1674). Geen database of AI-aanroep nodig.
/// </summary>
public class EmailAiVeldEnAutomatischeAfzenderTests
{
    private static readonly DateTime Vandaag =
        BerichtAiService.KnvbRegelsGeldigTot.AddDays(-30).ToDateTime(TimeOnly.MinValue);

    private static readonly ClubAppSettingsSnapshot ClubSettings = new(
        PlannerAfzenderNaam: null, CoordinatorNaam: null, CoordinatorFunctie: null,
        EmailVoetnoot: "Testomgeving", HerplanDeadlineDagen: null);

    private static InkomendBericht MaakEmail() => new()
    {
        MessageId = "veld-test",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Vraag",
        OntvangstDatum = DateTime.UtcNow,
        Body = "Wie is de trainer?"
    };

    // ── Classificatie: modelvelden ──

    [Fact]
    public void Parse_KnvbNotitieMetLink_WordtGenegeerd()
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            """{"type":"herplan_verzoek","knvbNotitie":"Zie https://voorbeeld.test/regels"}""", Vandaag);

        result.KnvbNotitie.Should().BeNull();
    }

    [Fact]
    public void Parse_KnvbNotitieMetRegeleinden_WordtEenRegel()
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            "{\"type\":\"herplan_verzoek\",\"knvbNotitie\":\"Eerste regel\\nTweede regel\"}", Vandaag);

        result.KnvbNotitie.Should().Be("Eerste regel Tweede regel");
    }

    [Fact]
    public void Parse_KnvbNotitieTeLang_WordtGenegeerd()
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            "{\"knvbNotitie\":\"" + new string('a', 201) + "\"}", Vandaag);

        result.KnvbNotitie.Should().BeNull();
    }

    [Theory]
    [InlineData("14:30", "14:30")]
    [InlineData("9:05", "09:05")]
    [InlineData("half drie", null)]
    [InlineData("25:99", null)]
    public void Parse_AanvangsTijd_WordtGenormaliseerdOfGenegeerd(string invoer, string? verwacht)
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            "{\"type\":\"beschikbaarheid_check\",\"aanvangsTijd\":\"" + invoer + "\"}", Vandaag);

        result.AanvangsTijd.Should().Be(verwacht);
    }

    [Fact]
    public void Parse_Samenvatting_WordtBegrensd()
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            "{\"samenvatting\":\"" + new string('x', 900) + "\"}", Vandaag);

        result.Samenvatting.Length.Should().BeLessThanOrEqualTo(500);
    }

    // ── Antwoordtekst: team en tegenstander ──

    private static BerichtClassificatie TeamContact(string? team, bool onbevestigd) => new()
    {
        Type = VerzoekType.TeamContactOpvragen,
        TeamNaam = team,
        TeamNaamOnbevestigd = onbevestigd
    };

    [Fact]
    public void TeamContactAntwoord_BevestigdTeamWordtGenoemd()
    {
        var (_, body) = BerichtResponseGenerator.BouwTeamContactAutoReply(TeamContact("JO13-2", false), MaakEmail(), ClubSettings);

        body.Should().Contain("de begeleiding van JO13-2");
    }

    [Fact]
    public void TeamContactAntwoord_OnbevestigdTeamKrijgtGeneriekeTekst()
    {
        var (_, body) = BerichtResponseGenerator.BouwTeamContactAutoReply(TeamContact("JO13-2", true), MaakEmail(), ClubSettings);

        body.Should().Contain("de begeleiding van het opgegeven team").And.NotContain("JO13-2");
    }

    [Fact]
    public void TeamContactAntwoord_TeamnaamMetLinkWordtNietOvergenomen()
    {
        var (_, body) = BerichtResponseGenerator.BouwTeamContactAutoReply(
            TeamContact("JO13 https://voorbeeld.test", false), MaakEmail(), ClubSettings);

        body.Should().Contain("het opgegeven team").And.NotContain("voorbeeld.test");
    }

    [Fact]
    public void Sjabloon_PlaatshoudersKrijgenAlleenVeiligeWaarden()
    {
        var template = new EmailTemplate("test", "Onderwerp {{team}}", "Team: {{team}}; tegenstander: {{tegenstander}}; tijd: {{aanvangstijd}}");
        var classificatie = new BerichtClassificatie
        {
            TeamNaam = "JO13-2",
            TeamNaamOnbevestigd = true,
            Tegenstander = "Ajax www.voorbeeld.test",
            AanvangsTijd = "14:30"
        };

        var (onderwerp, body) = BerichtResponseGenerator.BouwAangepasteAntwoord(template, classificatie, MaakEmail(), ClubSettings);

        onderwerp.TrimEnd().Should().Be("Onderwerp");
        body.Should().Contain("Team: ; tegenstander: ; tijd: 14:30").And.NotContain("voorbeeld.test").And.NotContain("JO13-2");
    }

    [Fact]
    public void TeamOnbekendAntwoord_TegenstanderMetLinkKrijgtGeneriekeTekst()
    {
        var (_, body) = BerichtResponseGenerator.BouwTeamOnbekendAntwoord(
            "Ajax https://voorbeeld.test", new BerichtClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("de opgegeven tegenstander").And.NotContain("voorbeeld.test");
    }

    // ── Automatische afzenders ──

    [Fact]
    public async Task PreFilter_OverslaatBerichtenVanAutomatischeAfzenders()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Afzender = "a@voorbeeld.nl", InternetHeaders = [new("Auto-Submitted", "auto-replied")] },
            new() { MessageId = "m2", Afzender = "b@voorbeeld.nl", InternetHeaders = [new("Precedence", "bulk")] },
            new() { MessageId = "m3", Afzender = "c@voorbeeld.nl", InternetHeaders = [new("Auto-Submitted", "no")] },
            new() { MessageId = "m4", Afzender = "d@voorbeeld.nl" }
        };

        var result = await service.PreFilterVoorClassificatieAsync(
            emails, "coordinator@voorbeeld.nl", new HashSet<string>(), graph, NullLogger.Instance);

        result.Select(e => e.MessageId).Should().Equal(["m3", "m4"]);
        graph.MarkedAsReadIds.Should().BeEquivalentTo(["m1", "m2"]);
    }
}
