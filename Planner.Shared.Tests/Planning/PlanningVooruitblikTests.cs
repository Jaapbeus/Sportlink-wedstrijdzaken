using AwesomeAssertions;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

public class PlanningVooruitblikTests
{
    [Theory]
    [InlineData("2026-11-16", "2026-11-21")]
    [InlineData("2026-11-21", "2026-11-21")]
    [InlineData("2026-11-22", "2026-11-28")]
    [InlineData("2026-12-31", "2027-01-02")]
    public void VierZaterdagen_OokRondJaarwisseling(string vanaf, string eerste)
    {
        var dagen = PlanningVooruitblik.ZaterdagenVanaf(DateOnly.Parse(vanaf));
        dagen.Should().HaveCount(4).And.OnlyContain(d => d.DayOfWeek == DayOfWeek.Saturday);
        dagen[0].Should().Be(DateOnly.Parse(eerste));
        dagen[3].Should().Be(dagen[0].AddDays(21));
    }

    [Fact]
    public void Timer_KentOokZaterdagenZonderGesynchroniseerdeWedstrijden_EnBegrenstOverigeDagen()
    {
        var start = new DateOnly(2026, 11, 16);
        var dagen = PlanningVooruitblik.VoorbereidDatums(start,
            new[] { start.AddDays(-1), start.AddDays(50), start, start.AddDays(5), start });
        dagen.Take(4).Should().Equal(PlanningVooruitblik.ZaterdagenVanaf(start));
        dagen.Should().HaveCount(5).And.EndWith(start);
    }

    [Fact]
    public async Task BekendeNummers_VragenAlleenOnbekendeOfTegenstrijdigeSleutelsOp()
    {
        var opvragen = new List<string>();
        var haal = VeldplannerBekendeNummers.Opvrager(new[]
        {
            new EigenSleutel("bekend", 42), new EigenSleutel("bekend", 42),
            new EigenSleutel("dubbel", 1), new EigenSleutel("dubbel", 2),
            new EigenSleutel("zonder", null), new EigenSleutel(null, 5)
        }, id => { opvragen.Add(id); return Task.FromResult<long?>(99); });
        (await haal("bekend")).Should().Be(42);
        foreach (var id in new[] { "dubbel", "zonder", "onbekend", "BEKEND" }) await haal(id);
        opvragen.Should().Equal("dubbel", "zonder", "onbekend", "BEKEND");
    }
}
