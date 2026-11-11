using System.Globalization;

namespace Planner.Shared.TeamResolution;

/// <summary>Source ownership is teamnaam, not mere participation as home/away team.</summary>
public sealed record TeamFixture(string? SourceKey, string OwnTeam, string? Home, string? Away, string? Time)
{
    /// <summary>Native queries filter cancellation; incomplete rows still count toward owner-date uniqueness.</summary>
    public static TeamFixture? FromSourceRow(string? key, string? owner, string? home, string? away, string? time)
        => owner is null ? null : new(key, owner, home, away, time);
}
public sealed record TeamFixtureSpelling(int TeamId, string Text);
public sealed record TeamFixtureContext(IReadOnlyList<TeamFixture> Fixtures, IReadOnlyList<TeamFixtureSpelling> Spellings);

public interface ITeamFixtureContextRepository
{
    Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds);
}

/// <summary>Only two individually resolved identities may be compared; this never chooses from unresolved candidates.</summary>
public static class TeamFixtureChoice
{
    public static int? Choose(TeamFixtureContext context, int firstId, int secondId, string? requestedTime)
    {
        var supported = new List<int>();
        var owned = new List<int>();
        foreach (var fixture in context.Fixtures)
        {
            var owner = Ids(context, fixture.OwnTeam);
            if (owner.Count > 1) return null;
            if (owner.Count == 0 || (owner[0] != firstId && owner[0] != secondId)) continue;
            owned.Add(owner[0]);
            if (fixture.SourceKey is null || fixture.Home is null || fixture.Away is null) continue;
            if (!string.IsNullOrWhiteSpace(requestedTime) && !SameTime(fixture.Time, requestedTime)) continue;
            var home = Ids(context, fixture.Home);
            var away = Ids(context, fixture.Away);
            if (home.Count > 1 || away.Count > 1) return null;
            var other = owner[0] == firstId ? secondId : firstId;
            if (MatchesSides(home, away, owner[0], other))
                supported.Add(owner[0]);
        }
        if (supported.Count != 1) return null;
        var chosen = supported[0];
        // The downstream lookup is team/date based: every visible owned row counts, even incomplete duplicates.
        return owned.Count(teamId => teamId == chosen) == 1 ? chosen : null;
    }

    public static bool TryDate(string? date, IReadOnlyList<string>? dates, string? time, out DateOnly value)
        => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            && (dates is null || dates.All(d => d == date))
            && (string.IsNullOrWhiteSpace(time) || TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    private static bool MatchesSides(IReadOnlyList<int> home, IReadOnlyList<int> away, int owner, int opponent)
        => home.Count == 1 && away.Count == 1
            && ((home[0] == owner && away[0] == opponent) || (away[0] == owner && home[0] == opponent));

    private static List<int> Ids(TeamFixtureContext context, string text)
        => context.Spellings.Where(s => Equal(s.Text, text)).Select(s => s.TeamId).Distinct().ToList();
    private static bool SameTime(string? first, string? second)
        => TimeOnly.TryParse(first, out var firstTime) && TimeOnly.TryParse(second, out var secondTime)
            && firstTime == secondTime;
    private static bool Equal(string? first, string? second)
        => string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}
