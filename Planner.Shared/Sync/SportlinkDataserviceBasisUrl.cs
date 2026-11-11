namespace Planner.Shared.Sync;

/// <summary>
/// Accepts only the documented Sportlink data-service base for configured production reads.
/// Fixture pipelines receive their test URL directly and do not read this setting.
/// </summary>
public static class SportlinkDataserviceBasisUrl
{
    public const string Canonical = "https://data.sportlink.com";

    public static bool TryGetCanonical(string? configured, out string canonical)
    {
        canonical = string.Empty;
        if (!Uri.TryCreate(configured, UriKind.RelativeOrAbsolute, out var uri)
            || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "data.sportlink.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            return false;

        canonical = Canonical;
        return true;
    }
}
