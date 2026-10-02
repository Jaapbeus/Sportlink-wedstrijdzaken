using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Sync;

/// <summary>Body van <c>POST /api/beheer/sync/trigger</c> (#1352). Beide velden optioneel.</summary>
public sealed class SyncTriggerRequest
{
    /// <summary>true = volledig seizoen opnieuw ophalen vanaf <see cref="Season"/>.</summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; set; }

    /// <summary>Startjaar van het seizoen (bijv. 2025 voor 2025/2026). Alleen relevant bij <see cref="Reset"/>.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }
}

/// <summary>Uitkomst van <see cref="SyncTriggerCore.Valideer"/>: óf een foutmelding, óf het seizoensstartjaar (null = standaardvenster).</summary>
public sealed record SyncTriggerKeuze(string? Fout, int? SeasonStartYear)
{
    public bool Geldig => Fout is null;
}

/// <summary>
/// Tier-onafhankelijke validatie van de reset-parameters van de handmatige sync (#1352). Eén plek voor
/// beide tiers; de tiers doen alleen nog de databasevraag (seizoensstart → weekoffset) en de queue.
/// </summary>
public static class SyncTriggerCore
{
    public const int MinSeasonStartYear = 2000;

    private static readonly JsonSerializerOptions Opties = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Leest de optionele body. Lege/ontbrekende body = standaardgedrag. Ongeldige JSON = fout.</summary>
    public static (SyncTriggerRequest? Request, string? Fout) LeesBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (new SyncTriggerRequest(), null);
        try
        {
            return (JsonSerializer.Deserialize<SyncTriggerRequest>(body, Opties) ?? new SyncTriggerRequest(), null);
        }
        catch (JsonException)
        {
            return (null, "Ongeldige aanvraag: 'reset' moet true/false zijn en 'season' een geheel jaartal.");
        }
    }

    /// <summary>
    /// Zonder <c>reset</c> blijft <c>season</c> genegeerd (exact het oude gedrag). Met <c>reset</c> is
    /// <c>season</c> verplicht en moet het tussen <see cref="MinSeasonStartYear"/> en huidigJaar+1 liggen.
    /// </summary>
    public static SyncTriggerKeuze Valideer(SyncTriggerRequest? request, int huidigJaar)
    {
        if (request is null || !request.Reset) return new SyncTriggerKeuze(null, null);
        if (request.Season is not int jaar)
            return new SyncTriggerKeuze("Geef bij een volledige herberekening een seizoen op (startjaar, bijv. 2025).", null);
        if (jaar < MinSeasonStartYear || jaar > huidigJaar + 1)
            return new SyncTriggerKeuze($"Ongeldig seizoen {jaar}: kies een startjaar tussen {MinSeasonStartYear} en {huidigJaar + 1}.", null);
        return new SyncTriggerKeuze(null, jaar);
    }

    /// <summary>Standaard begin van het sync-venster: vorige week.</summary>
    public const int StandaardVanWeekOffset = -1;

    /// <summary>
    /// Bepaalt de "van"-weekoffset: standaard -1, bij een reset de seizoensstart via de (tier-specifieke)
    /// databasevraag <paramref name="seizoensStartWeekOffset"/>.
    /// </summary>
    public static async Task<int> BepaalVanWeekOffsetAsync(SyncTriggerKeuze keuze, Func<int, Task<int>> seizoensStartWeekOffset) =>
        keuze.SeasonStartYear is int jaar ? await seizoensStartWeekOffset(jaar) : StandaardVanWeekOffset;
}
