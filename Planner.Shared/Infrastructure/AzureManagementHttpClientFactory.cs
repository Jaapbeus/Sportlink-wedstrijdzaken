namespace Planner.Shared.Infrastructure;

/// <summary>Creates the bounded HTTP client used for Azure Management requests.</summary>
public static class AzureManagementHttpClientFactory
{
    /// <summary>Creates an Azure Management client with the caller's explicit request timeout.</summary>
    public static HttpClient Create(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        return Create(TimeSpan.FromSeconds(timeoutSeconds), new HttpClientHandler());
    }

    internal static HttpClient Create(TimeSpan timeout, HttpMessageHandler handler)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentNullException.ThrowIfNull(handler);

        return new HttpClient(handler) { Timeout = timeout };
    }
}
