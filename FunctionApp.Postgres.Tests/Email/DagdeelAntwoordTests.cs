using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Processing;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// #1587: een mail die om een dagdeel (ochtend/middag/avond) vraagt, moet vensters binnen dat dagdeel
/// opleveren, en het antwoord vermeldt welk dagdeel en welk tijdvenster is gecontroleerd — zonder
/// alternatieven uit andere dagdelen te noemen (besluit eigenaar). Geen database of AI-aanroep nodig.
/// SQL Server-tier-tegenhanger: <c>FunctionApp.Tests/Email/DagdeelAntwoordTests.cs</c>.
/// </summary>
public class DagdeelAntwoordTests
{
    private static readonly DateTime Vandaag =
        BerichtAiService.KnvbRegelsGeldigTot.AddDays(-30).ToDateTime(TimeOnly.MinValue);

    private static readonly ClubAppSettingsSnapshot ClubSettings = new(
        PlannerAfzenderNaam: null, CoordinatorNaam: null, CoordinatorFunctie: null,
        EmailVoetnoot: "Testomgeving", HerplanDeadlineDagen: null);

    private static InkomendBericht MaakEmail() => new()
    {
        MessageId = "dagdeel-test",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Oefenwedstrijd",
        OntvangstDatum = DateTime.UtcNow,
        Body = "Kan het zaterdagochtend?"
    };

    private static BerichtClassificatie MaakClassificatie(string? dagdeel = "ochtend") => new()
    {
        Type = VerzoekType.BeschikbaarheidCheck,
        Datum = "2026-10-10",
        Dagdeel = dagdeel,
        TeamNaam = "JO11-1"
    };

    private static BeschikbaarVenster Venster(string van, string tot) => new()
    {
        VeldNummer = 1, VeldNaam = "veld 1", Van = van, Tot = tot, MaxDuurMinuten = 120, VeldType = "kunstgras"
    };

    // ── Classificatie ──

    [Fact]
    public void Parse_DagdeelInJson_WordtOvergenomen()
    {
        var result = BerichtAiService.ParseClassificatieResponse(
            """{"type":"beschikbaarheid_check","dagdeel":"ochtend"}""", Vandaag);

        result.Dagdeel.Should().Be("ochtend");
    }

    [Theory]
    [InlineData("""{"dagdeel":" Middag "}""", "middag")]
    [InlineData("""{"dagdeel":null}""", null)]
    [InlineData("""{"dagdeel":"nacht"}""", null)]
    [InlineData("""{"dagdeel":""}""", null)]
    [InlineData("""{"dagdeel":12}""", null)]
    [InlineData("""{}""", null)]
    public void Parse_DagdeelWordtGenormaliseerdOfNull(string json, string? verwacht)
        => BerichtAiService.ParseClassificatieResponse(json, Vandaag).Dagdeel.Should().Be(verwacht);

    // ── Doorgifte naar de planner ──

    [Fact]
    public void BouwBeschikbaarheidRequest_GeeftHetDagdeelDoorAanDePlanner()
    {
        var classificatie = MaakClassificatie("middag");
        classificatie.AanvangsTijd = "14:00";

        var request = BerichtPipeline.BouwBeschikbaarheidRequest(classificatie, "2026-10-10");

        request.Dagdeel.Should().Be("middag");
        request.Datum.Should().Be("2026-10-10");
        request.AanvangsTijd.Should().Be("14:00");
        request.TeamNaam.Should().Be("JO11-1");
    }

    [Fact]
    public void BouwBeschikbaarheidRequest_ZonderDagdeel_LaatHetDagdeelLeeg()
        => BerichtPipeline.BouwBeschikbaarheidRequest(MaakClassificatie(null), "2026-10-10")
            .Dagdeel.Should().BeNull();

    [Theory]
    [InlineData("ochtend", "08:30", "12:00")]
    [InlineData("middag", "12:00", "17:00")]
    public void PlannerVenster_VolgtHetDagdeelUitHetVerzoek(string dagdeel, string van, string tot)
    {
        var request = BerichtPipeline.BouwBeschikbaarheidRequest(MaakClassificatie(dagdeel), "2026-10-10");

        AvailabilityService.ResolveDagdeelVenster(request.Dagdeel, new TimeOnly(0, 0), new TimeOnly(23, 59))
            .Should().Be((TimeOnly.Parse(van), TimeOnly.Parse(tot)));
    }

    // ── Antwoordtekst ──

    [Fact]
    public void Antwoord_MetVensters_VermeldtHetGecontroleerdeDagdeel()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = true,
            GecontroleerdDagdeel = "ochtend",
            BeschikbareVensters = new List<BeschikbaarVenster> { Venster("09:00", "12:00") }
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("09:00 tot 12:00");
        body.Should().Contain("alleen de ochtend (08:30 - 12:00) gecontroleerd");
        body.Should().Contain("andere dagdelen zijn niet meegenomen");
    }

    [Fact]
    public void Antwoord_ZonderIetsVrijInHetDagdeel_MeldtDatExpliciet_ZonderAlternatievenUitAndereDagdelen()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            GecontroleerdDagdeel = "ochtend",
            BeschikbareVensters = new List<BeschikbaarVenster>(),
            Reden = "Geen beschikbare vensters op zaterdag 10 oktober."
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("in de ochtend (08:30 - 12:00) is helaas niets beschikbaar");
        body.Should().Contain("andere dagdelen zijn niet meegenomen");
        body.Should().NotContain("Beschikbare mogelijkheden").And.NotContain("Alternatieven");
        // De aanhef ("Goedemiddag") hangt van het tijdstip af; toets daarom op de dagdeelnaam mét lidwoord.
        body.Should().NotContain("de middag").And.NotContain("de avond");
    }

    [Fact]
    public void Antwoord_ZonderDagdeel_BlijftOngewijzigd()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = true,
            BeschikbareVensters = new List<BeschikbaarVenster> { Venster("09:00", "12:00") }
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie(null), MaakEmail(), ClubSettings);

        body.Should().NotContain("gecontroleerd").And.NotContain("dagdeel");
    }

    [Fact]
    public void Antwoord_TeamConflict_ZegtNietsOverEenDagdeel()
    {
        var response = new CheckAvailabilityResponse
        {
            TeamConflict = new TeamConflictInfo { Wedstrijd = "A - B", AanvangsTijd = "10:00", EindTijd = "11:30", VeldNaam = "veld 1" },
            Reden = "JO11-1 heeft al een wedstrijd op 10 oktober."
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().NotContain("dagdeel");
    }

    [Fact]
    public void MultiDatumAntwoord_VermeldtHetDagdeelEenKeer_EnMeldtLegeDagenPerDatum()
    {
        var metRuimte = new CheckAvailabilityResponse
        {
            Beschikbaar = true,
            GecontroleerdDagdeel = "ochtend",
            BeschikbareVensters = new List<BeschikbaarVenster> { Venster("09:00", "12:00") }
        };
        var leeg = new CheckAvailabilityResponse
        {
            GecontroleerdDagdeel = "ochtend",
            BeschikbareVensters = new List<BeschikbaarVenster>()
        };

        var (_, body) = BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            new List<(string, CheckAvailabilityResponse)> { ("2026-10-10", metRuimte), ("2026-10-17", leeg) },
            MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("In de ochtend (08:30 - 12:00) is helaas niets beschikbaar.");
        body.Split("alleen de ochtend (08:30 - 12:00) gecontroleerd").Length.Should().Be(2);
    }

    [Fact]
    public void HerplanGewensteDatum_MetDagdeel_VermeldtHetGecontroleerdeDagdeel()
    {
        var beschikbaarheid = new CheckAvailabilityResponse
        {
            Beschikbaar = true,
            GecontroleerdDagdeel = "middag",
            BeschikbareVensters = new List<BeschikbaarVenster> { Venster("13:00", "17:00") }
        };
        var wedstrijd = new ZoekWedstrijdResponse { Wedstrijd = "A - B", Datum = "2026-10-03", AanvangsTijd = "10:00", VeldNaam = "veld 2" };

        var (_, body) = BerichtResponseGenerator.BouwHerplanGewensteDatumAntwoord(
            wedstrijd, "2026-10-10", beschikbaarheid, MaakClassificatie("middag"), MaakEmail(), ClubSettings);

        body.Should().Contain("13:00 tot 17:00");
        body.Should().Contain("alleen de middag (12:00 - 17:00) gecontroleerd");
    }

    [Fact]
    public void TemplateAntwoord_KentDeDagdeelPlaceholder()
    {
        var template = new EmailTemplate("beschikbaarheid_check", "Re: {{dagdeel}}", "Gecontroleerd: {{dagdeel}}.");

        var (onderwerp, body) = BerichtResponseGenerator.BouwAangepasteAntwoord(
            template, MaakClassificatie("avond"), MaakEmail(), ClubSettings);

        onderwerp.Should().Be("Re: de avond (17:00 - 22:00)");
        body.Should().Contain("Gecontroleerd: de avond (17:00 - 22:00).");
    }
}
