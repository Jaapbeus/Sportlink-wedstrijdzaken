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

    /// <summary>Alleen de waarde <c>true</c> (hoofdletterongevoelig) zet de modus aan; leeg of onbekend = uit.</summary>
    public static bool IsActief(string? waarde) => string.Equals(waarde, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Leest de actuele instelling uit de omgeving.</summary>
    public static bool IsActief() => IsActief(Environment.GetEnvironmentVariable(InstellingNaam));
}
