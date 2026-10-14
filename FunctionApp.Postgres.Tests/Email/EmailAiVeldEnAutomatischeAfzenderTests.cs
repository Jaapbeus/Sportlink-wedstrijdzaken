using AwesomeAssertions;
using Database.Postgres.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Processing;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Vrije modelvelden worden begrensd voordat ze in een antwoord belanden (#1673) en berichten van
/// automatische afzenders worden overgeslagen (#1674). Geen database of AI-aanroep nodig.
/// </summary>
public class EmailAiVeldEnAutomatischeAfzenderTests
{
    private static readonly DateTime Vandaag =
        BerichtAiKern.KnvbRegelsGeldigTot.AddDays(-30).ToDateTime(TimeOnly.MinValue);

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
        var result = BerichtAiKern.ParseClassificatieResponse(
            """{"type":"herplan_verzoek","knvbNotitie":"Zie https://voorbeeld.test/regels"}""", Vandaag);

        result.KnvbNotitie.Should().BeNull();
    }

    [Fact]
    public void Parse_KnvbNotitieMetRegeleinden_WordtEenRegel()
    {
        var result = BerichtAiKern.ParseClassificatieResponse(
            "{\"type\":\"herplan_verzoek\",\"knvbNotitie\":\"Eerste regel\\nTweede regel\"}", Vandaag);

        result.KnvbNotitie.Should().Be("Eerste regel Tweede regel");
    }

    [Fact]
    public void Parse_KnvbNotitieTeLang_WordtGenegeerd()
    {
        var result = BerichtAiKern.ParseClassificatieResponse(
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
        var result = BerichtAiKern.ParseClassificatieResponse(
            "{\"type\":\"beschikbaarheid_check\",\"aanvangsTijd\":\"" + invoer + "\"}", Vandaag);

        result.AanvangsTijd.Should().Be(verwacht);
    }

    [Fact]
    public void Parse_Samenvatting_WordtBegrensd()
    {
        var result = BerichtAiKern.ParseClassificatieResponse(
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

        onderwerp.Should().Be("Onderwerp het opgegeven team");
        body.Should().Contain("Team: het opgegeven team; tegenstander: de opgegeven tegenstander; tijd: 14:30")
            .And.NotContain("voorbeeld.test").And.NotContain("JO13-2");
    }

    [Fact]
    public void Sjabloon_BevestigingMetOnbevestigdTeamKrijgtGeneriekeTekstInPlaatsVanLeeg()
    {
        // Zelfde vorm als het seed-sjabloon voor een bevestiging.
        var template = new EmailTemplate("bevestiging", "Bevestiging wedstrijd {{datum}} — {{team}} vs {{tegenstander}}", "Team {{team}} tegen {{tegenstander}}.");
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.Bevestiging,
            Datum = "2026-06-06",
            TeamNaam = "JO13-2",
            TeamNaamOnbevestigd = true,
            Tegenstander = "FC Voorbeeld (Noord)"
        };

        var (onderwerp, body) = BerichtResponseGenerator.BouwAangepasteAntwoord(template, classificatie, MaakEmail(), ClubSettings);

        onderwerp.Should().EndWith("— het opgegeven team vs FC Voorbeeld (Noord)").And.NotContain("—  vs");
        body.Should().Contain("Team het opgegeven team tegen FC Voorbeeld (Noord).");
    }

    [Fact]
    public void Sjabloon_ZonderTeamEnTegenstanderBlijvenPlaatshoudersLeeg()
    {
        var template = new EmailTemplate("test", "Onderwerp", "Team: {{team}}; tegenstander: {{tegenstander}};");

        var (_, body) = BerichtResponseGenerator.BouwAangepasteAntwoord(
            template, new BerichtClassificatie { TeamNaamOnbevestigd = true }, MaakEmail(), ClubSettings);

        body.Should().Contain("Team: ; tegenstander: ;");
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
            new() { MessageId = "m4", Afzender = "d@voorbeeld.nl" },
            new() { MessageId = "m5", Afzender = "e@voorbeeld.nl", InternetHeaders = [new("Precedence", "list")] },
            new() { MessageId = "m6", Afzender = "f@voorbeeld.nl", InternetHeaders = [new("X-Auto-Response-Suppress", "All")] }
        };

        var result = await service.PreFilterVoorClassificatieAsync(
            emails, "coordinator@voorbeeld.nl", new HashSet<string>(), graph, NullLogger.Instance);

        // Een mailinglijst of een inkomende suppress-header is geen automatische afzender: normale verwerking.
        result.Select(e => e.MessageId).Should().Equal(["m3", "m4", "m5", "m6"]);
        graph.MarkedAsReadIds.Should().BeEquivalentTo(["m1", "m2"]);
    }

    [Fact]
    public async Task PreFilter_AutomatischBerichtKrijgtLabelVoorDeCoordinator()
    {
        var graph = new FakeEmailGraphService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Afzender = "a@voorbeeld.nl", InternetHeaders = [new("Auto-Submitted", "auto-replied")] },
            new() { MessageId = "m2", Afzender = "b@voorbeeld.nl" }
        };

        await new EmailBatchFilterService().PreFilterVoorClassificatieAsync(
            emails, "coordinator@voorbeeld.nl", new HashSet<string>(), graph, NullLogger.Instance);

        graph.CategoryUpdates.Should().ContainSingle().Which.MessageId.Should().Be("m1");
        graph.CategoryUpdates[0].Categories.Should().Equal([EmailCategorieLabels.GeenAiAntwoord]);
        graph.EnsuredCategories.Select(c => c.Name).Should().Contain(EmailCategorieLabels.GeenAiAntwoord);
    }

    // ── Pipeline: team zonder bevestiging door de teamresolutie (#1673) ──
    // De Postgres-pipeline leest bij de start PostgresDatabaseConfig (omgevingsvariabele), dus deze
    // tests draaien in de CI-job met database; de takken zelf raken de database niet.

    [PostgresTheory]
    [InlineData(VerzoekType.Bevestiging)]
    [InlineData(VerzoekType.BuitenScope)]
    public async Task Pipeline_TeamresolutieZonderUitkomst_ZetTeamNaamOnbevestigd(VerzoekType type)
    {
        var classificatie = new BerichtClassificatie { Type = type, TeamNaam = "JO13-2", Tegenstander = "FC Voorbeeld" };

        await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, MaakEmail(), NullLogger.Instance, new VasteResolver(null), "TESTCLUB");

        classificatie.TeamNaamOnbevestigd.Should().BeTrue();
        AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd).Should().BeNull();
    }

    [PostgresFact]
    public async Task Pipeline_TeamresolutieMetUitkomst_LaatTeamNaamBevestigd()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.Bevestiging, TeamNaam = "jo13 2", TeamNaamOnbevestigd = true };

        await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, MaakEmail(), NullLogger.Instance, new VasteResolver("JO13-2"), "TESTCLUB");

        classificatie.TeamNaamOnbevestigd.Should().BeFalse();
        classificatie.TeamNaam.Should().Be("JO13-2");
    }

    [PostgresFact]
    public async Task Pipeline_ZonderClubcode_ZetTeamNaamOnbevestigd()
    {
        // Een lege clubcode valt in de pipeline terug op de primaire club uit de procesbrede
        // PostgresAppSettings-cache. "Zonder clubcode" betekent dus: ook die cache leeg. Expliciet
        // leegmaken, anders hangt de uitkomst af van welke testklasse eerder de cache vulde.
        PostgresAppSettings.ResetForTests();
        var classificatie = new BerichtClassificatie { Type = VerzoekType.Bevestiging, TeamNaam = "JO13-2" };

        await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, MaakEmail(), NullLogger.Instance, new VasteResolver("JO13-2"), " ");

        classificatie.TeamNaamOnbevestigd.Should().BeTrue();
    }

    /// <summary>Lost elke aanvraag op naar <c>canoniek</c>, of niets als die <c>null</c> is.</summary>
    private sealed class VasteResolver(string? canoniek) : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(canoniek is null
                ? TeamResolutionResult.Onopgelost
                : new TeamResolutionResult(1, canoniek, 1.0, Array.Empty<TeamCandidate>(), ResolutionBron.ExacteMatch));
    }
}
