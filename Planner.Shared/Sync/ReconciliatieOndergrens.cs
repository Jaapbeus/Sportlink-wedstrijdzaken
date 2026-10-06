namespace Planner.Shared.Sync;

/// <summary>
/// Tier-onafhankelijke regel voor de reconciliatie van verdwenen wedstrijden (#1547, #1558): vanaf
/// welke datum betekent "niet meer in de sync" ook "niet meer bij Sportlink". Beide tiers
/// (<c>PostgresSyncPipeline</c>, <c>SportlinkSyncPipeline</c>) gebruiken deze ene plek; een eigen
/// kopie per tier is precies de duplicatie die #1262 verbiedt.
/// </summary>
public static class ReconciliatieOndergrens
{
    /// <summary>
    /// Eerste datum waarop afwezigheid in de sync een wedstrijd als verwijderd mag markeren: morgen.
    /// <para>
    /// Review #1547 R1-F2: niet vandaag. Op een speeldag verdwijnt een gespeelde wedstrijd uit
    /// <c>/programma</c> terwijl de uitslag soms nog niet gepubliceerd is; dan ontbreekt hij in beide
    /// feeds, ook als beide aanroepen slagen. Prijs: een wedstrijd die Sportlink op de speeldag zelf
    /// schrapt, blijft die dag en daarna als historie staan. Een afgelaste wedstrijd houdt in
    /// Sportlink zijn rij met status "Afgelast" en valt via dat filter wél direct weg.
    /// </para>
    /// </summary>
    public static DateOnly EersteTeReconcilierenDatum(DateOnly vandaag) => vandaag.AddDays(1);

    /// <summary>
    /// De datum van vandaag in Nederland, uit <paramref name="utcNu"/>. Gooit nooit.
    /// <para>
    /// Probeert eerst de Windows-id en dan de IANA-id (welke op een host bestaat, hangt af van ICU en
    /// tzdata) en valt bij beide mislukkingen terug op de UTC-datum plus één dag. Dat is bewust de
    /// veilige kant op: een latere "vandaag" geeft een latere ondergrens en dus minder reconciliatie,
    /// nooit meer.
    /// </para>
    /// </summary>
    public static DateOnly VandaagInNederland(DateTime utcNu, Func<string, TimeZoneInfo> zoekTijdzone)
    {
        foreach (var id in new[] { "W. Europe Standard Time", "Europe/Amsterdam" })
        {
            try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNu, zoekTijdzone(id))); }
            catch (Exception) { /* volgende id; zie summary */ }
        }
        return DateOnly.FromDateTime(utcNu).AddDays(1);
    }

    /// <summary>De ondergrens voor een sync-run op dit moment.</summary>
    public static DateOnly Nu() =>
        EersteTeReconcilierenDatum(VandaagInNederland(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById));
}
