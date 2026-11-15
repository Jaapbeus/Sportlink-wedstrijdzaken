using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1960: teambuffers worden gevonden via dezelfde teamsleutel als voorkeursveld en voorkeurstijd
/// (<see cref="TeamSleutelLookup"/>), niet alleen op de exacte schrijfwijze.
/// </summary>
public class AutoPlanTeamBuffersTests
{
    private static Dictionary<string, (int bufferVoor, int bufferNa)> Regels(params (string Team, int Voor, int Na)[] regels) =>
        regels.ToDictionary(r => r.Team, r => (r.Voor, r.Na), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void RegelZonderClubprefix_RaaktWedstrijdteamMetClubprefix()
    {
        var buffers = AutoPlanRegels.TeamBuffersVoorWedstrijden(
            ["ALLSTARS 1"], Regels(("1", 60, 30)), "ALLSTARS");

        buffers.Should().ContainKey("ALLSTARS 1").WhoseValue.Should().Be((60, 30));
    }

    [Fact]
    public void ExacteSchrijfwijze_BlijftWerken()
    {
        var buffers = AutoPlanRegels.TeamBuffersVoorWedstrijden(
            ["ALLSTARS 1"], Regels(("ALLSTARS 1", 60, 30)), "ALLSTARS");

        buffers["ALLSTARS 1"].Should().Be((60, 30));
    }

    [Fact]
    public void TeamZonderRegel_KrijgtGeenBuffer()
    {
        var buffers = AutoPlanRegels.TeamBuffersVoorWedstrijden(
            ["ALLSTARS JO13-1", null, ""], Regels(("1", 60, 30)), "ALLSTARS");

        buffers.Should().BeEmpty();
    }
}
