namespace Planner.Shared.Email;

/// <summary>Pure parser for the one-time legacy review setting. Runtime review uses the club DB snapshot.</summary>
public static class EmailReviewModus
{
    public const string InstellingNaam = "EmailReviewMode";

    /// <summary>
    /// Fail-safe (#1809): alleen een expliciet <c>false</c> (hoofdletterongevoelig) zet de modus uit.
    /// Leeg, ontbrekend of onbekend betekent: reviewmodus AAN, zodat een verdwenen app-setting nooit
    /// onbedoeld tot automatische antwoorden leidt (analoog aan <c>sportlinkDryRun</c>).
    /// </summary>
    public static bool IsActief(string? waarde)
        => !string.Equals(waarde, "false", StringComparison.OrdinalIgnoreCase);

}
