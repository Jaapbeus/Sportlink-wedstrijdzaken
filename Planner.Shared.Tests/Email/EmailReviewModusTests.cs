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
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(" true", false)]
    [InlineData(null, false)]
    public void IsActief_AlleenDeWaardeTrue_ZetDeModusAan(string? waarde, bool verwacht)
        => EmailReviewModus.IsActief(waarde).Should().Be(verwacht);

    [Fact]
    public void InstellingNaam_IsDeOmgevingsvariabeleVanDeProcessor()
        => EmailReviewModus.InstellingNaam.Should().Be("EmailReviewMode");
}
