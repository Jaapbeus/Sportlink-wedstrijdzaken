using System.Net;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Sync;

/// <summary>
/// Begrensde herhaalpogingen voor idempotente GET-aanroepen naar de Sportlink-dataservice (#1779).
/// Eén tijdelijke 5xx/429/time-out mag een dagelijkse sync niet laten falen (#1081 blijft: een
/// fout die na de pogingen blijft, telt nog steeds als mislukte fase). Gedeeld door beide
/// database-tiers; alleen voor GET's, nooit voor mutaties.
/// <para>
/// Twee grenzen: per aanroep hoogstens <see cref="MaxPogingen"/> pogingen, en per sync-run een
/// gedeeld <see cref="HerhaalBudget"/>. Zonder dat runbrede budget zou een brede storing (elke
/// wedstrijdcode faalt) elke aanroep verdrievoudigen en de run over de <c>functionTimeout</c>
/// duwen, vóór merge, reconciliatie en foutrapportage (#1081). Is het budget op, dan doet elke
/// volgende aanroep weer één poging, zoals vóór #1779.
/// </para>
/// </summary>
public static class SportlinkDataserviceHttp
{
    public const int MaxPogingen = 3;
    public static readonly TimeSpan StandaardWachttijd = TimeSpan.FromSeconds(2);

    /// <summary>Tijdelijke statuscodes die het herhalen waard zijn: 5xx, 429 en 408.</summary>
    public static bool IsTransiente(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    /// <summary>
    /// GET met hoogstens <see cref="MaxPogingen"/> pogingen, zolang <paramref name="budget"/> nog
    /// herhalingen toestaat. Geeft de laatste respons terug (de aanroeper roept
    /// <c>EnsureSuccessStatusCode</c> aan); een netwerkfout of time-out die niet meer herhaald mag
    /// worden, wordt doorgegooid. Meldt nooit de URL (#1200: de clientId zit in de querystring).
    /// </summary>
    public static async Task<HttpResponseMessage> GetMetHerhalingAsync(
        HttpClient client, string url, HerhaalBudget budget, Action<string>? meld = null,
        TimeSpan? wachttijd = null, CancellationToken ct = default)
    {
        var wacht = wachttijd ?? StandaardWachttijd;
        for (var poging = 1; ; poging++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct);
            }
            catch (HttpRequestException) when (MagOpnieuw(poging, budget, meld, "netwerkfout"))
            {
                await WachtAsync(wacht, ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && MagOpnieuw(poging, budget, meld, "time-out"))
            {
                await WachtAsync(wacht, ct);
                continue;
            }

            if (!IsTransiente(response.StatusCode)
                || !MagOpnieuw(poging, budget, meld, $"HTTP {(int)response.StatusCode}"))
                return response;
            response.Dispose();
            await WachtAsync(wacht, ct);
        }
    }

    /// <summary>
    /// Haalt de respons van een dataservice-GET op als tekst, met begrensde herhaling. Gooit bij
    /// een blijvende fout (zoals <c>EnsureSuccessStatusCode</c>). <paramref name="fase"/> is een
    /// vast label voor het log; de URL wordt nooit gelogd (#1200).
    /// </summary>
    public static async Task<string> HaalTekstAsync(
        HttpClient client, string url, HerhaalBudget budget, string fase, ILogger log,
        TimeSpan? wachttijd = null, CancellationToken ct = default)
    {
        using var response = await GetMetHerhalingAsync(
            client, url, budget,
            reden => log.LogWarning("{Fase} - tijdelijke fout: {Reden}", fase, reden),
            wachttijd, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static bool MagOpnieuw(int poging, HerhaalBudget budget, Action<string>? meld, string reden)
    {
        if (poging >= MaxPogingen)
            return false;
        if (!budget.Neem())
        {
            meld?.Invoke($"{reden}; herhaalbudget van deze run is op, geen nieuwe poging");
            return false;
        }
        meld?.Invoke($"{reden}; opnieuw proberen");
        return true;
    }

    private static Task WachtAsync(TimeSpan wacht, CancellationToken ct)
        => wacht > TimeSpan.Zero ? Task.Delay(wacht, ct) : Task.CompletedTask;
}

/// <summary>
/// Runbreed budget voor extra pogingen (#1779): één instantie per sync-run, gedeeld door alle
/// dataservice-GET's van die run. Begrenst het totaal aantal aanroepen op
/// <c>aantal GET's + <see cref="Resterend"/></c>, ook als de dataservice volledig uitligt.
/// </summary>
public sealed class HerhaalBudget
{
    /// <summary>
    /// Standaard extra pogingen per run. Een korte storing raakt doorgaans een handvol aanroepen;
    /// zes herhalingen kosten hoogstens zes extra aanroepen en 6 × 2 s wachttijd.
    /// </summary>
    public const int StandaardPerRun = 6;

    private int _resterend;

    public HerhaalBudget(int extraPogingen = StandaardPerRun)
        => _resterend = Math.Max(0, extraPogingen);

    public int Resterend => Volatile.Read(ref _resterend);

    /// <summary>Neemt één extra poging uit het budget; <c>false</c> als het budget op is.</summary>
    public bool Neem()
    {
        while (true)
        {
            var huidig = Volatile.Read(ref _resterend);
            if (huidig <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _resterend, huidig - 1, huidig) == huidig)
                return true;
        }
    }
}
