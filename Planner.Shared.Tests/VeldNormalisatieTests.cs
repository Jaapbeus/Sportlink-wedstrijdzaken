using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

public class VeldNormalisatieTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Veld 1", "veld 1")]
    [InlineData("  Veld 1  ", "veld 1")]
    [InlineData("Veld  1", "veld 1")]
    [InlineData("VELD 1 A", "veld 1 a")]
    public void Normaliseer_GeeftVerwachtResultaat(string? input, string verwacht)
        => VeldNormalisatie.Normaliseer(input).Should().Be(verwacht);
}
