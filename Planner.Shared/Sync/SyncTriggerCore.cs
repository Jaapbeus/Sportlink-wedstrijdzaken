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

    /// <summary>Leest en valideert in één stap; leesfouten worden een foutkeuze.</summary>
    public static SyncTriggerKeuze LeesEnValideer(string? body, int huidigJaar)
    {
        var (request, fout) = LeesBody(body);
        return fout is null ? Valideer(request, huidigJaar) : new SyncTriggerKeuze(fout, null);
    }

    /// <summary>Standaard begin van het sync-venster: vorige week.</summary>
    public const int StandaardVanWeekOffset = -1;

    /// <summary>
    /// Bepaalt de "van"-weekoffset: standaard -1, bij een reset de seizoensstart via de (tier-specifieke)
    /// databasevraag <paramref name="seizoensStartWeekOffset"/>. Die geeft <c>null</c> als er geen
    /// seizoensrij voor dat startjaar bestaat; dat is een fout (400) en nooit een stille terugval op
    /// een standaardvenster, want de GUI zou dan "voltooid" melden terwijl er niets is opgebouwd (#1461).
    /// </summary>
    public static async Task<(int? Van, string? Fout)> BepaalVanWeekOffsetAsync(SyncTriggerKeuze keuze, Func<int, Task<int?>> seizoensStartWeekOffset)
    {
        if (keuze.SeasonStartYear is not int jaar) return (StandaardVanWeekOffset, null);
        var offset = await seizoensStartWeekOffset(jaar);
        return offset is int o
            ? (o, null)
            : (null, $"Seizoen {jaar} bestaat niet: er is geen seizoensrij met dat startjaar, dus er is niets om opnieuw op te bouwen.");
    }
}
