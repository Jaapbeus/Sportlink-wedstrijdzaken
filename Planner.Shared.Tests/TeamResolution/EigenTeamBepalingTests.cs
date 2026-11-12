using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;
using Xunit;

namespace Planner.Shared.Tests.TeamResolution;

/// <summary>
/// Gevallen van de gedeelde <see cref="EigenTeamBepaling"/> (#1716 slice 4): welke van de twee genoemde
/// teams het eigen team is. (Het eigen team uit een wedstrijd op een andere datum bestaat sinds #1861 niet meer.) Stond tot dan per tier als
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
        resolver.Verzoeken.Select(v => v.RuweTeamTekst).Should().Equal("JO13-2", "Tegenstander JO13-1");
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

        uitkomst.Should().Be(EigenTeamUitkomst.Handmatig);
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
    public async Task EigenTeam_StoringInResolver_VereistHandmatigeAfhandeling()
    {
        var resolver = new FakeResolver { ["JO13-2"] = Opgelost(EigenTeam), Gooit = "Kapot" };
        var trace = new TraceBuilder();

        var uitkomst = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "Kapot", "JO13-2", resolver, Club, NullLogger.Instance, trace);

        uitkomst.Should().Be(EigenTeamUitkomst.Handmatig);
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
    [InlineData("TESTCLUB")]
    [InlineData(" testclub - ")]
    public async Task BereidPlannerVoor_AlleenDeClubnaam_TeltNietAlsGenoemdTeam(string teamveld)
    {
        // #1861: het model vulde het teamveld met de eigen clubnaam uit een zin over een ander team.
        var classificatie = new Planner.Shared.Email.BerichtClassificatie
        {
            Type = Planner.Shared.Email.VerzoekType.BeschikbaarheidCheck, TeamNaam = teamveld,
        };
        var resolver = new FakeResolver();
        var trace = new TraceBuilder();

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(classificatie, resolver, Club, NullLogger.Instance, trace);

        herkend.Should().BeFalse();
        classificatie.TeamNaam.Should().BeNull();
        resolver.Verzoeken.Should().BeEmpty("een lege teamtekst wordt niet geresolved");
        trace.Stappen.Single(s => s.Code == TraceCodes.Classificatie).Details["teamGenoemd"].Should().Be("nee");
    }

    [Fact]
    public async Task BereidPlannerVoor_ClubnaamMetTeamaanduiding_BlijftStaan()
    {
        var classificatie = new Planner.Shared.Email.BerichtClassificatie
        {
            Type = Planner.Shared.Email.VerzoekType.BeschikbaarheidCheck, TeamNaam = "TESTCLUB JO13-2",
        };
        var resolver = new FakeResolver { ["TESTCLUB JO13-2"] = Opgelost(EigenTeam) };

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(classificatie, resolver, Club, NullLogger.Instance, null);

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be(EigenTeam);
        classificatie.LeeftijdsCategorie.Should().Be("JO13");
    }

    /// <summary>
    /// #1863: een herkend seniorenteam (eerste elftal, vrouwen, veteranen) heeft geen leeftijd in de naam en krijgt
    /// de speeltijd uit zijn Sportlink-categorie; een onbekende categorie blijft handmatige planning.
    /// </summary>
    [Theory]
    [InlineData("1", "TESTCLUB 1", "1-99", "1-99")]
    [InlineData("VR1", "TESTCLUB VR1", "VR", "VR")]
    [InlineData("35+1", "TESTCLUB 35+1", "1-99", "1-99")]
    [InlineData("Tegenstander 1", "TESTCLUB 1", "1-99", "1-99")]  // eigen team in het tegenstanderveld
    [InlineData("1", "TESTCLUB 1", null, null)]
    [InlineData("G1", "TESTCLUB G1", "1-99", null)]  // G-team: nooit seniorenduur, de planner gebruikt de naamsleutel G
    public async Task BereidPlannerVoor_Seniorenteam_KrijgtSpeelduurUitSportlinkCategorie(
        string genoemd, string canoniek, string? sportlink, string? verwacht)
    {
        var resolver = new FakeResolver { [canoniek] = new(5, canoniek, 1.0, [], ResolutionBron.ExacteMatch, sportlink) };
        var classificatie = genoemd.StartsWith("Tegenstander", StringComparison.Ordinal)
            ? new Planner.Shared.Email.BerichtClassificatie
            { Type = Planner.Shared.Email.VerzoekType.BeschikbaarheidCheck, TeamNaam = genoemd, Tegenstander = canoniek }
            : new Planner.Shared.Email.BerichtClassificatie
            { Type = Planner.Shared.Email.VerzoekType.BeschikbaarheidCheck, TeamNaam = canoniek };
        var speeltijden = new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase)
        {
            ["1-99"] = new() { Leeftijd = "1-99", WedstrijdTotaal = 115, Veldafmeting = 1m },
            ["VR"] = new() { Leeftijd = "VR", WedstrijdTotaal = 105, Veldafmeting = 1m },
        };

        (await EigenTeamBepaling.BereidPlannerVoorAsync(classificatie, resolver, Club, NullLogger.Instance, null)).Should().BeTrue();
        var handmatig = await Planner.Shared.Email.BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            sleutel => Task.FromResult(speeltijden.GetValueOrDefault(sleutel)), null);

        classificatie.TeamNaam.Should().Be(canoniek);
        classificatie.LeeftijdsCategorie.Should().Be(verwacht);
        if (verwacht is null) handmatig.Should().NotBeNull("zonder categorie blijft het handmatige planning");
        else handmatig.Should().BeNull();
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
