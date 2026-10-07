using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>
/// De permanente trace bevat geen ruwe, door de AI uit de mail gehaalde tekst (#1568, Codex R1-F1).
/// Synthetische vrije tekst: een voornaam-achtig woord en een bijzin.
/// </summary>
public class TraceOpslagProjectieTests
{
    private const string VrijeTekst = "Pieter en zijn moeder Sanne komen zaterdag niet omdat het regent";
    private const string Club = "TESTCLUB";

    private static TraceBuilder Basis() => new TraceBuilder().Classificatie("BeschikbaarheidCheck", true, true, 1, true);

    private static BeslissingsTrace MetVrijeTekstAlsTeam()
        => Basis()
            .TeamHerkenning(TraceCodes.TeamHerkenning, VrijeTekst, "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TegenstanderHerkenning, VrijeTekst + " tegenstander", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.OpponentTeamHerkenning, VrijeTekst + " opponent", "MeerdereKandidaten", 0.5, new[] { "JO13-1", "MO13-1" }, null)
            .Bouw();

    [Fact]
    public void VrijeTekst_StaatNietInDeTransienteTrace_MaarWelInDePermanenteNiet()
    {
        var trace = MetVrijeTekstAlsTeam();

        trace.ToJson().Should().Contain("Pieter", "de transiënte trace (e-mailtester) toont wat de AI teruggaf");
        var opslag = trace.VoorOpslag().ToJson();
        opslag.Should().NotContain("Pieter").And.NotContain("Sanne").And.NotContain("regent").And.NotContain("ruweTekst");
    }

    [Fact]
    public void ViaEmailTraceRecord_KrijgtDeRepositoryGeenVrijeTekst()
    {
        var record = EmailTraceRecord.Van(1, Club, "BeschikbaarheidCheck", MetVrijeTekstAlsTeam(), "1.0.0.0");

        record.TraceJson.Should().NotContain("Pieter").And.NotContain("Sanne").And.NotContain("regent");
        record.TraceJson.Should().Contain("\"vorm\":", "het vormkenmerk vervangt de tekst");
    }

    [Fact]
    public void NegatieveControle_ZonderProjectieStaatDeTekstWel_InDeTrace()
    {
        // Bewijst dat de bovenstaande asserts iets betekenen: de ongeprojecteerde JSON bevat de tekst wél.
        var zonderProjectie = MetVrijeTekstAlsTeam().ToJson();
        zonderProjectie.Should().Contain("Pieter").And.Contain("ruweTekst");
    }

    [Fact]
    public void TeamSchrijfwijze_Onopgelost_KomtNietInDeTrace_MaarWelAlsVorm()
    {
        var trace = Basis().TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null).Bouw();

        var stap = trace.VoorOpslag().Stappen.Single(s => s.Code == TraceCodes.TeamHerkenning);

        stap.Details.Should().NotContainKey("ruweTekst");
        stap.Details["vorm"].Should().Be("6 tekens: letters+cijfers+streepje");
        stap.Details["bron"].Should().Be("Onopgelost");
        trace.VoorOpslag().ToJson().Should().NotContain("j10-04");
    }

    [Fact]
    public void OpgelosteAlias_BlijftZichtbaarAlsCanoniekeNaam_ZonderRuweTekst()
    {
        var trace = Basis()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "jo 10 4", "ExacteAlias", 1.0, new[] { "TESTCLUB O10-4" }, "TESTCLUB O10-4")
            .Bouw();

        var stap = trace.VoorOpslag().Stappen.Single(s => s.Code == TraceCodes.TeamHerkenning);

        stap.Uitkomst.Should().Be("Herkend als TESTCLUB O10-4");
        stap.Details["canoniekeNaam"].Should().Be("TESTCLUB O10-4");
        stap.Details["kandidaten"].Should().Be("TESTCLUB O10-4");
        stap.Details.Should().NotContainKey("ruweTekst");
        trace.VoorOpslag().ToJson().Should().NotContain("jo 10 4");
    }

    [Fact]
    public void TeamWissel_BewaartAlleenHetCanonieke_EnDeTegenstanderAlsVorm()
    {
        var trace = Basis().TeamWissel("TESTCLUB O13-2", VrijeTekst).Bouw();

        var stap = trace.VoorOpslag().Stappen.Single(s => s.Code == TraceCodes.TeamWissel);

        stap.Details["team"].Should().Be("TESTCLUB O13-2");
        stap.Details.Should().NotContainKey("tegenstander");
        stap.Details["tegenstanderVorm"].Should().Contain("tekens: letters+spaties");
        trace.VoorOpslag().ToJson().Should().NotContain("Pieter");
    }

    [Fact]
    public void OnbekendeDetailsleutel_WordtBijOpslagWeggelaten()
    {
        var trace = Basis()
            .Voeg(TraceCodes.Datum, "Datumverwerking", "Eén datum", ZekerheidsNiveau.Zeker, new KeyValuePair<string, string?>[]
            {
                new("aantalDatums", "1"), new("datums", "2026-10-07"), new("opmerking", VrijeTekst), new("afzender", "x")
            })
            .Bouw();

        var details = trace.VoorOpslag().Stappen.Single(s => s.Code == TraceCodes.Datum).Details;

        details.Keys.Should().BeEquivalentTo("aantalDatums", "datums");
        trace.VoorOpslag().ToJson().Should().NotContain("Pieter").And.NotContain("opmerking");
    }

    [Fact]
    public void WaardeDieNietInDeVerwachteVormPast_WordtWeggelaten()
    {
        var trace = Basis()
            .Voeg(TraceCodes.Datum, "Datumverwerking", VrijeTekst, ZekerheidsNiveau.Zeker, new KeyValuePair<string, string?>[]
            {
                new("aantalDatums", VrijeTekst), new("datums", "morgen na de training")
            })
            .Bouw();

        var stap = trace.VoorOpslag().Stappen.Single(s => s.Code == TraceCodes.Datum);

        stap.Details.Should().BeEmpty();
        stap.Uitkomst.Should().Be(TraceOpslagProjectie.Streepje);
    }

    [Fact]
    public void OnbekendeStapcode_WordtNietBewaard_EnTitelIsVast()
    {
        var trace = Basis()
            .Voeg("vrije-stap", VrijeTekst, VrijeTekst, ZekerheidsNiveau.Zeker, new KeyValuePair<string, string?>[] { new("x", VrijeTekst) })
            .Voeg(TraceCodes.Leermomenten, VrijeTekst, "Geen leermomenten meegegeven", ZekerheidsNiveau.Zeker)
            .Bouw();

        var opslag = trace.VoorOpslag();

        opslag.Stappen.Select(s => s.Code).Should().NotContain("vrije-stap");
        opslag.Stappen.Single(s => s.Code == TraceCodes.Leermomenten).Titel.Should().Be("Geleerde voorbeelden");
        opslag.ToJson().Should().NotContain("Pieter");
    }

    [Fact]
    public void RedenenInEindoordeel_DraagtGeenTekstDieDeProjectieWeglaat()
    {
        var trace = Basis().TeamHerkenning(TraceCodes.TeamHerkenning, VrijeTekst, "Onopgelost", 0, null, null)
            .Voeg(TraceCodes.Zekerheidspoort, "Zekerheidspoort", "Tegengehouden: antwoord niet naar de afzender", ZekerheidsNiveau.Onzeker,
                new KeyValuePair<string, string?>[] { new("redenen", VrijeTekst), new("poortActief", "ja") })
            .Bouw();

        var opslag = trace.VoorOpslag();

        opslag.Stappen.Single(s => s.Code == TraceCodes.Zekerheidspoort).Details["redenen"].Should().Be("Het eigen team is niet herkend");
        opslag.Oordeel.IsZeker.Should().Be(trace.Oordeel.IsZeker);
        opslag.ToJson().Should().NotContain("Pieter");
    }

    [Fact]
    public void BewaardeTrace_BlijftLeesbaar_EnBehoudtDeZekerheidsbeslissing()
    {
        var trace = Basis()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-1", "ExacteMatch", 1.0, new[] { "TESTCLUB O13-1" }, "TESTCLUB O13-1")
            .Antwoordkeuze("BeschikbaarheidCheck", """{"gewensteDatum":"x","beschikbaarheid":[]}""")
            .Bouw();

        var terug = BeslissingsTrace.FromJson(trace.VoorOpslag().ToJson());

        terug.Should().NotBeNull();
        terug!.Oordeel.IsZeker.Should().Be(trace.Oordeel.IsZeker);
        terug.Stappen.Select(s => s.Code).Should().Equal(trace.Stappen.Select(s => s.Code));
        terug.Stappen.Single(s => s.Code == TraceCodes.Sjabloon).Details["sjabloon"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ElkeAutomatischeStapcode_HeeftEenAllowlist_ZonderRuweTekstSleutels()
    {
        foreach (var code in TraceOpslagProjectie.ToegestaneStappen)
        {
            var sleutels = TraceOpslagProjectie.ToegestaneSleutels(code);
            sleutels.Should().NotContain(new[] { "ruweTekst", "tegenstander", "body", "onderwerp", "afzender" }, code);
        }
        TraceOpslagProjectie.ToegestaneSleutels("onbekend").Should().BeEmpty();
    }

    [Fact]
    public void Vormkenmerk_BevatGeenTekst()
    {
        TraceOpslagProjectie.Vormkenmerk("JO 13/2").Should().Be("7 tekens: letters+cijfers+spaties+schuine-streep");
        JsonDocument.Parse(MetVrijeTekstAlsTeam().VoorOpslag().ToJson()).Should().NotBeNull();
    }
}
