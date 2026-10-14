using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;
using Xunit;

namespace Planner.Shared.Tests.TeamResolution;

/// <summary>
/// Gevallen van de gedeelde <see cref="EigenTeamBepaling"/> (#1716 slice 4): welke van de twee genoemde
/// teams het eigen team is, en het eigen team uit een gevonden wedstrijd. Stond tot dan per tier als
/// private methoden in <c>BerichtPipeline</c>; omdat beide databasetiers nu deze ene klasse gebruiken,
/// gelden deze gevallen ook voor de productietier. Per tier blijft alleen een test van het omhulsel
/// (<c>BerichtPipelineEigenTeamTests</c> in beide tiertestprojecten).
/// </summary>
public class EigenTeamBepalingTests
{
    private const string Club = "TESTCLUB";
    private const string EigenTeam = "TESTCLUB JO13-2";

    private static TeamResolutionResult Opgelost(string naam) =>
        new(42, naam, 1.0, [], ResolutionBron.ExacteAlias);

    private static TeamResolutionResult Meerdere() =>
        new(null, null, 0.0, [new TeamCandidate(1, "TESTCLUB JO13-1", "JO13"), new TeamCandidate(2, "TESTCLUB MO13-1", "MO13")],
            ResolutionBron.MeerdereKandidaten);

    [Fact]
    public async Task EigenTeam_LegeClubCode_SlaatHerkenningOverZonderResolver()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam) };
        var trace = new TraceBuilder();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "JO13-2", "Tegenstander JO13-1", resolver, "  ", NullLogger.Instance, trace);

        uitkomst.Should().Be(EigenTeamUitkomst.NietHerkend);
        resolver.Verzoeken.Should().BeEmpty();
        trace.Stappen.Should().ContainSingle()
            .Which.Should().Match<TraceStap>(s => s.Code == TraceCodes.TeamHerkenning && s.Uitkomst == "Overgeslagen");
    }

    [Fact]
    public async Task EigenTeam_GenoemdTeamHerkend_GeeftCanoniekeNaamEnLaatTegenstanderOngemoeid()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam), ["Tegenstander JO13-1"] = Opgelost("NIET GEBRUIKEN") };

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "  JO13-2 ", " Tegenstander JO13-1 ", resolver, Club, NullLogger.Instance, null);

        uitkomst.Herkend.Should().BeTrue();
        uitkomst.TeamNaam.Should().Be(EigenTeam);
        uitkomst.Tegenstander.Should().Be(" Tegenstander JO13-1 ", "de tegenstander blijft letterlijk staan als het genoemde team herkend is");
        resolver.Verzoeken.Should().ContainSingle()
            .Which.Should().Be(new TeamResolutionRequest("JO13-2", null, null, Club));
    }

    [Fact]
    public async Task EigenTeam_TeamInTegenstanderveld_WisseltTeamEnTegenstander()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam) };
        var trace = new TraceBuilder();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            " Tegenstander JO13-1 ", "JO13-2", resolver, Club, NullLogger.Instance, trace);

        uitkomst.Should().Be(new EigenTeamUitkomst(true, EigenTeam, "Tegenstander JO13-1"));
        resolver.Verzoeken.Select(v => v.RuweTeamTekst).Should().Equal("Tegenstander JO13-1", "JO13-2");
        trace.Stappen.Select(s => s.Code).Should().Equal(
            TraceCodes.TeamHerkenning, TraceCodes.TegenstanderHerkenning, TraceCodes.TeamWissel);
        trace.Stappen[2].Details["team"].Should().Be(EigenTeam);
        trace.Stappen[2].Details["tegenstander"].Should().Be("Tegenstander JO13-1");
    }

    [Fact]
    public async Task EigenTeam_MeerdereKandidaten_KiestNietsEnGokNooit()
    {
        var resolver = new FakeResolver { ["13-1"] = Meerdere() };
        var trace = new TraceBuilder();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "13-1", "Tegenstander JO13-1", resolver, Club, NullLogger.Instance, trace);

        uitkomst.Should().Be(EigenTeamUitkomst.NietHerkend);
        trace.Stappen[0].Uitkomst.Should().Be("Meerdere kandidaten, niets gekozen");
        trace.Stappen[0].Details["aantalKandidaten"].Should().Be("2");
    }

    [Fact]
    public async Task EigenTeam_GeenVanBeideHerkend_GeeftNietHerkend()
    {
        var resolver = new FakeResolver();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "Onbekend JO9-1", "Ook onbekend", resolver, Club, NullLogger.Instance, null);

        uitkomst.Should().Be(EigenTeamUitkomst.NietHerkend);
        resolver.Verzoeken.Should().HaveCount(2);
    }

    [Fact]
    public async Task EigenTeam_LeegTeam_VraagtResolverNietMaarProbeertTegenstander()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam) };

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            null, "JO13-2", resolver, Club, NullLogger.Instance, null);

        uitkomst.Should().Be(new EigenTeamUitkomst(true, EigenTeam, ""));
        resolver.Verzoeken.Select(v => v.RuweTeamTekst).Should().Equal("JO13-2");
    }

    [Fact]
    public async Task EigenTeam_StoringInResolver_TeltAlsNietHerkendEnGaatDoorNaarTegenstander()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam), Gooit = "Kapot" };
        var trace = new TraceBuilder();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "Kapot", "JO13-2", resolver, Club, NullLogger.Instance, trace);

        uitkomst.Should().Be(new EigenTeamUitkomst(true, EigenTeam, "Kapot"));
        trace.Stappen[0].Uitkomst.Should().Be("Resolutie mislukt");
        trace.Stappen[0].Details["bron"].Should().BeEmpty();
    }

    [Fact]
    public async Task EigenTeam_TraceLegtBronEnCanoniekeNaamVast()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam) };
        var trace = new TraceBuilder();

        await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync("JO13-2", null, resolver, Club, NullLogger.Instance, trace);

        var stap = trace.Stappen.Should().ContainSingle().Subject;
        stap.Code.Should().Be(TraceCodes.TeamHerkenning);
        stap.Details["bron"].Should().Be(nameof(ResolutionBron.ExacteAlias));
        stap.Details["canoniekeNaam"].Should().Be(EigenTeam);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task UitWedstrijd_LegeWedstrijd_GeeftNullZonderResolver(string? wedstrijd)
    {
        var resolver = new FakeResolver { [""] = Opgelost(EigenTeam) };

        var naam = await EigenTeamBepaling.BepaalEigenTeamUitWedstrijdAsync(wedstrijd, resolver, Club, NullLogger.Instance, null);

        naam.Should().BeNull();
        resolver.Verzoeken.Should().BeEmpty();
    }

    [Fact]
    public async Task UitWedstrijd_EigenTeamAanUitkant_GeeftCanoniekeNaamVanGetrimdeKant()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam) };
        var trace = new TraceBuilder();

        var naam = await EigenTeamBepaling.BepaalEigenTeamUitWedstrijdAsync(
            "Tegenstander JO13-1 -  JO13-2 ", resolver, Club, NullLogger.Instance, trace);

        naam.Should().Be(EigenTeam);
        resolver.Verzoeken.Select(v => v.RuweTeamTekst).Should().Equal("Tegenstander JO13-1", "JO13-2");
        trace.Stappen.Should().OnlyContain(s => s.Code == TraceCodes.OpponentTeamHerkenning);
    }

    [Fact]
    public async Task UitWedstrijd_EersteKantHerkend_StoptDaar()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam), ["Tegenstander JO13-1"] = Opgelost("NIET GEBRUIKEN") };

        var naam = await EigenTeamBepaling.BepaalEigenTeamUitWedstrijdAsync(
            "JO13-2 - Tegenstander JO13-1", resolver, Club, NullLogger.Instance, null);

        naam.Should().Be(EigenTeam);
        resolver.Verzoeken.Should().ContainSingle();
    }

    [Fact]
    public async Task UitWedstrijd_SplitstAlleenOpHetEersteStreepje()
    {
        var resolver = new FakeResolver { ["JO13-2 - Reserve"] = Opgelost(EigenTeam) };

        var naam = await EigenTeamBepaling.BepaalEigenTeamUitWedstrijdAsync(
            "Tegenstander JO13-1 - JO13-2 - Reserve", resolver, Club, NullLogger.Instance, null);

        naam.Should().Be(EigenTeam);
    }

    [Fact]
    public async Task UitWedstrijd_GeenKantHerkendOfMeerdereKandidaten_GeeftNull()
    {
        var resolver = new FakeResolver { ["13-1"] = Meerdere() };

        var naam = await EigenTeamBepaling.BepaalEigenTeamUitWedstrijdAsync(
            "13-1 - Tegenstander JO13-1", resolver, Club, NullLogger.Instance, null);

        naam.Should().BeNull();
        resolver.Verzoeken.Should().HaveCount(2);
    }

    /// <summary>Resolver met vaste antwoorden per (getrimde) ruwe tekst; onbekend = Onopgelost.</summary>
    private sealed class FakeResolver : Dictionary<string, TeamResolutionResult>, ITeamResolver
    {
        public List<TeamResolutionRequest> Verzoeken { get; } = [];
        public string? Gooit { get; init; }

        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
        {
            Verzoeken.Add(request);
            if (request.RuweTeamTekst == Gooit) throw new InvalidOperationException("storing (test)");
            return Task.FromResult(TryGetValue(request.RuweTeamTekst, out var r) ? r : TeamResolutionResult.Onopgelost);
        }
    }
}
