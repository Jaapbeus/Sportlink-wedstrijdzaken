namespace Planner.Shared.Email;

/// <summary>
/// Herkent berichten van automatische afzenders (afwezigheidsmeldingen, bulk, lijsten) aan hun
/// internetheaders, zodat daar nooit een automatisch antwoord op gaat. Pure logica; de Graph-adapter
/// levert alleen de headers aan.
/// </summary>
public static class AutomatischeAfzenderHerkenning
{
    public const string AutoSubmitted = "Auto-Submitted";
    public const string Precedence = "Precedence";
    public const string AutoResponseSuppress = "X-Auto-Response-Suppress";
    public const string AutoResponseSuppressAlles = "All";

    /// <summary>Tekst voor de log- of tracereden bij een overgeslagen bericht.</summary>
    public const string Reden = "Bericht van een automatische afzender: geen automatisch antwoord";

    private static readonly string[] OnderdrukkendePrecedence = ["bulk", "auto_reply", "junk", "list"];
    private static readonly string[] OnderdrukkendeSuppressWaarden = ["all", "oof", "autoreply"];

    /// <summary>Headers die elk uitgaand bericht van de pijplijn krijgt, zodat ontvangers er niet automatisch op antwoorden.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> UitgaandeHeaders { get; } =
        [new(AutoResponseSuppress, AutoResponseSuppressAlles)];

    public static bool IsAutomatisch(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return false;

        foreach (var (naam, waarde) in headers)
        {
            if (IsAutomatischeHeader(naam?.Trim() ?? "", waarde ?? ""))
                return true;
        }

        return false;
    }

    private static bool IsAutomatischeHeader(string naam, string waarde)
    {
        if (naam.Equals(AutoSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            var kern = waarde.Split(';')[0].Trim();
            return kern.Length > 0 && !kern.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        if (naam.Equals(Precedence, StringComparison.OrdinalIgnoreCase))
            return OnderdrukkendePrecedence.Contains(waarde.Trim(), StringComparer.OrdinalIgnoreCase);

        if (naam.Equals(AutoResponseSuppress, StringComparison.OrdinalIgnoreCase))
            return waarde.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(t => OnderdrukkendeSuppressWaarden.Contains(t, StringComparer.OrdinalIgnoreCase));

        return false;
    }
}
