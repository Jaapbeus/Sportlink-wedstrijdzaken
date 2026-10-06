using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Email.Trace;

/// <summary>Hoe zeker een enkele beslissingsstap is (#1568).</summary>
public enum ZekerheidsNiveau
{
    Zeker,
    Onzeker,
    Mislukt
}

/// <summary>
/// Eén beslissing van de e-mailpipeline, in leesbare vorm. Bevat uitsluitend PII-arme waarden
/// (zie <see cref="TraceBuilder"/>): nooit mailbody, afzender of vrije tekst uit de mail.
/// </summary>
/// <param name="Code">Stabiele sleutel (zie <see cref="TraceCodes"/>); machine-leesbaar.</param>
/// <param name="Titel">Korte Nederlandse naam van de stap.</param>
/// <param name="Uitkomst">Korte Nederlandse uitkomst.</param>
/// <param name="Zekerheid">Zekerheid van deze stap.</param>
/// <param name="Details">Aanvullende sleutel/waarde-paren (alle waarden gesaneerd).</param>
public sealed record TraceStap(
    string Code,
    string Titel,
    string Uitkomst,
    ZekerheidsNiveau Zekerheid,
    IReadOnlyDictionary<string, string> Details);

/// <summary>Geordende reeks beslissingen plus het eindoordeel (#1568).</summary>
public sealed record BeslissingsTrace(
    IReadOnlyList<TraceStap> Stappen,
    ZekerheidsOordeel Oordeel)
{
    private static readonly JsonSerializerOptions JsonOpties = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>Serialiseert de trace; op beide tiers identiek (System.Text.Json, camelCase).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpties);

    /// <summary>De trace als JSON-element, zodat een API-respons hem tier-onafhankelijk kan opnemen.</summary>
    public JsonElement ToJsonElement() => JsonDocument.Parse(ToJson()).RootElement;

    /// <summary>Leest een met <see cref="ToJson"/> geschreven trace terug; <c>null</c> bij ongeldige invoer.</summary>
    public static BeslissingsTrace? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BeslissingsTrace>(json, JsonOpties); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Stabiele stapcodes. Wijzig bestaande waarden nooit: ze worden straks opgeslagen (deel B).</summary>
public static class TraceCodes
{
    public const string Classificatie = "classificatie";
    public const string TeamHerkenning = "team-herkenning";
    public const string TegenstanderHerkenning = "tegenstander-herkenning";
    public const string TeamWissel = "team-wissel";
    public const string OpponentPad = "opponent-pad";
    public const string OpponentTeamHerkenning = "opponent-team-herkenning";
    public const string Datum = "datum";
    public const string Tak = "tak";
    public const string Sjabloon = "sjabloon";
    public const string Eindoordeel = "eindoordeel";
}
