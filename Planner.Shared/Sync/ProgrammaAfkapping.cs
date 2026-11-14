namespace Planner.Shared.Sync;

/// <summary>
/// Haalt een <c>/programma</c>-venster van de Sportlink-dataservice volledig op, ook als dat meer dan 100 wedstrijden
/// bevat (#1915). Beide tiers (<c>PostgresSyncPipeline</c>, <c>SportlinkSyncPipeline</c>) gebruiken deze ene plek.
/// <para>
/// <b>Waarom.</b> <c>/programma</c> geeft per aanroep maximaal <see cref="Plafond"/> rijen terug, oplopend in tijd; de
/// rest valt zonder foutmelding weg (live vastgesteld, ook met <c>aantalrecords</c>). Het venster loopt vanaf het
/// aanroepmoment (<c>nu + 7 dagen × weekoffset</c>, daarna <c>aantaldagen</c> × 24 uur) en kent geen datumparameter, dus een
/// afgekapte speeldag is niet gericht opnieuw op te vragen. Zonder <c>aantaldagen</c> is het venster 28 dagen terwijl
/// <c>weekoffset</c> maar 7 dagen opschuift: de vensters overlappen en elk kapte af op de eerste 100. Op een drukke zaterdag
/// verdween zo het einde van de dag uit staging, waarna de reconciliatie (<c>ReconcileWindowedAsync</c>, venster uit MIN/MAX
/// van de datum) die wedstrijden als verwijderd markeerde — en Sportlink ze nooit meer teruggaf.
/// </para>
/// <para>
/// <b>Hoe.</b> De aanroeper vraagt aansluitende vensters van <see cref="VensterDagen"/> dagen op. Raakt een aanroep het
/// plafond, dan volgt in twee stappen een fijnere indeling van <i>hetzelfde</i> venster: (1) alleen thuis en alleen uit
/// (<c>thuis=JA&amp;uit=NEE</c>, <c>thuis=NEE&amp;uit=JA</c>: disjunct, samen het hele venster), (2) per team
/// (<c>teamcode</c>; een team speelt zelden meer dan een paar wedstrijden per venster). Een wedstrijd tussen twee eigen teams
/// staat in meer dan één verzameling; de opslag ontdubbelt op wedstrijdcode. Is ook de fijnste indeling nog afgekapt, dan is
/// de ophaling <see cref="Ophaling{T}.Volledig"/> <c>false</c>: de aanroeper moet dat als onvolledig behandelen en dus niet
/// reconciliëren.
/// </para>
/// </summary>
public static class ProgrammaAfkapping
{
    /// <summary>Het maximale aantal rijen dat <c>/programma</c> per aanroep teruggeeft.</summary>
    public const int Plafond = 100;

    /// <summary>
    /// De lengte van elk venster in dagen: een dag meer dan de 7 dagen die <c>weekoffset</c> opschuift. Zo sluiten de vensters
    /// aan, ook als de aanroepen een paar seconden na elkaar komen (het venster begint op het aanroepmoment) en een wedstrijd
    /// precies op de grens begint.
    /// </summary>
    public const int VensterDagen = 8;

    /// <summary>De thuis- en uit-verzameling van het eigen programma (disjunct, samen het hele venster).</summary>
    public const string FilterAlleenThuis = "thuis=JA&uit=NEE";

    /// <inheritdoc cref="FilterAlleenThuis"/>
    public const string FilterAlleenUit = "thuis=NEE&uit=JA";

    /// <summary>Gelijktijdige aanroepen bij de terugval per team.</summary>
    public const int GelijktijdigePerTeam = 6;

    /// <param name="Rijen">Alle rijen die binnenkwamen; mogelijk met dubbelen (zie de klasse-documentatie).</param>
    /// <param name="Volledig"><c>false</c> als ook de fijnste indeling het plafond raakte: dan ontbreken er rijen.</param>
    /// <param name="Gesplitst">Of de ophaling in thuis en uit is gesplitst.</param>
    /// <param name="PerTeam">Of de ophaling per team is gedaan (thuis en uit waren nog afgekapt).</param>
    public sealed record Ophaling<T>(IReadOnlyList<T> Rijen, bool Volledig, bool Gesplitst, bool PerTeam = false);

    /// <param name="url">De volledige aanroep-URL inclusief <c>?</c>-query; wordt nooit gelogd (bevat de clientId).</param>
    /// <param name="haal">Voert één aanroep uit en geeft de rijen terug; een fout propageert naar de aanroeper.</param>
    /// <param name="teamcodes">De teamcodes voor de terugval per team; <c>null</c> of leeg = geen terugval (dan is een nog
    /// afgekapte splitsing onvolledig).</param>
    public static async Task<Ophaling<T>> HaalVolledigAsync<T>(
        string url, Func<string, Task<IReadOnlyList<T>>> haal, Func<Task<IReadOnlyList<long>>>? teamcodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(haal);

        var eerste = await haal(url);
        if (eerste.Count < Plafond) return new Ophaling<T>(eerste, true, false);

        var thuis = await haal(MetFilter(url, FilterAlleenThuis));
        var uit = await haal(MetFilter(url, FilterAlleenUit));
        var samen = thuis.Concat(uit).ToList();

        // Een splitsing die minder oplevert dan de gewone aanroep is niet te vertrouwen (filter genegeerd of een
        // storing): behoud wat we hadden en meld het als onvolledig.
        if (samen.Count < eerste.Count) return new Ophaling<T>(eerste, false, true);

        if (thuis.Count < Plafond && uit.Count < Plafond) return new Ophaling<T>(samen, true, true);

        // Nog afgekapt: per team. Zonder teams, of als dit minder oplevert, blijft de splitsing het beste wat we hebben.
        var codes = teamcodes == null ? [] : await teamcodes();
        if (codes.Count == 0) return new Ophaling<T>(samen, false, true);

        var perTeam = await HaalPerTeamAsync(url, haal, codes);
        if (perTeam.Rijen.Count < samen.Count) return new Ophaling<T>(samen, false, true, PerTeam: true);
        return new Ophaling<T>(perTeam.Rijen, perTeam.Volledig, true, PerTeam: true);
    }

    private static async Task<(IReadOnlyList<T> Rijen, bool Volledig)> HaalPerTeamAsync<T>(
        string url, Func<string, Task<IReadOnlyList<T>>> haal, IReadOnlyList<long> teamcodes)
    {
        using var poort = new SemaphoreSlim(GelijktijdigePerTeam);
        var taken = teamcodes.Distinct().Select(async code =>
        {
            await poort.WaitAsync();
            try { return await haal(MetFilter(url, $"teamcode={code}")); }
            finally { poort.Release(); }
        }).ToList();

        var resultaten = await Task.WhenAll(taken);
        return (resultaten.SelectMany(r => r).ToList(), resultaten.All(r => r.Count < Plafond));
    }

    private static string MetFilter(string url, string filter) => url + (url.Contains('?') ? "&" : "?") + filter;
}
