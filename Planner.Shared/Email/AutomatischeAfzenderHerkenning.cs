namespace Planner.Shared.Email;

/// <summary>
/// Herkent berichten van automatische afzenders (afwezigheidsmeldingen, autoreplies, bulk) aan hun
/// internetheaders, zodat daar nooit een automatisch antwoord op gaat. Pure logica; de Graph-adapter
/// levert alleen de headers aan.
/// <para>
/// Bewust smal: alleen headers die een automatisch gegenereerd bericht aanduiden tellen. Een
/// mailinglijst (<c>Precedence: list</c>) of een inkomende <c>X-Auto-Response-Suppress</c> zegt niets
/// over de afzender (een lijstalias of ticketsysteem kan een gewoon verzoek van een mens doorsturen)
/// en is dus geen reden om een bericht over te slaan.
/// </para>
/// </summary>
public static class AutomatischeAfzenderHerkenning
{
    public const string AutoSubmitted = "Auto-Submitted";
    public const string Precedence = "Precedence";
    public const string AutoResponseSuppress = "X-Auto-Response-Suppress";

    /// <summary>
    /// Waarde van de uitgaande <see cref="AutoResponseSuppress"/>-header: alleen afwezigheidsmeldingen
    /// (OOF) en autoreplies onderdrukken. Niet <c>All</c>: dat onderdrukt bij Exchange-ontvangers ook
    /// onbestelbaarheids- en bezorgberichten (NDR/DR/RN/NRN), en een niet-bezorgd antwoord moet voor de
    /// coördinator zichtbaar blijven.
    /// </summary>
    public const string AutoResponseSuppressWaarde = "OOF, AutoReply";

    /// <summary>Tekst voor de log- of tracereden bij een overgeslagen bericht.</summary>
    public const string Reden = "Bericht van een automatische afzender: geen automatisch antwoord";

    private static readonly string[] OnderdrukkendePrecedence = ["bulk", "auto_reply", "junk"];

    /// <summary>Headers die elk uitgaand bericht van de pijplijn krijgt, zodat ontvangers er niet automatisch op antwoorden.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> UitgaandeHeaders { get; } =
        [new(AutoResponseSuppress, AutoResponseSuppressWaarde)];

    public static bool IsAutomatisch(IEnumerable<KeyValuePair<string, string>>? headers)
        => HerkenningsHeader(headers) is not null;

    /// <summary>
    /// Naam van de header waarop het bericht als automatisch is herkend (voor de log), of <c>null</c>
    /// als het geen automatisch bericht is. Geeft nooit de headerwaarde terug.
    /// </summary>
    public static string? HerkenningsHeader(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return null;

        foreach (var (naam, waarde) in headers)
        {
            var schoneNaam = naam?.Trim() ?? "";
            if (IsAutomatischeHeader(schoneNaam, waarde ?? ""))
                return schoneNaam.Equals(AutoSubmitted, StringComparison.OrdinalIgnoreCase) ? AutoSubmitted : Precedence;
        }

        return null;
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

        return false;
    }
}
