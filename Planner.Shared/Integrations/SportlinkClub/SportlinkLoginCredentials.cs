namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Credentials needed by an explicitly configured Sportlink login.</summary>
public sealed class SportlinkLoginCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string TotpSecret { get; set; } = string.Empty;
    public string TotpAlgorithm { get; set; } = "SHA1";
    public int TotpDigits { get; set; } = 6;
    public int TotpPeriodSeconds { get; set; } = 30;

    // Never expose credential values, even accidentally through logging or debugger formatting.
    public override string ToString() => nameof(SportlinkLoginCredentials);
}
