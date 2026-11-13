namespace Planner.Shared.Sync;

/// <summary>
/// Haalt een <c>/programma</c>-venster van de Sportlink-dataservice volledig op, ook als dat meer dan 100 wedstrijden
/// bevat (#1915). Beide tiers (<c>PostgresSyncPipeline</c>, <c>SportlinkSyncPipeline</c>) gebruiken deze ene plek.
/// <para>
/// <b>Waarom.</b> <c>/programma</c> geeft per aanroep maximaal <see cref="Plafond"/> rijen terug, oplopend in tijd; de
/// rest valt zonder foutmelding weg (live vastgesteld, ook met <c>aantaldagen</c> of <c>aantalrecords</c>). Het
/// venster loopt vanaf het aanroepmoment (<c>nu + 7 dagen × weekoffset</c>) en kent geen datumparameter, dus een
/// afgekapte speeldag is niet gericht opnieuw op te vragen. Op een drukke zaterdag verdween zo het einde van de dag
/// uit staging, waarna de reconciliatie (<c>ReconcileWindowedAsync</c>, venster uit MIN/MAX van de datum) die
/// wedstrijden als verwijderd markeerde — en Sportlink ze nooit meer teruggaf.
/// </para>
/// <para>
/// <b>Hoe.</b> Raakt een aanroep het plafond, dan volgt dezelfde aanroep gesplitst in alleen thuis en alleen uit
/// (<c>thuis=JA&amp;uit=NEE</c> en <c>thuis=NEE&amp;uit=JA</c>). Dat zijn twee disjuncte verzamelingen van het eigen
/// programma die samen het hele venster dekken; een wedstrijd tussen twee eigen teams staat in beide, en de opslag
/// ontdubbelt op wedstrijdcode. Is ook een van beide nog afgekapt, dan is de ophaling <see cref="Ophaling{T}.Volledig"/>
/// <c>false</c>: de aanroeper moet dat als onvolledig behandelen en dus niet reconciliëren.
/// </para>
/// </summary>
public static class ProgrammaAfkapping
{
    /// <summary>Het maximale aantal rijen dat <c>/programma</c> per aanroep teruggeeft.</summary>
    public const int Plafond = 100;

    /// <summary>De thuis- en uit-verzameling van het eigen programma (disjunct, samen het hele venster).</summary>
    public const string FilterAlleenThuis = "thuis=JA&uit=NEE";

    /// <inheritdoc cref="FilterAlleenThuis"/>
    public const string FilterAlleenUit = "thuis=NEE&uit=JA";

    /// <param name="Rijen">Alle rijen die binnenkwamen; mogelijk met dubbelen (zie de klasse-documentatie).</param>
    /// <param name="Volledig"><c>false</c> als ook na splitsing een aanroep het plafond raakte: dan ontbreken er rijen.</param>
    /// <param name="Gesplitst">Of de ophaling in thuis en uit is gesplitst.</param>
    public sealed record Ophaling<T>(IReadOnlyList<T> Rijen, bool Volledig, bool Gesplitst);

    /// <param name="url">De volledige aanroep-URL inclusief <c>?</c>-query; wordt nooit gelogd (bevat de clientId).</param>
    /// <param name="haal">Voert één aanroep uit en geeft de rijen terug; een fout propageert naar de aanroeper.</param>
    public static async Task<Ophaling<T>> HaalVolledigAsync<T>(string url, Func<string, Task<IReadOnlyList<T>>> haal)
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

        return new Ophaling<T>(samen, thuis.Count < Plafond && uit.Count < Plafond, true);
    }

    private static string MetFilter(string url, string filter) => url + (url.Contains('?') ? "&" : "?") + filter;
}
