using AwesomeAssertions;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>Legt de zekerheidspoort vast (#1568 deel D): beslissing, trace-stap en vaste review-kop.</summary>
public class ZekerheidsPoortTests
{
    private static TraceBuilder OnbekendTeam()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Onopgelost", 0, null, null)
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"teamOnbekend\":true}");

    private static TraceBuilder HerkendTeam()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Alias", 1.0, null, "JO13-2")
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"beschikbaar\":true}");

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("onzin", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    public void IsActief_OntbrekendOfOnbekend_IsAan(string? waarde, bool verwacht)
        => ZekerheidsPoort.IsActief(waarde).Should().Be(verwacht);

    [Fact]
    public void OnbekendTeam_PoortAan_HoudtTegen_EnLegtDeReden_VastInDeTrace()
    {
        var trace = OnbekendTeam();

        var besluit = ZekerheidsPoort.Bepaal(true, trace);

        besluit.Tegenhouden.Should().BeTrue();
        besluit.Redenen.Should().NotBeEmpty();
        var stap = trace.Stappen.Should().ContainSingle(s => s.Code == TraceCodes.Zekerheidspoort).Subject;
        stap.Uitkomst.Should().StartWith("Tegengehouden");
        stap.Details["redenen"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void OnbekendTeam_PoortUit_LaatDoor_MaarMeldtHetInDeTrace()
    {
        var trace = OnbekendTeam();

        ZekerheidsPoort.Bepaal(false, trace).Tegenhouden.Should().BeFalse();

        var stap = trace.Stappen.Should().ContainSingle(s => s.Code == TraceCodes.Zekerheidspoort).Subject;
        stap.Details["poortActief"].Should().Be("nee");
    }

    [Fact]
    public void HerkendTeam_LaatDoor_ZonderPoortStap()
    {
        var trace = HerkendTeam();

        ZekerheidsPoort.Bepaal(true, trace).Tegenhouden.Should().BeFalse();
        trace.Stappen.Should().NotContain(s => s.Code == TraceCodes.Zekerheidspoort);
    }

    /// <summary>Na het aanmaken van een alias (deel C) wordt dezelfde mail herkend en dus zeker.</summary>
    [Fact]
    public void NaAliasAanmaken_IsDezelfdeMailZeker()
    {
        ZekerheidsPoort.Bepaal(true, OnbekendTeam()).Tegenhouden.Should().BeTrue();
        ZekerheidsPoort.Bepaal(true, HerkendTeam()).Tegenhouden.Should().BeFalse();
    }

    [Fact]
    public void MeerdereKandidaten_HoudtTegen()
    {
        var trace = new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "13-1", "MeerdereKandidaten", 0.5, new[] { "JO13-1", "MO13-1" }, null)
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"beschikbaar\":true}");

        ZekerheidsPoort.Bepaal(true, trace).Tegenhouden.Should().BeTrue();
    }

    private static TraceBuilder Herplan(string plannerResponse)
        => new TraceBuilder()
            .Classificatie("HerplanVerzoek", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Alias", 1.0, null, "JO13-2")
            .Antwoordkeuze("HerplanVerzoek", plannerResponse);

    /// <summary>M3: team herkend maar geen wedstrijd gevonden, of team/datum ontbreekt: een mens moet het zien.</summary>
    [Theory]
    [InlineData("{\"gevonden\":false,\"reden\":\"geen\"}", "geen-wedstrijd")]
    [InlineData("{\"error\":\"Onvoldoende gegevens voor herplanverzoek (team en datum nodig)\"}", "onvoldoende-gegevens")]
    public void Herplan_ZonderWedstrijdOfDatum_HoudtTegen(string plannerResponse, string uitkomst)
    {
        var trace = Herplan(plannerResponse);

        ZekerheidsPoort.Bepaal(true, trace).Tegenhouden.Should().BeTrue();
        trace.Stappen.Single(s => s.Code == TraceCodes.HerplanUitkomst).Details["uitkomst"].Should().Be(uitkomst);
        trace.Stappen.Single(s => s.Code == TraceCodes.HerplanUitkomst).Details["sjabloon"].Should().Be("herplan_verzoek");
    }

    /// <summary>Negatieve controle: elke geslaagde herplantak blijft Zeker en wordt dus niet tegengehouden.</summary>
    [Theory]
    [InlineData("{\"wedstrijd\":{\"wedstrijdcode\":1},\"herplanOpties\":{}}")]
    [InlineData("{\"wedstrijd\":{\"wedstrijdcode\":1},\"gewensteDatum\":\"2026-11-01\",\"beschikbaarheid\":{}}")]
    [InlineData("{\"herplanTeLaat\":true,\"wedstrijd\":{\"wedstrijdcode\":1},\"deadlineDagen\":8,\"dagenTotWedstrijd\":3}")]
    [InlineData("{\"verzetZonderDatum\":true,\"wedstrijd\":{\"wedstrijdcode\":1},\"vrijeZaterdagen\":[]}")]
    public void Herplan_GeslaagdeTakken_BlijvenZeker(string plannerResponse)
        => ZekerheidsPoort.Bepaal(true, Herplan(plannerResponse)).Tegenhouden.Should().BeFalse();

    [Fact]
    public void ZonderTrace_LaatDoor()
        => ZekerheidsPoort.Bepaal(true, null).Tegenhouden.Should().BeFalse();

    [Fact]
    public void PoortStap_BeinvloedtHetEindoordeelNiet()
    {
        var trace = OnbekendTeam();
        ZekerheidsPoort.Bepaal(true, trace);

        var eerste = ZekerheidsBeoordeling.Beoordeel(trace.Stappen);
        var bouw = trace.Bouw();

        bouw.Oordeel.IsZeker.Should().BeFalse();
        bouw.Oordeel.Redenen.Should().BeEquivalentTo(eerste.Redenen);
    }

    [Fact]
    public void ReviewKop_StaatBovenaan_ZonderTraceDetails()
    {
        var mail = ZekerheidsPoort.MetReviewKop("voorstel-body");

        mail.Should().StartWith("LET OP: dit antwoord is NIET naar de afzender verstuurd");
        mail.Should().EndWith("voorstel-body");
        ZekerheidsPoort.ReviewKop.Should().NotContain("@").And.NotContain("JO");
    }
}
