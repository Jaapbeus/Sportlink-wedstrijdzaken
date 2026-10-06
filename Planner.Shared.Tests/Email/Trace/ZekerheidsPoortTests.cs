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
