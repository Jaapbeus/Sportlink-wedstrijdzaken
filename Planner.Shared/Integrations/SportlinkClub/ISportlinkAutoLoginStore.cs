namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Versleutelde authenticatieopslag. Alle mutaties lopen onder dezelfde club/rol-lease.</summary>
public interface ISportlinkAutoLoginStore : ISportlinkClubTokenStore
{
    string ClubCode { get; }
    Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken);
    Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken);
    Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken);
    Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken);
    Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken);
    Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken);
    Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class SportlinkAutoLoginState
{
    public SportlinkLoginCredentials? Credentials { get; init; }
    public DateTimeOffset? LastLoginUtc { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public string? LastError { get; init; }
    public int FailureCount { get; init; }
    public override string ToString() => nameof(SportlinkAutoLoginState);
}
