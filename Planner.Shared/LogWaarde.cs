using System.Text;

namespace Planner.Shared;

/// <summary>
/// Maakt een door de gebruiker beïnvloedbare waarde veilig om in een logregel te zetten (#1472,
/// CodeQL <c>cs/log-forging</c>). Een waarde met CR/LF kan anders een extra, valse logregel
/// fabriceren. Stuurcodes worden vervangen door een spatie en de lengte wordt begrensd.
/// Gebruik dit op het <i>argument</i> van een gestructureerde logplaceholder.
/// </summary>
public static class LogWaarde
{
    /// <summary>Maximale lengte van een gelogde waarde; langer wordt afgekapt met een ellips.</summary>
    public const int MaxLengte = 200;

    public static string Schoon(string? waarde)
    {
        if (string.IsNullOrEmpty(waarde))
            return string.Empty;

        var bron = waarde.Length > MaxLengte ? waarde[..MaxLengte] : waarde;
        var sb = new StringBuilder(bron.Length + 1);
        foreach (var c in bron)
            sb.Append(char.IsControl(c) ? ' ' : c);
        if (waarde.Length > MaxLengte)
            sb.Append('…');
        return sb.ToString();
    }
}
