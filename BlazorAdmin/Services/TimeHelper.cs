using System.Text.RegularExpressions;

namespace BlazorAdmin.Services;

/// <summary>
/// Centrale normalisering van tijdinvoer. Accepteert "830", "0830", "8:30", "8:3", "14:30" etc.
/// en converteert naar "HH:mm"-formaat. Eén implementatie voor de gehele applicatie.
/// </summary>
public static class TimeHelper
{
    private static readonly Regex ColonFormat = new(@"^(\d{1,2}):(\d{1,2})$");
    private static readonly Regex NoColonFormat = new(@"^(\d{1,4})$");

    /// <summary>
    /// Normaliseert tijdinvoer naar "HH:mm". Retourneert null bij lege/ongeldige invoer.
    /// Voorbeelden: "830"→"08:30", "0830"→"08:30", "8:30"→"08:30", "1430"→"14:30"
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        int hours, minutes;

        var colonMatch = ColonFormat.Match(s);
        if (colonMatch.Success)
        {
            hours = int.Parse(colonMatch.Groups[1].Value);
            minutes = int.Parse(colonMatch.Groups[2].Value);
        }
        else
        {
            var digitMatch = NoColonFormat.Match(s);
            if (!digitMatch.Success) return input; // onbekend formaat, ongewijzigd teruggeven

            var digits = digitMatch.Groups[1].Value;
            (hours, minutes) = digits.Length switch
            {
                1 => (int.Parse(digits), 0),          // "8" → 08:00
                2 => (int.Parse(digits), 0),          // "14" → 14:00
                3 => (int.Parse(digits[..1]), int.Parse(digits[1..])), // "830" → 08:30
                4 => (int.Parse(digits[..2]), int.Parse(digits[2..])), // "0830" / "1430"
                _ => (-1, -1)
            };
        }

        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
            return input; // buiten bereik, ongewijzigd laten zodat validatiefout zichtbaar blijft

        return $"{hours:D2}:{minutes:D2}";
    }

    /// <summary>
    /// Normaliseert tijdinvoer. Retourneert lege string bij lege invoer (voor niet-verplichte velden).
    /// </summary>
    public static string NormalizeOrEmpty(string? input)
        => Normalize(input) ?? string.Empty;
}
