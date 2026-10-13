namespace Planner.Shared.Sync;

/// <summary>
/// De seizoensstartmaand van een club (#1715): de maand waarin het seizoen begint. Het seizoenseinde
/// is de dag vóór de start van het volgende seizoen. Eén plek voor de standaardwaarde, de geldige
/// reeks en de maandnamen van de keuzelijst, zodat beide tiers, de validatie en de GUI niet uit elkaar
/// lopen. Elke club heeft een eigen waarde; er wordt nooit een waarde uit de rijen van alle clubs
/// afgeleid (geen <c>MIN()</c>/<c>MAX()</c> over clubs).
/// </summary>
public static class SeizoenStartMaand
{
    /// <summary>Standaard: juli. Gelijk aan de kolomdefault <c>seasonstartmonth</c> / <c>SeasonStartMonth</c>.</summary>
    public const int Standaard = 7;

    /// <summary>Nederlandse maandnamen, index 0 = januari.</summary>
    public static IReadOnlyList<string> Maandnamen { get; } =
    [
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december",
    ];

    /// <summary>Is <paramref name="maand"/> een maandnummer van 1 (januari) tot en met 12 (december)?</summary>
    public static bool IsGeldig(int maand) => maand is >= 1 and <= 12;

    /// <summary>Parst een ingevoerde waarde strikt als geheel getal 1 t/m 12 (geen spaties, geen tekens).</summary>
    public static bool TryParse(string? waarde, out int maand)
    {
        maand = 0;
        return waarde is { Length: 1 or 2 }
            && waarde.All(char.IsAsciiDigit)
            && int.TryParse(waarde, out maand)
            && IsGeldig(maand);
    }

    /// <summary>De opgeslagen waarde als die geldig is, anders de <see cref="Standaard"/> (juli).</summary>
    public static int OfStandaard(int? opgeslagen) => opgeslagen is int m && IsGeldig(m) ? m : Standaard;

    /// <summary>Maandnaam van een geldige maand ("juli"), anders de naam van de standaardmaand.</summary>
    public static string Naam(int maand) => Maandnamen[OfStandaard(maand) - 1];
}
