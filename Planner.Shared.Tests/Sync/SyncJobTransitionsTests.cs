using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

public class SyncJobTransitionsTests
{
    [Theory]
    [InlineData(true, "succeeded")]
    [InlineData(false, "failed")]
    public void CompletionStatus_MapsOutcomeToTerminalStatus(bool succeeded, string expected)
        => SyncJobTransitions.CompletionStatus(succeeded).Should().Be(expected);

    [Fact]
    public void IsClaimGranted_ReturnsTrueOnlyWhenDatabaseReturnedTheJobId()
    {
        SyncJobTransitions.IsClaimGranted(Guid.NewGuid()).Should().BeTrue();
        SyncJobTransitions.IsClaimGranted(null).Should().BeFalse();
        SyncJobTransitions.IsClaimGranted(DBNull.Value).Should().BeFalse();
        SyncJobTransitions.IsClaimGranted(1).Should().BeFalse();
    }

    [Fact]
    public void TruncateError_NullStaysNull_ShortStaysUntouched_LongIsCut()
    {
        SyncJobTransitions.TruncateError(null).Should().BeNull();
        SyncJobTransitions.TruncateError("kort").Should().Be("kort");

        var exact = new string('x', SyncJobTransitions.MaxErrorMessageLength);
        SyncJobTransitions.TruncateError(exact).Should().Be(exact);

        var long1 = new string('y', SyncJobTransitions.MaxErrorMessageLength + 5);
        SyncJobTransitions.TruncateError(long1).Should().HaveLength(1000).And.Be(long1[..1000]);
    }
}
