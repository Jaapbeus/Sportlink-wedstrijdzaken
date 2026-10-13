using Microsoft.Extensions.Logging;

namespace Planner.Shared.Sync;

/// <summary>
/// Ontdubbelt een Sportlink-respons op de business key vóór die naar staging gaat (#1712). Beide
/// tiers (<c>PostgresStagingRepository</c>, <c>SportlinkStagingRepository</c>) gebruiken deze ene
/// plek.
/// <para>
/// <b>Waarom.</b> De merge van staging naar his raakt per business key precies één his-rij. Staat
/// dezelfde sleutel twee keer in staging, dan weigert de database de hele opdracht: Postgres met
/// SQLSTATE 21000 (<c>ON CONFLICT DO UPDATE command cannot affect row a second time</c>), SQL
/// Server met fout 8672 (MERGE werkt dezelfde rij twee keer bij) of een unieke-indexfout op
/// <c>bk_*</c> als de rij nog niet bestaat. <c>/teams</c> levert soms twee vermeldingen met dezelfde
/// sleutel; zonder ontdubbeling faalde dan de hele teams-merge, en op de Postgres-tier ook de
/// mergestappen erna.
/// </para>
/// <para>
/// <b>Laatste wint.</b> Sportlink is de bron van waarheid (WZ-ADR-013), en binnen één respons is er
/// geen inhoudelijk criterium om tussen twee vermeldingen te kiezen. De laatste vermelding wint,
/// zodat de uitkomst bij dezelfde respons altijd dezelfde is. De volgorde van de overgebleven rijen
/// is die van hun laatste voorkomen.
/// </para>
/// </summary>
public static class StagingOntdubbeling
{
    /// <summary>
    /// Geeft <paramref name="rijen"/> terug met per sleutel alleen de laatste vermelding. Zonder
    /// dubbele sleutels komt dezelfde lijst ongewijzigd terug.
    /// </summary>
    public static IReadOnlyList<T> LaatsteWint<T, TSleutel>(IReadOnlyList<T> rijen, Func<T, TSleutel> sleutel)
        where TSleutel : notnull
    {
        ArgumentNullException.ThrowIfNull(rijen);
        ArgumentNullException.ThrowIfNull(sleutel);

        var laatstePositie = new Dictionary<TSleutel, int>();
        for (var i = 0; i < rijen.Count; i++)
            laatstePositie[sleutel(rijen[i])] = i;

        if (laatstePositie.Count == rijen.Count)
            return rijen;

        var uniek = new List<T>(laatstePositie.Count);
        for (var i = 0; i < rijen.Count; i++)
        {
            if (laatstePositie[sleutel(rijen[i])] == i)
                uniek.Add(rijen[i]);
        }
        return uniek;
    }

    /// <summary>
    /// Als <see cref="LaatsteWint{T, TSleutel}(IReadOnlyList{T}, Func{T, TSleutel})"/>, en meldt het
    /// aantal samengevoegde vermeldingen als waarschuwing. Alleen het aantal: sleutels en namen
    /// horen niet in het log.
    /// </summary>
    public static IReadOnlyList<T> LaatsteWint<T, TSleutel>(
        IReadOnlyList<T> rijen, Func<T, TSleutel> sleutel, ILogger log, string entiteit)
        where TSleutel : notnull
    {
        ArgumentNullException.ThrowIfNull(log);
        var uniek = LaatsteWint(rijen, sleutel);
        if (uniek.Count < rijen.Count)
            log.LogWarning("{Entiteit} - {Aantal} dubbele vermelding(en) met dezelfde sleutel; de laatste wint.",
                entiteit, rijen.Count - uniek.Count);
        return uniek;
    }

    /// <summary>
    /// De business key van een team zoals beide tiers hem in his gebruiken: <c>teamcode</c>,
    /// <c>lokaleteamcode</c>, <c>poulecode</c> (<c>KnownEntities.Teams</c> resp.
    /// <c>mta.source_target_mapping</c>). Een ontbrekende <c>poulecode</c> is één waarde, net als de
    /// lege string die <c>COALESCE</c>/<c>ISNULL</c> er in de database van maakt.
    /// </summary>
    public static (long Teamcode, long Lokaleteamcode, long? Poulecode) TeamSleutel(
        long teamcode, long lokaleteamcode, long? poulecode) => (teamcode, lokaleteamcode, poulecode);
}
