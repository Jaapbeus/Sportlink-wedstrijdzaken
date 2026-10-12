using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

public class LikePatroonTests
{
    [Theory]
    [InlineData("a%b", "a\\%b")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a[b", "a\\[b")]
    [InlineData("gewoon", "gewoon")]
    [InlineData("", "")]
    public void Escape_MaaktWildcardsLetterlijk(string invoer, string verwacht)
        => LikePatroon.Escape(invoer).Should().Be(verwacht);

    [Fact]
    public void Escape_Null_GeeftLegeTekst() => LikePatroon.Escape(null).Should().BeEmpty();

    [Fact]
    public void Bevat_OmringtMetPercentEnEscapet() => LikePatroon.Bevat("JO_1%").Should().Be("%JO\\_1\\%%");
}
