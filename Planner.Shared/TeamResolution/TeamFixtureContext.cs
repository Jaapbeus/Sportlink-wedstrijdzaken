using System.Globalization;

namespace Planner.Shared.TeamResolution;

/// <summary>Source ownership is teamnaam, not mere participation as home/away team.</summary>
public sealed record TeamFixture(string SourceKey, string OwnTeam, string Home, string Away, string? Time)
{
    /// <summary>Cancelled or incomplete source rows cannot establish fixture ownership.</summary>
    public static TeamFixture? FromSourceRow(string? key, string? owner, string? home, string? away, string? time, string? status)
        => key is null || owner is null || home is null || away is null
            || string.Equals(status?.Trim(), "AFGELAST", StringComparison.OrdinalIgnoreCase)
            ? null : new(key, owner, home, away, time);
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
        var supported = new List<(int TeamId, string Key, string Home, string Away, string Time)>();
        var owned = new List<(int TeamId, string Key, string Home, string Away, string Time)>();
        foreach (var fixture in context.Fixtures)
        {
            var owner = Ids(context, fixture.OwnTeam);
            if (owner.Count > 1) return null;
            if (owner.Count == 0 || (owner[0] != firstId && owner[0] != secondId)) continue;
            owned.Add((owner[0], fixture.SourceKey, fixture.Home.Trim(), fixture.Away.Trim(), fixture.Time?.Trim() ?? ""));
            if (!string.IsNullOrWhiteSpace(requestedTime) && !Equal(fixture.Time, requestedTime)) continue;
            var home = Ids(context, fixture.Home);
            var away = Ids(context, fixture.Away);
            if (home.Count > 1 || away.Count > 1) return null;
            var other = owner[0] == firstId ? secondId : firstId;
            if (MatchesSides(home, away, owner[0], other))
                supported.Add((owner[0], fixture.SourceKey, fixture.Home.Trim(), fixture.Away.Trim(), fixture.Time?.Trim() ?? ""));
        }
        // Retain all ownership claims before dedup: mirrored rows must never select their first owner.
        if (supported.Select(x => x.TeamId).Distinct().Count() != 1) return null;
        if (supported.Select(x => (x.Key, x.Home, x.Away, x.Time)).Distinct().Count() != 1) return null;
        var chosen = supported[0].TeamId;
        // Existing planner lookup is team/date based, so another owner fixture could be selected downstream.
        if (owned.Where(x => x.TeamId == chosen).Select(x => (x.Key, x.Home, x.Away, x.Time)).Distinct().Count() != 1) return null;
        return chosen;
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
    private static bool Equal(string? first, string? second)
        => string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}
