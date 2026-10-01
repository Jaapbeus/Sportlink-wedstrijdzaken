namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Obtains a Sportlink token pair through the interactive Keycloak form flow.</summary>
public interface ISportlinkAutoLoginProvider
{
    Task<SportlinkLoginResult> LoginAsync(
        SportlinkLoginCredentials credentials,
        CancellationToken cancellationToken = default);
}

/// <summary>Tokens returned by Keycloak. Keep this type free of generated ToString output.</summary>
public sealed class SportlinkLoginResult
{
    public SportlinkLoginResult(
        string accessToken,
        string refreshToken,
        int expiresInSeconds,
        int refreshExpiresInSeconds)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresInSeconds = expiresInSeconds;
        RefreshExpiresInSeconds = refreshExpiresInSeconds;
    }

    public string AccessToken { get; }
    public string RefreshToken { get; }
    public int ExpiresInSeconds { get; }
    public int RefreshExpiresInSeconds { get; }
}

public enum SportlinkLoginFailure
{
    InvalidCredentials,
    UnsupportedChallenge,
    InvalidResponse,
    NetworkFailure,
    Timeout
}

/// <summary>A sanitized login failure with no response body, URI, or inner exception.</summary>
public sealed class SportlinkLoginException : Exception
{
    public SportlinkLoginException(SportlinkLoginFailure failure)
        : base("Sportlink login failed.") => Failure = failure;

    public SportlinkLoginFailure Failure { get; }
}
