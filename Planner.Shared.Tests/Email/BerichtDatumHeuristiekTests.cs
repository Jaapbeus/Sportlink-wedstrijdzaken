using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// Legt de datumherkenning vast die bij #1716 uit beide <c>BerichtPipeline.cs</c>-bestanden naar
/// <see cref="BerichtDatumHeuristiek"/> is verhuisd. De gevallen zijn overgenomen uit
/// <c>FunctionApp.Tests/Processing/BerichtPipelineTests.cs</c> (tot #1716 alleen op de SQL Server-tier);
/// omdat de productietier dezelfde klasse aanroept, gelden ze nu voor beide tiers. Dat de tier-omhulsels
/// het resultaat terugschrijven, bewijzen <c>BerichtPipelineDatumTests</c> per tier.
/// </summary>
public class BerichtDatumHeuristiekTests
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

    private static string? Corrigeer(string? modelDatum, string body, string onderwerp)
        => BerichtDatumHeuristiek.CorrigeerDagDatum(modelDatum, body, onderwerp);

    // ── Datum in onderwerp ──

    [Fact]
    public void OnderwerpBevat_ddmmyyyy_GebruiktOnderwerpDatum()
        => Corrigeer(null, "Kan jullie op die datum?", "Beschikbaarheid 18-4-2026").Should().Be("2026-04-18");

    [Fact]
    public void OnderwerpBevat_dmaandyyyy_GebruiktOnderwerpDatum()
        => Corrigeer(null, "Tekst zonder datum", "Verzoek 9 mei 2026").Should().Be("2026-05-09");

    [Fact]
    public void OnderwerpBevat_dmaandZonderJaar_NeemtEerstvolgendVoorkomen()
    {
        // Een maandnaam zonder jaartal betekent "het eerstvolgende voorkomen". De testdatum is
        // relatief aan vandaag, zodat de test niet afhangt van de dag waarop hij draait.
        var doel = DateOnly.FromDateTime(DateTime.Today).AddDays(30);

        Corrigeer(null, "tekst", $"{doel.Day} {MaandNaam(doel)} beschikbaarheid")
            .Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void OnderwerpPrioriteit_BovenBody()
        => Corrigeer(null, "body 05-06-2026 iets", "onderwerp 18-4-2026").Should().Be("2026-04-18");

    // ── Datum in body ──

    [Fact]
    public void BodyBevat_ddmmyyyy_EnModelDatumLeeg_GebruiktBodyDatum()
        => Corrigeer(null, "We willen graag spelen op 12-5-2026.", "Hallo").Should().Be("2026-05-12");

    [Fact]
    public void BodyDatum_NietGebruikt_AlsModelDatumAlGevuld()
        => Corrigeer("2026-04-15", "body 12-5-2026", "geen datum in onderwerp").Should().Be("2026-04-15");

    // ── Dagnaamcorrectie ──

    [Fact]
    public void DagNaamZaterdagInTekst_CorrigeertNaarDichtsteZaterdag()
    {
        // 2026-04-14 is een dinsdag; de tekst zegt "zaterdag" → corrigeer naar de dichtstbijzijnde zaterdag
        var datum = DateOnly.Parse(Corrigeer("2026-04-14", "Kunnen we zaterdag inhalen?", "Verzoek")!);

        datum.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void DagNaamMatchtModelDatum_GeenWijziging()
        => Corrigeer("2026-04-18", "Kunnen we zaterdag spelen?", "Verzoek").Should().Be("2026-04-18");

    [Fact]
    public void GeenDatumEnGeenDagNaam_DatumBlijftLeeg()
        => Corrigeer(null, "Gewoon wat tekst zonder datum", "onderwerp").Should().BeNullOrEmpty();

    [Fact]
    public void OngeldigeDatumString_DatumOngewijzigd()
        => Corrigeer("geen-datum", "tekst", "onderwerp").Should().Be("geen-datum");

    [Fact]
    public void BeideDagNamenInTekst_DatumOngewijzigd()
    {
        // Zowel "zaterdag" als "zondag" in de tekst → ambigu, dus niet corrigeren. Eerder pakte de
        // lus de eerste dagnaam in arrayvolgorde (maandag→zondag); dat is geen keuze maar toeval.
        Corrigeer("2026-04-14", "zaterdag of zondag?", "onderwerp").Should().Be("2026-04-14");
    }

    // ── Randgevallen datum parsing ──

    [Fact]
    public void Patroon_1_1_2026_WordtCorrectGeparsed()
        => Corrigeer(null, "tekst", "wedstrijd 1-1-2026").Should().Be("2026-01-01");

    [Fact]
    public void MaandNaamDecember_WordtCorrectGeparsed()
        => Corrigeer(null, "tekst", "3 december 2025").Should().Be("2025-12-03");

    // ── Citaat-/ondertekeningsstaart (K1) ──

    [Fact]
    public void DagnaamInOutlookCitaat_GebruiktDagnaamUitEigenTekst()
    {
        // De afzender vraagt zaterdag; de Outlook-citaatkop bevat "dinsdag" (de verzenddatum van
        // het vorige bericht). Eerder won "dinsdag" omdat het eerder in de dagnamen-array staat.
        var body = "Ja zaterdag 6 juni kan bij ons!\r\n\r\n"
                 + "Van: [afzender]\r\n"
                 + "Verzonden: dinsdag 26 mei 2026 14:03\r\n"
                 + "Aan: [ontvanger]\r\n"
                 + "Onderwerp: oefenwedstrijd JO13-2";

        var datum = Corrigeer("2026-06-05", body, "Re: oefenwedstrijd JO13-2"); // vrijdag — model zat één dag mis

        datum.Should().Be("2026-06-06");
        DateOnly.Parse(datum!).DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void DatumInCitaatkop_WordtNietAlsBodyDatumGebruikt()
    {
        // "26 mei 2026" staat alleen in de citaatkop; de eigen tekst noemt 6 juni 2026.
        var body = "Ja, 6 juni 2026 kan bij ons!\r\n\r\n"
                 + "Van: [afzender]\r\n"
                 + "Verzonden: dinsdag 26 mei 2026 14:03\r\n"
                 + "Aan: [ontvanger]";

        Corrigeer(null, body, "Re: oefenwedstrijd JO13-2").Should().Be("2026-06-06");
    }

    [Fact]
    public void OndertekeningMetTweedeDagnaam_DatumOngewijzigd()
    {
        // Een ondertekening zonder citaatkop wordt niet afgekapt; de tweede dagnaam maakt de tekst
        // ambigu en dan blijft de modeldatum staan in plaats van naar vrijdag te schuiven.
        var body = "Kunnen we zaterdag spelen?\r\n\r\n"
                 + "Met vriendelijke groet\r\n"
                 + "De kantine is open vrijdag vanaf 19:00 uur";

        Corrigeer("2026-04-14", body, "Verzoek").Should().Be("2026-04-14");
    }

    [Fact]
    public void StripCitaatEnOndertekening_KniptBijEersteCitaatkop()
        => BerichtDatumHeuristiek.StripCitaatEnOndertekening("Eigen tekst\r\nVan: [afzender]\r\nVerzonden: later")
            .Should().Be("Eigen tekst\r\n");

    [Fact]
    public void StripCitaatEnOndertekening_VolledigCitaat_GeeftOrigineleTekstTerug()
        => BerichtDatumHeuristiek.StripCitaatEnOndertekening("Van: [afzender]\r\nAlleen citaat")
            .Should().Be("Van: [afzender]\r\nAlleen citaat");

    // ── Reply-thread vs. datum in onderwerp (H3) ──

    [Theory]
    [InlineData("Re: Oefenwedstrijd 30 mei")]
    [InlineData("RE: Oefenwedstrijd 30 mei")]
    [InlineData("Fwd: Oefenwedstrijd 30 mei")]
    [InlineData("FW: Oefenwedstrijd 30 mei")]
    [InlineData("AW: Oefenwedstrijd 30 mei")]
    [InlineData("Re: Fwd: Oefenwedstrijd 30 mei")]
    public void ReplyMetOudeDatumInOnderwerp_ModelDatumBlijftStaan(string onderwerp)
    {
        // In een reply-thread staat de oorspronkelijke datum in het onderwerp. De afzender vraagt
        // om een nieuwe datum; die van het model is dan actueler dan die uit het onderwerp.
        Corrigeer("2026-06-06", "30 mei kan niet, kan het 6 juni?", onderwerp).Should().Be("2026-06-06");
    }

    [Fact]
    public void OnderwerpZonderReplyPrefix_OverschrijftModelDatum()
        => Corrigeer("2026-06-06", "tekst zonder datum", "Oefenwedstrijd 18-4-2026").Should().Be("2026-04-18");

    [Fact]
    public void ReplyPrefixZonderModelDatum_GebruiktOnderwerpDatum()
        => Corrigeer(null, "tekst zonder datum", "Re: Oefenwedstrijd 18-4-2026").Should().Be("2026-04-18");

    // ── Maandnaam zonder jaartal (H4) ──

    [Fact]
    public void MaandnaamZonderJaarRuimInVerleden_LevertGeenDatumInHetVerleden()
    {
        // Winterstop-scenario: een mail in december over "10 januari" leverde eerder een datum van
        // elf maanden terug op, waarop de afzender "datum moet in de toekomst zijn" terugkreeg.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var ruimVerleden = new DateOnly(vandaag.Year, vandaag.Month, 1).AddMonths(-4);

        var resultaat = DateOnly.Parse(Corrigeer(
            null, "tekst", $"Beschikbaarheid {ruimVerleden.Day} {MaandNaam(ruimVerleden)}")!);

        resultaat.Month.Should().Be(ruimVerleden.Month);
        resultaat.Day.Should().Be(ruimVerleden.Day);
        (resultaat > vandaag).Should()
            .BeTrue($"een maandnaam zonder jaartal mag geen datum in het verleden opleveren (was {resultaat})");
    }

    // ── Afgekorte maandnamen (#722-analyse) ──
    //
    // De e-mailvariatie-analyse (#722) telde 117 waargenomen afgekorte maandnamen ("22 aug",
    // "24 mrt.") buiten de citaat-/ondertekeningstekst.

    /// <summary>
    /// Eerstvolgende toekomstige datum wiens afgekorte maandnaam AFWIJKT van de volledige vorm
    /// ("mei" is in het Nederlands identiek in beide vormen en zou de afkortings-regex dus
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
    public void OnderwerpBevat_AfgekorteMaandZonderPunt_WordtGeparsed()
    {
        var doel = EerstvolgendeDatumMetAfwijkendeAfkorting(30);

        Corrigeer(null, "tekst", $"Wij spelen graag op {doel.Day} {AfgekorteMaandNaam(doel)}")
            .Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void OnderwerpBevat_AfgekorteMaandMetPunt_WordtGeparsed()
    {
        var doel = EerstvolgendeDatumMetAfwijkendeAfkorting(30);

        Corrigeer(null, "tekst", $"Op {doel.Day} {AfgekorteMaandNaam(doel)}. om 20:36")
            .Should().Be(doel.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void AfgekorteMaandMetJaartal_GebruiktExpliciteJaar()
        => Corrigeer(null, "tekst", "Aanduiding 24 mrt 2026").Should().Be("2026-03-24");

    [Fact]
    public void AfgekorteMaandSept_WordtAlsSeptemberGeparsed()
        // "sept" is een tweede veelgebruikte afkorting naast "sep".
        => Corrigeer(null, "tekst", "Wedstrijd 14 sept 2026").Should().Be("2026-09-14");

    // ── Slash-notatie met jaartal (#722-analyse) ──
    //
    // De analyse telde 103 slash-datums; dd-mm zónder jaar via '/' blijft bewust ongesteund omdat
    // die vorm ambigu is met teamnotatie ("13/1").

    [Fact]
    public void SlashDatumMetJaartal_WordtGeparsed()
        => Corrigeer(null, "tekst", "Re: Thuisteam jo14 - Uitteam jo14 14/02/2026").Should().Be("2026-02-14");

    [Fact]
    public void SlashDatumZonderJaartal_LevertGeenDatumOp()
        // Bewuste keuze: '13/1' zonder jaartal is niet te onderscheiden van een teamaanduiding.
        => Corrigeer(null, "tekst", "Groet, -19/1").Should().BeNull();

    // ── ExpandDoordeweeksDatums (M2) ──

    [Fact]
    public void ExpandDoordeweeksDatums_ConcreteDatumInBericht_GeenExpansie()
    {
        // "doordeweeks, bijvoorbeeld woensdag 13 mei" is een concreet verzoek — dat mag niet door
        // vier andere dagen worden vervangen.
        var aiDatums = new List<string> { "2026-05-13" };

        var resultaat = BerichtDatumHeuristiek.ExpandDoordeweeksDatums(
            aiDatums, "Oefenwedstrijd JO13-2", "We kunnen alleen doordeweeks, bijvoorbeeld woensdag 13 mei");

        resultaat.Should().BeEquivalentTo(aiDatums);
    }

    [Fact]
    public void ExpandDoordeweeksDatums_ZonderConcreteDatum_GeeftVierToekomstigeDatums()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);

        var resultaat = BerichtDatumHeuristiek.ExpandDoordeweeksDatums(
            new List<string>(), "Oefenwedstrijd", "We kunnen alleen doordeweeks");

        resultaat.Should().HaveCount(4);
        resultaat.Should().OnlyContain(d => DateOnly.Parse(d) > vandaag);
    }

    [Fact]
    public void ExpandDoordeweeksDatums_ModelWeekMetVerstrekenDagen_LaatVerledenWeg()
    {
        // "deze week doordeweeks": op donderdag liggen maandag t/m woensdag al achter ons.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);

        var resultaat = BerichtDatumHeuristiek.ExpandDoordeweeksDatums(
            new List<string> { vandaag.ToString("yyyy-MM-dd") }, "Oefenwedstrijd", "Kan het doordeweeks?");

        resultaat.Should().NotContain(vandaag.ToString("yyyy-MM-dd"));
        resultaat.Where(d => DateOnly.Parse(d) <= vandaag).Should().BeEmpty();
    }

    [Fact]
    public void ExpandDoordeweeksDatums_ZonderDoordeweeks_GeeftModelDatumsOngewijzigd()
    {
        var aiDatums = new List<string> { "2026-05-13" };

        BerichtDatumHeuristiek.ExpandDoordeweeksDatums(aiDatums, "Oefenwedstrijd", "Kan het zaterdag?")
            .Should().BeSameAs(aiDatums);
    }
}
