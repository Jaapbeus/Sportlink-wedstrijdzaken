namespace Planner.Shared.Email;

/// <summary>
/// De algemene reviewmodus van de e-mailverwerking (omgevingsvariabele <c>EmailReviewMode</c>): staat hij aan, dan
/// gaat er nooit een antwoord naar de afzender; het voorstel wordt opgeslagen ter beoordeling. Eén lezer voor de
/// productieprocessor (beide tiers) én de e-mailtester (#1583), zodat het eindoordeel van de tester niet kan
/// afwijken van wat de processor doet. Let op: dit is een proces-brede instelling, geen clubinstelling.
/// </summary>
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

    /// <summary>Leest de actuele instelling uit de omgeving.</summary>
    public static bool IsActief() => IsActief(Environment.GetEnvironmentVariable(InstellingNaam));
}
