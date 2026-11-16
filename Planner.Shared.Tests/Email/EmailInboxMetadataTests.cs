using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailInboxMetadataTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(-1, false)]
    public void AlleenPositiefBewezenAantal_IsWerk(int aantal, bool verwacht) =>
        EmailInboxMetadata.HeeftWerk(aantal).Should().Be(verwacht);

    [Fact]
    public void OntbrekendAantal_IsGeenLegeInbox()
    {
        var act = () => EmailInboxMetadata.HeeftWerk(null);
        act.Should().Throw<InvalidOperationException>();
    }
}
