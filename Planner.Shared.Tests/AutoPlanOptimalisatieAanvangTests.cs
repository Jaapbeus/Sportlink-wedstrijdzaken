using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

public class AutoPlanOptimalisatieAanvangTests
{
    private static Dictionary<string, Speeltijd> Speeltijden() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["JO9"] = new()
        {
            Leeftijd = "JO9", Veldafmeting = 1m, WedstrijdTotaal = 75,
            StandaardVoorkeurTijd = new TimeOnly(11, 0)
        }
    };

    private static PlanDoel Bepaal(TimeOnly? clubStart, string? teamTijd = null) =>
        AutoPlanRegels.BepaalPlanDoel(
            "TESTCLUB JO9-1", "JO9", false,
            new Dictionary<string, TeamVoorkeurVeld>(),
            teamTijd is null
                ? new Dictionary<string, List<(TimeOnly, int)>>()
                : new Dictionary<string, List<(TimeOnly, int)>>
                    { ["JO9-1"] = [(TimeOnly.Parse(teamTijd), 1)] },
            Speeltijden(), "TESTCLUB", clubStart);

    [Fact]
    public void ClubtijdIsBasisOokAlsCategorieStandaardLaterIs()
    {
        var doel = Bepaal(new TimeOnly(8, 0));
        doel.DoelTijd.Should().Be(new TimeOnly(8, 0));
        doel.OndergrensTijd.Should().Be(new TimeOnly(8, 0));
        doel.Bron.Should().Be("club");
    }

    [Fact]
    public void LatereTeamvoorkeurBlijftDoelNaClubtijd()
    {
        var doel = Bepaal(new TimeOnly(8, 0), "12:00");
        doel.DoelTijd.Should().Be(new TimeOnly(12, 0));
        doel.OndergrensTijd.Should().Be(new TimeOnly(8, 0));
        doel.Bron.Should().Be("team");
    }

    [Fact]
    public void EerdereTeamvoorkeurWordtBegrensdDoorClubtijd()
    {
        var doel = Bepaal(new TimeOnly(8, 0), "07:30");
        doel.DoelTijd.Should().Be(new TimeOnly(8, 0));
        doel.OndergrensTijd.Should().Be(new TimeOnly(8, 0));
    }

    [Fact]
    public void CategorieStandaardtijdBlijftFallbackAlsClubtijdLeegIs()
    {
        var doel = Bepaal(null);
        doel.DoelTijd.Should().Be(new TimeOnly(11, 0));
        doel.OndergrensTijd.Should().BeNull();
        doel.Bron.Should().Be("leeftijd");
    }
}

public class FieldSchedulerClubStartTests
{
    private static FieldScheduler Scheduler(TimeOnly open) => new(
        [new() { VeldNummer = 1, BeschikbaarVanaf = open, BeschikbaarTot = new TimeOnly(13, 0) }],
        [new() { VeldNummer = 1, VeldNaam = "veld 1" }], 0);

    [Fact]
    public void ClubstartBegrenstVroegeDoelTijdEnVeldOpeningBlijftLeidend()
    {
        var scheduler = Scheduler(new TimeOnly(8, 0));
        var slot = scheduler.FindAndOccupyNearTime(new TimeOnly(7, 30), 1m, 60,
            teamNaam: "TESTCLUB JO9-1", ondergrens: new TimeOnly(8, 0));
        slot!.AanvangsTijd.Should().Be(new TimeOnly(8, 0));
    }

    [Fact]
    public void LatereTeamvoorkeurBlijftDoelMetClubstart()
    {
        var scheduler = Scheduler(new TimeOnly(8, 0));
        var slot = scheduler.FindAndOccupyNearTime(new TimeOnly(8, 30), 1m, 60,
            teamNaam: "TESTCLUB JO9-1", ondergrens: new TimeOnly(8, 0));
        slot!.AanvangsTijd.Should().Be(new TimeOnly(8, 30));
    }

    [Fact]
    public void BuitenVeldBeschikbaarheidValtTerugOpClubtijdOokVoorNegenUur()
    {
        var scheduler = new FieldScheduler(
            [new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(9, 0) }],
            [new() { VeldNummer = 1, VeldNaam = "veld 1" }], 0);
        var slot = scheduler.FindAndOccupyNearTime(new TimeOnly(12, 0), 1m, 60,
            teamNaam: "TESTCLUB JO9-1", ondergrens: new TimeOnly(8, 0));
        slot!.AanvangsTijd.Should().Be(new TimeOnly(8, 0));
    }

    [Fact]
    public void ZonderClubtijdBlijftNegenUurDeBestaandeTerugvalgrens()
    {
        var scheduler = new FieldScheduler(
            [new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(9, 0) }],
            [new() { VeldNummer = 1, VeldNaam = "veld 1" }], 0);
        scheduler.FindAndOccupyNearTime(new TimeOnly(12, 0), 1m, 60,
            teamNaam: "TESTCLUB JO9-1").Should().BeNull();
    }

    [Fact]
    public void VeldOpeningNaClubstartWordtNietVervroegd()
    {
        var scheduler = Scheduler(new TimeOnly(8, 15));
        var slot = scheduler.FindAndOccupyNearTime(new TimeOnly(8, 0), 1m, 60,
            teamNaam: "TESTCLUB JO9-1", ondergrens: new TimeOnly(8, 0));
        slot!.AanvangsTijd.Should().Be(new TimeOnly(8, 15));
    }
}
