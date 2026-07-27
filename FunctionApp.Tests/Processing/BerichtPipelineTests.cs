using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.Processing;

public class BerichtPipelineTests
{
    private static readonly string[] MaandNamen =
    {
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december"
    };

    private static string MaandNaam(DateOnly datum) => MaandNamen[datum.Month - 1];

    private static readonly string[] AfgekorteMaandNamen =
    {
        "jan", "feb", "mrt", "apr", "mei", "jun",
        "jul", "aug", "sep", "okt", "nov", "dec"
    };

    private static string AfgekorteMaandNaam(DateOnly datum) => AfgekorteMaandNamen[datum.Month - 1];

    // ── ValideerDagDatum — datum in onderwerp ──

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_ddmmyyyy_GebruiktOnderwerpDatum()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kan jullie op die datum?", "Beschikbaarheid 18-4-2026");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_dmaandyyyy_GebruiktOnderwerpDatum()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Tekst zonder datum", "Verzoek 9 mei 2026");
        classificatie.Datum.Should().Be("2026-05-09");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_dmaandZonderJaar_NeemtEerstvolgendVoorkomen()
    {
        // Een maandnaam zonder jaartal betekent "het eerstvolgende voorkomen". De testdatum is
        // relatief aan vandaag, zodat de test niet afhangt van de dag waarop hij draait.
        var doel = DateOnly.FromDateTime(DateTime.Today).AddDays(30);
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", $"{doel.Day} {MaandNaam(doel)} beschikbaarheid");

        classificatie.Datum.Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpPrioriteit_BovenBody()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "body 05-06-2026 iets", "onderwerp 18-4-2026");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    // ── ValideerDagDatum — datum in body ──

    [Fact]
    public void ValideerDagDatum_BodyBevat_ddmmyyyy_EnAiDatumLeeg_GebruiktBodyDatum()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = null
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "We willen graag spelen op 12-5-2026.", "Hallo");
        classificatie.Datum.Should().Be("2026-05-12");
    }

    [Fact]
    public void ValideerDagDatum_BodyDatum_NietGebruikt_AlsAiDatumAlGevuld()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-15"
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "body 12-5-2026", "geen datum in onderwerp");
        classificatie.Datum.Should().Be("2026-04-15");
    }

    // ── ValideerDagDatum — dag-naam correctie ──

    [Fact]
    public void ValideerDagDatum_DagNaamZaterdagInTekst_CorregeertNaarDichtsteZaterdag()
    {
        // 2026-04-14 is een dinsdag; de tekst zegt "zaterdag" → corrigeer naar 2026-04-18
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-14"  // dinsdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we zaterdag inhalen?", "Verzoek");
        var datum = DateOnly.Parse(classificatie.Datum!);
        datum.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void ValideerDagDatum_DagNaamMatchtAiDatum_GeenWijziging()
    {
        // 2026-04-18 is een zaterdag; tekst zegt "zaterdag" → ongewijzigd
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-18"  // zaterdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we zaterdag spelen?", "Verzoek");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_GeenDatumEnGeenDagNaam_DatumBlijftLeeg()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Gewoon wat tekst zonder datum", "onderwerp");
        classificatie.Datum.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ValideerDagDatum_OngeldigeDatumString_DatumOngewijzigd()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "geen-datum"
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "onderwerp");
        classificatie.Datum.Should().Be("geen-datum");
    }

    // ── ValideerDagDatum — tweestrijdige dag-namen ──

    [Fact]
    public void ValideerDagDatum_BeideDagNamenInTekst_DatumOngewijzigd()
    {
        // Zowel "zaterdag" als "zondag" in de tekst → ambigu, dus niet corrigeren. Eerder pakte de
        // lus de eerste dagnaam in arrayvolgorde (maandag→zondag); dat is geen keuze maar toeval.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-14"  // dinsdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "zaterdag of zondag?", "onderwerp");
        classificatie.Datum.Should().Be("2026-04-14");
    }

    // ── ValideerDagDatum — randgevallen datum parsing ──

    [Fact]
    public void ValideerDagDatum_Patroon_1_1_2026_WordtCorrectGeparsed()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "wedstrijd 1-1-2026");
        classificatie.Datum.Should().Be("2026-01-01");
    }

    [Fact]
    public void ValideerDagDatum_MaandNaamDecember_WordtCorrectGeparsed()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "3 december 2025");
        classificatie.Datum.Should().Be("2025-12-03");
    }

    // ── ValideerDagDatum — citaat-/ondertekeningsstaart (K1) ──

    [Fact]
    public void ValideerDagDatum_DagnaamInOutlookCitaat_GebruiktDagnaamUitEigenTekst()
    {
        // De afzender vraagt zaterdag; de Outlook-citaatkop bevat "dinsdag" (de verzenddatum van
        // het vorige bericht). Eerder won "dinsdag" omdat het eerder in de dagnamen-array staat.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-06-05"  // vrijdag — AI zat één dag mis
        };
        var body = "Ja zaterdag 6 juni kan bij ons!\r\n\r\n"
                 + "Van: [afzender]\r\n"
                 + "Verzonden: dinsdag 26 mei 2026 14:03\r\n"
                 + "Aan: [ontvanger]\r\n"
                 + "Onderwerp: oefenwedstrijd JO13-2";

        BerichtPipeline.ValideerDagDatum(classificatie, body, "Re: oefenwedstrijd JO13-2");

        classificatie.Datum.Should().Be("2026-06-06");
        DateOnly.Parse(classificatie.Datum!).DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void ValideerDagDatum_DatumInCitaatkop_WordtNietAlsBodyDatumGebruikt()
    {
        // "26 mei 2026" staat alleen in de citaatkop; de eigen tekst noemt 6 juni 2026.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = null
        };
        var body = "Ja, 6 juni 2026 kan bij ons!\r\n\r\n"
                 + "Van: [afzender]\r\n"
                 + "Verzonden: dinsdag 26 mei 2026 14:03\r\n"
                 + "Aan: [ontvanger]";

        BerichtPipeline.ValideerDagDatum(classificatie, body, "Re: oefenwedstrijd JO13-2");

        classificatie.Datum.Should().Be("2026-06-06");
    }

    [Fact]
    public void ValideerDagDatum_OndertekeningMetTweedeDagnaam_DatumOngewijzigd()
    {
        // Een ondertekening zonder citaatkop wordt niet afgekapt; de tweede dagnaam maakt de tekst
        // ambigu en dan blijft de AI-datum staan in plaats van naar vrijdag te schuiven.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-14"  // dinsdag
        };
        var body = "Kunnen we zaterdag spelen?\r\n\r\n"
                 + "Met vriendelijke groet\r\n"
                 + "De kantine is open vrijdag vanaf 19:00 uur";

        BerichtPipeline.ValideerDagDatum(classificatie, body, "Verzoek");

        classificatie.Datum.Should().Be("2026-04-14");
    }

    // ── ValideerDagDatum — reply-thread vs. datum in onderwerp (H3) ──

    [Theory]
    [InlineData("Re: Oefenwedstrijd 30 mei")]
    [InlineData("RE: Oefenwedstrijd 30 mei")]
    [InlineData("Fwd: Oefenwedstrijd 30 mei")]
    [InlineData("FW: Oefenwedstrijd 30 mei")]
    [InlineData("AW: Oefenwedstrijd 30 mei")]
    [InlineData("Re: Fwd: Oefenwedstrijd 30 mei")]
    public void ValideerDagDatum_ReplyMetOudeDatumInOnderwerp_AiDatumBlijftStaan(string onderwerp)
    {
        // In een reply-thread staat de oorspronkelijke datum in het onderwerp. De afzender vraagt
        // om een nieuwe datum; die van de AI is dan actueler dan die uit het onderwerp.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-06-06"
        };

        BerichtPipeline.ValideerDagDatum(classificatie, "30 mei kan niet, kan het 6 juni?", onderwerp);

        classificatie.Datum.Should().Be("2026-06-06");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpZonderReplyPrefix_OverschrijftAiDatum()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-06-06"
        };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst zonder datum", "Oefenwedstrijd 18-4-2026");

        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_ReplyPrefixZonderAiDatum_GebruiktOnderwerpDatum()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = null
        };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst zonder datum", "Re: Oefenwedstrijd 18-4-2026");

        classificatie.Datum.Should().Be("2026-04-18");
    }

    // ── ValideerDagDatum — maandnaam zonder jaartal (H4) ──

    [Fact]
    public void ValideerDagDatum_MaandnaamZonderJaarRuimInVerleden_LeverGeenDatumInHetVerleden()
    {
        // Winterstop-scenario: een mail in december over "10 januari" leverde eerder een datum van
        // elf maanden terug op, waarop de afzender "datum moet in de toekomst zijn" terugkreeg.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var ruimVerleden = new DateOnly(vandaag.Year, vandaag.Month, 1).AddMonths(-4);
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(
            classificatie, "tekst", $"Beschikbaarheid {ruimVerleden.Day} {MaandNaam(ruimVerleden)}");

        var resultaat = DateOnly.Parse(classificatie.Datum!);
        resultaat.Month.Should().Be(ruimVerleden.Month);
        resultaat.Day.Should().Be(ruimVerleden.Day);
        (resultaat > vandaag).Should()
            .BeTrue($"een maandnaam zonder jaartal mag geen datum in het verleden opleveren (was {resultaat})");
    }

    // ── ExtractExpliciteDatum — afgekorte maandnamen (#722-analyse) ──
    //
    // De e-mailvariatie-analyse (#722) telde 117 waargenomen afgekorte maandnamen ("22 aug",
    // "24 mrt.") buiten de citaat-/ondertekeningstekst, terwijl ExtractExpliciteDatum vóór deze fix
    // uitsluitend volledige maandnamen herkende.

    /// <summary>
    /// Eerstvolgende toekomstige datum wiens afgekorte maandnaam AFWIJKT van de volledige vorm
    /// ("mei" is in het Nederlands identiek in beide vormen en zou de nieuwe afkortings-regex dus
    /// niet daadwerkelijk testen — zonder deze guard slaagt de test in mei-achtige periodes om de
    /// verkeerde reden).
    /// </summary>
    private static DateOnly EerstvolgendeDatumMetAfwijkendeAfkorting(int dagenVooruit)
    {
        var doel = DateOnly.FromDateTime(DateTime.Today).AddDays(dagenVooruit);
        while (AfgekorteMaandNaam(doel) == MaandNaam(doel)) doel = doel.AddDays(30);
        return doel;
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_AfgekorteMaandZonderPunt_WordtGeparsed()
    {
        var doel = EerstvolgendeDatumMetAfwijkendeAfkorting(30);
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", $"Wij spelen graag op {doel.Day} {AfgekorteMaandNaam(doel)}");

        classificatie.Datum.Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_AfgekorteMaandMetPunt_WordtGeparsed()
    {
        var doel = EerstvolgendeDatumMetAfwijkendeAfkorting(30);
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", $"Op {doel.Day} {AfgekorteMaandNaam(doel)}. om 20:36");

        classificatie.Datum.Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void ValideerDagDatum_AfgekorteMaandMetJaartal_GebruiktExpliciteJaar()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "Aanduiding 24 mrt 2026");

        classificatie.Datum.Should().Be("2026-03-24");
    }

    [Fact]
    public void ValideerDagDatum_AfgekorteMaandSept_WordtAlsSeptemberGeparsed()
    {
        // "sept" is een tweede veelgebruikte afkorting naast "sep".
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "Wedstrijd 14 sept 2026");

        classificatie.Datum.Should().Be("2026-09-14");
    }

    // ── ExtractExpliciteDatum — slash-notatie met jaartal (#722-analyse) ──
    //
    // De analyse telde 103 slash-datums; dd-mm zónder jaar via '/' blijft bewust ongesteund omdat
    // die vorm ambigu is met teamnotatie ("13/1") — zie "Genomen besluiten" in de PR-body.

    [Fact]
    public void ValideerDagDatum_SlashDatumMetJaartal_WordtGeparsed()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "Re: Thuisteam jo14 - Uitteam jo14 14/02/2026");

        classificatie.Datum.Should().Be("2026-02-14");
    }

    [Fact]
    public void ValideerDagDatum_SlashDatumZonderJaartal_LevertGeenDatumOp()
    {
        // Bewuste keuze: '13/1' zonder jaartal is niet te onderscheiden van een teamaanduiding.
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "Groet, -19/1");

        classificatie.Datum.Should().BeNull();
    }

    // ── ExpandDoordeweeksDatums (M2) ──

    [Fact]
    public void ExpandDoordeweeksDatums_ConcreteDatumInBericht_GeenExpansie()
    {
        // "doordeweeks, bijvoorbeeld woensdag 13 mei" is een concreet verzoek — dat mag niet door
        // vier andere dagen worden vervangen.
        var aiDatums = new List<string> { "2026-05-13" };

        var resultaat = BerichtPipeline.ExpandDoordeweeksDatums(
            aiDatums, "Oefenwedstrijd JO13-2", "We kunnen alleen doordeweeks, bijvoorbeeld woensdag 13 mei");

        resultaat.Should().BeEquivalentTo(aiDatums);
    }

    [Fact]
    public void ExpandDoordeweeksDatums_ZonderConcreteDatum_GeeftVierToekomstigeDatums()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);

        var resultaat = BerichtPipeline.ExpandDoordeweeksDatums(
            new List<string>(), "Oefenwedstrijd", "We kunnen alleen doordeweeks");

        resultaat.Should().HaveCount(4);
        resultaat.Should().OnlyContain(d => DateOnly.Parse(d) > vandaag);
    }

    [Fact]
    public void ExpandDoordeweeksDatums_AiWeekMetVerstrekenDagen_LaatVerledenWeg()
    {
        // "deze week doordeweeks": op donderdag liggen maandag t/m woensdag al achter ons.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);

        var resultaat = BerichtPipeline.ExpandDoordeweeksDatums(
            new List<string> { vandaag.ToString("yyyy-MM-dd") }, "Oefenwedstrijd", "Kan het doordeweeks?");

        resultaat.Should().NotContain(vandaag.ToString("yyyy-MM-dd"));
        resultaat.Where(d => DateOnly.Parse(d) <= vandaag).Should().BeEmpty();
    }

    // ── Eén verzoek, één datum (M3) ──

    [Fact]
    public void KiesPrimaireDatum_LijstGevuld_NeemtEersteUitLijstNietDeAiDatum()
    {
        BerichtPipeline.KiesPrimaireDatum(new List<string> { "2026-06-06" }, "2026-05-30")
            .Should().Be("2026-06-06");
    }

    [Fact]
    public void KiesPrimaireDatum_LegeLijst_ValtTerugOpAiDatum()
    {
        BerichtPipeline.KiesPrimaireDatum(new List<string>(), "2026-05-30")
            .Should().Be("2026-05-30");
    }

    [Fact]
    public async Task VerwerkMetPlannerAsync_DatumsLijstWijktAfVanAiDatum_GebruiktDeLijstDatum()
    {
        // De datumlijst bevat een onparseerbare waarde terwijl classificatie.Datum wél geldig is.
        // Wordt de lijst gebruikt (zoals de tegenstander-lookup doet), dan is er geen bruikbare
        // datum en volgt datumOnbekend — geen plannercheck op een andere datum.
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-06-06",
            Datums = new List<string> { "geen-datum" },
            TeamNaam = "JO13-2"
        };
        var bericht = new InkomendBericht { Onderwerp = "Oefenwedstrijd", Body = "Kan dat?" };

        var json = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, bericht, NullLogger.Instance, new GeenTeamResolver());

        json.Should().Contain("datumOnbekend");
    }

    // ── Geen herkende datum: net antwoord i.p.v. interne foutstring (M4) ──

    [Fact]
    public async Task VerwerkMetPlannerAsync_BeschikbaarheidZonderDatum_GeeftDatumOnbekend()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            TeamNaam = "JO13-2"
        };
        var bericht = new InkomendBericht
        {
            Onderwerp = "Oefenwedstrijd",
            Body = "Kunnen we ergens in mei nog een oefenwedstrijd spelen?"
        };

        var json = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, bericht, NullLogger.Instance, new GeenTeamResolver());

        json.Should().Contain("datumOnbekend");
    }

    [Fact]
    public async Task BouwTemplateAntwoord_DatumOnbekend_VraagtOmDatumZonderInterneFoutstring()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            TeamNaam = "JO13-2"
        };
        var bericht = new InkomendBericht
        {
            Afzender = "trainer@voorbeeld.nl",
            AfzenderNaam = "Jan de Vries",
            Onderwerp = "Oefenwedstrijd",
            Body = "Kunnen we ergens in mei nog een oefenwedstrijd spelen?"
        };
        var clubSettings = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "TESTCLUB Veldplanner",
            CoordinatorNaam: null,
            CoordinatorFunctie: null,
            EmailVoetnoot: null,
            HerplanDeadlineDagen: null);

        var (onderwerp, body) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, "{\"datumOnbekend\":true}", bericht, null, clubSettings);

        onderwerp.Should().Be("Re: Oefenwedstrijd");
        body.Should().NotContain("Ongeldige datum");
        body.Should().Contain("Jan");
        body.Should().Contain("datum");
        body.Should().Contain("TESTCLUB Veldplanner");
    }

    // De clubCode-override uit #677 wordt nu bewezen in FunctionApp.Tests/TeamResolution/:
    // teamherkenning loopt sinds #700 volledig via TeamNaamNormalisatie en TeamResolver, en
    // BerichtPipeline.NormaliseerTeamNaam bestaat niet meer.

    /// <summary>
    /// Resolver die niets herkent. Sinds #700 is de resolver een verplichte afhankelijkheid van de
    /// pipeline; voor tests die niet over teamherkenning gaan is "herkent niets" het neutrale gedrag.
    /// </summary>
    private sealed class GeenTeamResolver : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(TeamResolutionResult.Onopgelost);
    }
}
