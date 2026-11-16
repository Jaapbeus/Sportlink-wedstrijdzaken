using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public class GanttSelectieStateTests
{
    private sealed record Wedstrijd(long Code, string Status);

    [Fact]
    public void SelectieBlijftBestaanEnWordtNietDoorEenAndereWedstrijdGemarkeerd()
    {
        var state = new GanttSelectieState();
        state.Selecteer(42);

        state.IsGeselecteerd(42).Should().BeTrue();
        state.IsGeselecteerd(43).Should().BeFalse();
        state.WedstrijdCode.Should().Be(42);
    }

    [Fact]
    public void VoegtGeselecteerdeWedstrijdTijdelijkToeZonderFilterTeWijzigen()
    {
        var state = new GanttSelectieState();
        var all = new[] { new Wedstrijd(1, "ongewijzigd"), new Wedstrijd(2, "wijziging") };
        var filtered = new[] { all[0] };
        state.Selecteer(2);

        state.VoegGeselecteerdeToe(filtered, all, item => item.Code)
            .Should().Equal(all);
        state.IsGeselecteerd(2).Should().BeTrue();
    }

    [Fact]
    public void SelectieDieAlInDeFilterZitWordtNietDubbelToegevoegd()
    {
        var state = new GanttSelectieState();
        var all = new[] { new Wedstrijd(1, "wijziging") };
        state.Selecteer(1);

        state.VoegGeselecteerdeToe(all, all, item => item.Code).Should().Equal(all);
    }
}
