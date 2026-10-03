using AwesomeAssertions;
using Planner.Shared;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

/// <summary>
/// #1430: de automatische planner (<see cref="FieldScheduler"/>) en de handmatige conflictcontrole
/// (<see cref="PlanningConflictDetectie"/>) mogen niet uit elkaar lopen. Deze tests leggen dat vast
/// in beide richtingen: wat de planner zelf plaatst, keurt de detectie goed; wat de detectie afkeurt,
/// weigert de planner te plaatsen.
/// </summary>
public class PlannerConflictPariteitTests
{
    private const int Buffer = 15;

    private static readonly List<VeldInfo> TweeVelden =
    [
        new() { VeldNummer = 1, VeldNaam = "Veld 1", VeldType = "kunstgras" },
        new() { VeldNummer = 2, VeldNaam = "Veld 2", VeldType = "kunstgras" },
    ];

    private static readonly List<VeldBeschikbaarheidInfo> HeleDag =
    [
        new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(20, 0) },
        new() { VeldNummer = 2, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(20, 0) },
    ];

    private static Dictionary<string, (int bufferVoor, int bufferNa)> TeamBuffers() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = (0, 60),
            ["B"] = (45, 0),
            ["C"] = (30, 30),
            ["D"] = (5, 5),   // lager dan de algemene buffer: telt niet
        };

    private static PlanningSlot NaarSlot(IngeplandSlot s, Dictionary<string, (int bufferVoor, int bufferNa)> tb)
    {
        var team = s.TeamNaam!;
        tb.TryGetValue(team, out var b);
        return new PlanningSlot(team, $"Veld {s.VeldNummer}", s.VeldSubpositie, s.AanvangsTijd,
            (int)(s.EindTijd.ToTimeSpan() - s.AanvangsTijd.ToTimeSpan()).TotalMinutes,
            tb.ContainsKey(team) ? b.bufferVoor : null, tb.ContainsKey(team) ? b.bufferNa : null);
    }

    [Fact]
    public void PlanningVanDeAutomatischePlanner_LevertGeenEnkelConflictOp()
    {
        var tb = TeamBuffers();
        var scheduler = new FieldScheduler(HeleDag, TweeVelden, Buffer, tb);
        var opdrachten = new (string Team, decimal Fractie, int Duur)[]
        {
            ("A", 1.00m, 90), ("B", 0.50m, 70), ("C", 0.25m, 50), ("D", 0.50m, 60),
            ("E", 0.25m, 40), ("A", 0.50m, 60), ("B", 1.00m, 90), ("C", 0.50m, 60),
            ("F", 0.25m, 40), ("G", 0.25m, 40), ("D", 1.00m, 90), ("E", 0.50m, 60),
        };

        var geplaatst = new List<IngeplandSlot>();
        foreach (var (team, fractie, duur) in opdrachten)
        {
            tb.TryGetValue(team, out var b);
            var slot = scheduler.FindAndOccupyNextSlot(fractie, duur,
                PlanningBufferRegels.Effectief(Buffer, tb.ContainsKey(team) ? b.bufferVoor : null), team);
            slot.Should().NotBeNull($"er is de hele dag ruimte op twee velden voor {team}");
            geplaatst.Add(slot!);
        }

        PlanningConflictDetectie.Detecteer(geplaatst.Select(s => NaarSlot(s, tb)), Buffer)
            .Should().BeEmpty("wat de planner zelf plaatst, moet de handmatige controle goedkeuren");
    }

    [Fact]
    public void TeamBufferNa60_PlannerSchuiftOpvolgerDoor_EnDetectieKeurtDeHandmatigeZetAf()
    {
        // Het #1430-scenario op één veld: A 10:00–11:00 met BufferNa 60, B wil om 11:30.
        var tb = TeamBuffers();
        var eenVeld = TweeVelden.Take(1).ToList();
        var scheduler = new FieldScheduler(HeleDag, eenVeld, Buffer, tb);

        var a = scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.00m, 60, Buffer, "A");
        var e = scheduler.FindAndOccupyNearTime(new TimeOnly(11, 30), 1.00m, 60, Buffer, "E");

        a!.AanvangsTijd.Should().Be(new TimeOnly(10, 0));
        e!.AanvangsTijd.Should().Be(new TimeOnly(12, 0), "de planner houdt de 60 minuten BufferNa van A aan");

        PlanningConflictDetectie.Detecteer([NaarSlot(a, tb), NaarSlot(e, tb)], Buffer)
            .Should().BeEmpty("de plaatsing van de planner is geldig");

        var handmatig = NaarSlot(e, tb) with { Start = new TimeOnly(11, 30) };
        PlanningConflictDetectie.Detecteer([NaarSlot(a, tb), handmatig], Buffer)
            .Should().ContainSingle("dezelfde regel keurt de handmatige zet naar 11:30 af")
            .Which.VereisteBufferMinuten.Should().Be(60);
    }

    [Fact]
    public void FieldSchedulerBanenVanSubpositie_IsDeGedeeldeRegel()
    {
        foreach (var sub in new[] { "A1", "A2", "B1", "B2", "A", "B", "", null, "x" })
            FieldScheduler.BanenVanSubpositie(sub).Should().Equal(PlanningBufferRegels.BanenVanSubpositie(sub));
    }

    [Theory]
    [InlineData(15, null, 15)]
    [InlineData(15, 0, 15)]
    [InlineData(15, 10, 15)]
    [InlineData(15, 15, 15)]
    [InlineData(15, 60, 60)]
    public void Effectief_TeamregelTeltAlleenAlsHijGroterIs(int algemeen, int? team, int verwacht) =>
        PlanningBufferRegels.Effectief(algemeen, team).Should().Be(verwacht);
}
