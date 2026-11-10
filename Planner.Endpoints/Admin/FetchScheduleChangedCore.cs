namespace Planner.Endpoints.Admin;

/// <summary>Determines whether a submitted fetch schedule differs from the value read in the write transaction.</summary>
public static class FetchScheduleChangedCore
{
    public static bool IsChanged(
        IReadOnlyDictionary<string, string?> currentValues,
        IReadOnlyDictionary<string, string?> changes)
    {
        if (!changes.TryGetValue("FetchSchedule", out var requested))
            return false;

        return !currentValues.TryGetValue("FetchSchedule", out var current)
            || !string.Equals(current, requested, StringComparison.Ordinal);
    }
}
