using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

public class SportlinkDataserviceBasisUrlTests
{
    [Theory]
    [InlineData("https://data.sportlink.com")]
    [InlineData("https://data.sportlink.com/")]
    [InlineData("https://DATA.SPORTLINK.COM:443/")]
    public void OnlyDocumentedBaseIsCanonicalized(string configured)
    {
        SportlinkDataserviceBasisUrl.TryGetCanonical(configured, out var canonical).Should().BeTrue();
        canonical.Should().Be(SportlinkDataserviceBasisUrl.Canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/data.sportlink.com")]
    [InlineData("http://data.sportlink.com")]
    [InlineData("https://elsewhere.example")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://user@data.sportlink.com")]
    [InlineData("https://data.sportlink.com?x=y")]
    [InlineData("https://data.sportlink.com#fragment")]
    [InlineData("https://data.sportlink.com:444")]
    [InlineData("https://data.sportlink.com/other")]
    [InlineData("https://data.sportlink.com.attacker.example")]
    public void OtherConfiguredBasesAreRejected(string? configured)
    {
        SportlinkDataserviceBasisUrl.TryGetCanonical(configured, out var canonical).Should().BeFalse();
        canonical.Should().BeEmpty();
    }
}
