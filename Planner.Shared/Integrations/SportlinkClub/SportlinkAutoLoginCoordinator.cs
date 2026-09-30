namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Herstelt de absolute tien-uurs-sessie. Aanroeper houdt de gedeelde databaselease vast.</summary>
public sealed class SportlinkAutoLoginCoordinator(
    ISportlinkAutoLoginStore store,
    ISportlinkAutoLoginProvider provider,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken ct)
        => store.AcquireLeaseAsync(role, ct);

    public async Task<SportlinkLoginResult?> TryLoginAsync(string role, bool force, CancellationToken ct)
    {
        var state = await store.ReadAsync(role, ct);
        if (state?.Credentials is null) return null;
        var now = _clock.GetUtcNow();
        // Schrijf vóór de netwerkcall: ook een crash na een mislukte poging mag geen storm geven.
        if (state.RetryAfterUtc > now) return null;
        if (state.FailureCount >= 3) return null;
        if (state.LastLoginUtc is { } last &&
            now - last < (force ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(9)))
            return null;

        await store.MarkAttemptAsync(role, now.AddMinutes(15), ct);
        SportlinkLoginResult result;
        try
        {
            result = await provider.LoginAsync(state.Credentials, ct);
        }
        catch (SportlinkLoginException ex)
        {
            var permanent = ex.Failure is not (SportlinkLoginFailure.NetworkFailure or SportlinkLoginFailure.Timeout);
            await store.MarkFailureAsync(role, permanent ? "Controleer logininstellingen" : "Aanmelden tijdelijk mislukt", permanent, ct);
            throw;
        }
        // Eerst duurzaam bewaren; pas daarna mogen API-aanroepen dit token gebruiken.
        await store.SchrijfRefreshTokenAsync(role, result.RefreshToken, ct);
        await store.MarkSuccessAsync(role, _clock.GetUtcNow(), ct);
        return result;
    }
}
