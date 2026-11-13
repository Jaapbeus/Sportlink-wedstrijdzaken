using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>Het resultaat van één synchronisatie van een dag: de blokken (nu met <c>ExternalMatchId</c> waar bekend) en de aantallen.</summary>
public sealed record VeldplannerBlokSynchronisatieResultaat(
    IReadOnlyList<SportlinkVeldplannerBlok> Blokken, int Toegevoegd, int Gewijzigd, int Verwijderd, int Ongewijzigd,
    int ExternIdOpgelost, int ExternIdOnbekend, int ZonderSleutel);

/// <summary>
/// Werkt de tabel met veldplanner-blokken bij vanuit wat Sportlink nu toont (#1915): bij elke planningsaanvraag wordt per
/// <c>PublicMatchId</c> de hash vergeleken en alleen het verschil geschreven. Sportlink is leidend; de tabel is een
/// afgeleide kopie die met elke aanvraag zuiver wordt.
/// <para>
/// <b>De brug naar onze wedstrijden.</b> Het veldplanner-antwoord draagt alleen <c>PublicMatchId</c>. Onze eigen regels
/// kennen het bondswedstrijdnummer (<c>wedstrijdnummer</c>), en dat is Sportlinks <c>ExternalMatchId</c>. Dat ID is
/// onveranderlijk, dus het wordt per nieuwe sleutel één keer bij Sportlink opgevraagd (<paramref name="losExternIdOp"/>) en
/// bewaard. Een mislukte opvraag blokkeert niets: het blok blijft zonder nummer en de volgende aanvraag probeert het opnieuw.
/// </para>
/// </summary>
public static class VeldplannerBlokSynchronisatie
{
    /// <summary>Hooguit zoveel nieuwe sleutels per aanvraag opvragen; de rest volgt bij de volgende aanvraag.</summary>
    public const int MaxOpTeLossenPerAanvraag = 150;

    /// <summary>Gelijktijdige opvragen bij Sportlink.</summary>
    public const int GelijktijdigeOpvragen = 6;

    /// <summary>Hoeveel dagen historie de tabel bewaart.</summary>
    public const int BewaarDagen = 14;

    /// <param name="losExternIdOp">Vraagt het <c>ExternalMatchId</c> van een <c>PublicMatchId</c> op; <c>null</c> als dat niet lukt.</param>
    /// <param name="vandaag">De datum van vandaag (UTC); bepaalt hoe oud de bewaarde rijen mogen zijn.</param>
    public static async Task<VeldplannerBlokSynchronisatieResultaat> SynchroniseerAsync(
        IVeldplannerBlokStore store, Func<string, Task<long?>> losExternIdOp, string clubCode, DateOnly datum,
        IReadOnlyList<SportlinkVeldplannerBlok> blokken, DateOnly vandaag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(losExternIdOp);
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);

        var bestaand = await store.LeesDagAsync(clubCode, datum, ct);
        var diff = VeldplannerBlokDiff.Bepaal(bestaand, blokken);

        var opTeLossen = blokken
            .Where(b => !string.IsNullOrWhiteSpace(b.PublicMatchId) && !(bestaand.TryGetValue(b.PublicMatchId!, out var s) && s.ExternalMatchId != null))
            .Select(b => b.PublicMatchId!).Distinct(StringComparer.Ordinal).Take(MaxOpTeLossenPerAanvraag).ToList();
        var opgelost = await LosOpAsync(opTeLossen, losExternIdOp, ct);

        long? ExternId(string sleutel) =>
            bestaand.TryGetValue(sleutel, out var s) && s.ExternalMatchId != null ? s.ExternalMatchId
            : opgelost.TryGetValue(sleutel, out var nieuw) ? nieuw : null;

        // Schrijven: wat nieuw of gewijzigd is, plus wat alleen zijn nummer nog kreeg (hash ongewijzigd).
        var teSchrijven = diff.Toevoegen.Concat(diff.Wijzigen).ToList();
        var alGepland = teSchrijven.Select(b => b.PublicMatchId!).ToHashSet(StringComparer.Ordinal);
        foreach (var blok in blokken)
        {
            var sleutel = blok.PublicMatchId;
            if (string.IsNullOrWhiteSpace(sleutel) || alGepland.Contains(sleutel)) continue;
            if (opgelost.ContainsKey(sleutel)) { teSchrijven.Add(blok); alGepland.Add(sleutel); }
        }

        var rijen = teSchrijven
            .Select(b => new VeldplannerBlokRij(clubCode, b.PublicMatchId!, datum, b.Label, b.Veld, b.StartTijd, b.Veldafmeting,
                b.DuurMinuten, ExternId(b.PublicMatchId!), VeldplannerBlokHash.Bereken(b)))
            .ToList();

        if (rijen.Count > 0 || diff.Verwijderen.Count > 0)
            await store.SchrijfAsync(clubCode, datum, rijen, diff.Verwijderen, vandaag.AddDays(-BewaarDagen), ct);

        var verrijkt = blokken
            .Select(b => string.IsNullOrWhiteSpace(b.PublicMatchId) ? b : b with { ExternalMatchId = ExternId(b.PublicMatchId!) })
            .ToList();
        var onbekend = verrijkt.Count(b => !string.IsNullOrWhiteSpace(b.PublicMatchId) && b.ExternalMatchId == null);

        return new VeldplannerBlokSynchronisatieResultaat(
            verrijkt, diff.Toevoegen.Count, diff.Wijzigen.Count, diff.Verwijderen.Count, diff.Ongewijzigd,
            opgelost.Count, onbekend, diff.ZonderSleutel);
    }

    private static async Task<Dictionary<string, long>> LosOpAsync(
        IReadOnlyList<string> sleutels, Func<string, Task<long?>> losExternIdOp, CancellationToken ct)
    {
        var resultaat = new Dictionary<string, long>(StringComparer.Ordinal);
        if (sleutels.Count == 0) return resultaat;

        using var poort = new SemaphoreSlim(GelijktijdigeOpvragen);
        var taken = sleutels.Select(async sleutel =>
        {
            await poort.WaitAsync(ct);
            try
            {
                // Een fout bij één wedstrijd mag de planning of de andere opvragen nooit stoppen.
                try { return (Sleutel: sleutel, Id: await losExternIdOp(sleutel)); }
                catch (Exception) when (!ct.IsCancellationRequested) { return (Sleutel: sleutel, Id: (long?)null); }
            }
            finally { poort.Release(); }
        }).ToList();

        foreach (var (sleutel, id) in await Task.WhenAll(taken))
            if (id is { } waarde) resultaat[sleutel] = waarde;
        return resultaat;
    }
}
