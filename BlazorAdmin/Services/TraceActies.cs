using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Bepaalt welke leeracties bij een stap van de beslissingstrace horen (#1568 deel C). Puur en los
/// testbaar: de Razor-component vraagt hier alleen "is er een actie, en met welke tekst?".
/// </summary>
public static class TraceActies
{
    public const string TeamHerkenning = "team-herkenning";
    public const string TegenstanderHerkenning = "tegenstander-herkenning";
    public const string Classificatie = "classificatie";

    /// <summary>
    /// De teamtekst die een beheerder aan een team kan koppelen: alleen bij een herkenningsstap die
    /// <c>Onopgelost</c> of <c>MeerdereKandidaten</c> opleverde. Een herkende of niet-genoemde tekst geeft <c>null</c>.
    /// </summary>
    public static string? KoppelTekst(TraceStapDto stap)
    {
        if (stap.Code is not (TeamHerkenning or TegenstanderHerkenning)) return null;
        if (!stap.Details.TryGetValue("bron", out var bron) || bron is not ("Onopgelost" or "MeerdereKandidaten")) return null;
        if (!stap.Details.TryGetValue("ruweTekst", out var tekst) || string.IsNullOrWhiteSpace(tekst)) return null;

        // De trace maskeert e-mailadressen en nummers. Een gemaskeerde tekst is niet de echte schrijfwijze:
        // daar een alias op aanmaken koppelt letterlijk "[e-mail]" aan een team en verandert niets aan de echte mail.
        return GemaskeerdeTekens.Any(m => tekst.Contains(m, StringComparison.Ordinal)) ? null : tekst;
    }

    /// <summary>Pad van de pagina met de wachtrij onbekende teamteksten (Teamaliassen).</summary>
    public const string WachtrijPad = "teamaliassen";

    /// <summary>
    /// Of een stap uit de BEWAARDE trace naar de wachtrij moet verwijzen: een niet-herkend team, waarvan de ruwe
    /// tekst bewust niet in de permanente trace staat. De beheerder koppelt dan in de wachtrij (#1568, R1-F1).
    /// </summary>
    public static bool VerwijstNaarWachtrij(TraceStapDto stap)
        => stap.Code is TeamHerkenning or TegenstanderHerkenning
           && stap.Details.TryGetValue("bron", out var bron) && bron is "Onopgelost" or "MeerdereKandidaten"
           && !stap.Details.ContainsKey("ruweTekst");

    private static readonly string[] GemaskeerdeTekens = ["[e-mail]", "[nummer]"];

    /// <summary>Het verzoektype dat de classificatiestap koos; <c>null</c> als deze stap geen classificatie is.</summary>
    public static string? ClassificatieType(TraceStapDto stap)
        => stap.Code == Classificatie && stap.Details.TryGetValue("type", out var type) && !string.IsNullOrWhiteSpace(type) ? type : null;
}

/// <summary>Eén rij van de vergelijking vóór ↔ na een herbeoordeling.</summary>
public sealed record TraceVergelijkingRij(string Label, string Voor, string Na)
{
    public bool Gewijzigd => !string.Equals(Voor, Na, StringComparison.Ordinal);
}

/// <summary>
/// Vergelijking van twee tester-resultaten (#1568 deel C): na het aanmaken van een alias of leermoment
/// beoordeelt de beheerder dezelfde invoer opnieuw en ziet hier wat er veranderde.
/// </summary>
public static class TraceVergelijking
{
    public static IReadOnlyList<TraceVergelijkingRij> Maak(BeslissingsTraceDto? voor, BeslissingsTraceDto? na) =>
    [
        new("Verzoektype", Detail(voor, "classificatie", "type"), Detail(na, "classificatie", "type")),
        new("Herkend team", Uitkomst(voor, "team-herkenning"), Uitkomst(na, "team-herkenning")),
        new("Zekerheid", Zekerheid(voor), Zekerheid(na)),
        new("Antwoordsjabloon", Detail(voor, "sjabloon", "sjabloon", laatste: true), Detail(na, "sjabloon", "sjabloon", laatste: true)),
        new("Leermomenten meegegeven", Detail(voor, "leermomenten", "aantal"), Detail(na, "leermomenten", "aantal")),
    ];

    private static string Zekerheid(BeslissingsTraceDto? trace) => trace?.Oordeel is null
        ? "—"
        : trace.Oordeel.IsZeker ? "Zeker (zou automatisch verstuurd worden)" : "Onzeker (zou in review gaan)";

    private static string Uitkomst(BeslissingsTraceDto? trace, string code)
        => trace?.Stappen.FirstOrDefault(s => s.Code == code)?.Uitkomst is { Length: > 0 } uitkomst ? uitkomst : "—";

    private static string Detail(BeslissingsTraceDto? trace, string code, string sleutel, bool laatste = false)
    {
        var stappen = trace?.Stappen.Where(s => s.Code == code) ?? Enumerable.Empty<TraceStapDto>();
        var stap = laatste ? stappen.LastOrDefault() : stappen.FirstOrDefault();
        return stap is not null && stap.Details.TryGetValue(sleutel, out var waarde) && !string.IsNullOrWhiteSpace(waarde) ? waarde : "—";
    }
}
