using FluentAssertions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public class TeamResolverModeTests
{
    [Theory]
    [InlineData("shadow", TeamResolverMode.Shadow)]
    [InlineData("SHADOW", TeamResolverMode.Shadow)]
    [InlineData(" Shadow ", TeamResolverMode.Shadow)]
    [InlineData("on", TeamResolverMode.On)]
    [InlineData("true", TeamResolverMode.On)]
    [InlineData("1", TeamResolverMode.On)]
    public void Lees_BekendeWaarden(string waarde, TeamResolverMode verwacht)
    {
        TeamResolverModeReader.Lees(waarde).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("off")]
    [InlineData("shadowmode")]  // typefout mag nooit stilzwijgend iets activeren
    [InlineData("ja")]
    public void Lees_OnbekendeOfLegeWaarde_IsAltijdOff(string? waarde)
    {
        TeamResolverModeReader.Lees(waarde).Should().Be(TeamResolverMode.Off);
    }
}
