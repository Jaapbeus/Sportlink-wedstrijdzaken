using AwesomeAssertions;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public class FetchScheduleChangedCoreTests
{
    [Fact]
    public void IsChanged_NoScheduleSubmitted_ReturnsFalse()
    {
        FetchScheduleChangedCore.IsChanged(Values(("FetchSchedule", "0 0 4 * * *")), Values(("BufferMinuten", "15")))
            .Should().BeFalse();
    }

    [Fact]
    public void IsChanged_SameSchedule_ReturnsFalse()
    {
        FetchScheduleChangedCore.IsChanged(Values(("FetchSchedule", "0 0 4 * * *")), Values(("FetchSchedule", "0 0 4 * * *")))
            .Should().BeFalse();
    }

    [Fact]
    public void IsChanged_DifferentSchedule_ReturnsTrue()
    {
        FetchScheduleChangedCore.IsChanged(Values(("FetchSchedule", "0 0 4 * * *")), Values(("FetchSchedule", "0 0 5 * * *")))
            .Should().BeTrue();
    }

    [Fact]
    public void IsChanged_NoExistingSchedule_ReturnsTrue()
    {
        FetchScheduleChangedCore.IsChanged(Values(), Values(("FetchSchedule", null)))
            .Should().BeTrue();
    }

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
