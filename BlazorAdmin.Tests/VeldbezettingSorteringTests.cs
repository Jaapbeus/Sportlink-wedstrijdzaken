using BlazorAdmin.Models;
using BlazorAdmin.Services;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class VeldbezettingSorteringTests
{
    [Fact]
    public void SorteerVeldbezetting_SorteertOpTijdEnDaarnaOpBasisveldEnSubpositie()
    {
        var wedstrijden = new[]
        {
            Wedstrijd("09:00", "veld 1"),
            Wedstrijd("08:30", "veld 5 B"),
            Wedstrijd("08:30", "veld 4 B2"),
            Wedstrijd("08:30", "veld 4 A1"),
            Wedstrijd("08:30", "veld 2"),
            Wedstrijd("08:30", "veld 4 B1"),
            Wedstrijd("08:30", "veld 5 A"),
        };

        var resultaat = DagplanningWeergaveHelpers.SorteerVeldbezetting(wedstrijden)
            .Select(w => w.Veld)
            .ToArray();

        resultaat.Should().Equal("veld 2", "veld 4 A1", "veld 4 B1", "veld 4 B2", "veld 5 A", "veld 5 B", "veld 1");
    }

    [Fact]
    public void SorteerVeldbezetting_PlaatstOngeldigeTijdenEnVeldenAchteraan()
    {
        var wedstrijden = new[]
        {
            Wedstrijd("onbekend", "veld 1"),
            Wedstrijd("08:30", null),
            Wedstrijd("08:30", "veld 2"),
        };

        var resultaat = DagplanningWeergaveHelpers.SorteerVeldbezetting(wedstrijden)
            .Select(w => w.Veld ?? "geen veld")
            .ToArray();

        resultaat.Should().Equal("veld 2", "geen veld", "veld 1");
    }

    private static VeldbezettingItemDto Wedstrijd(string? tijd, string? veld) => new()
    {
        AanvangsTijd = tijd,
        Veld = veld,
        TeamNaam = "Team"
    };
}
