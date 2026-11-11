using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>#1583: één lezer van <c>EmailReviewMode</c> voor processor en tester.</summary>
public class EmailReviewModusTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData(" false ", true)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("nee", true)]
    [InlineData(null, true)]
    public void IsActief_FailSafe_AlleenExplicietFalseZetDeModusUit(string? waarde, bool verwacht)
        => EmailReviewModus.IsActief(waarde).Should().Be(verwacht);

    [Fact]
    public void InstellingNaam_IsDeOmgevingsvariabeleVanDeProcessor()
        => EmailReviewModus.InstellingNaam.Should().Be("EmailReviewMode");
}
