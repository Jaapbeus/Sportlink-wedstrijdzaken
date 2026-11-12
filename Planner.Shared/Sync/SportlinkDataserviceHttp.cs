using System.Net;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Sync;

/// <summary>
/// Begrensde herhaalpogingen voor idempotente GET-aanroepen naar de Sportlink-dataservice (#1779).
/// Eén tijdelijke 5xx/429/time-out mag een dagelijkse sync niet laten falen (#1081 blijft: een
/// fout die na de pogingen blijft, telt nog steeds als mislukte fase). Gedeeld door beide
/// database-tiers; alleen voor GET's, nooit voor mutaties.
/// <para>
/// Grenzen: per aanroep hoogstens <see cref="MaxPogingen"/> pogingen, en per sync-run een gedeeld
/// <see cref="HerhaalBudget"/> dat zowel het aantal herhalingen als hun looptijd begrenst. Zonder
/// die runbrede grenzen zou een brede storing (elke wedstrijdcode faalt of hangt) elke aanroep
/// verdrievoudigen en de run over de <c>functionTimeout</c> duwen, vóór merge, reconciliatie en
/// foutrapportage (#1081). Is het budget op, dan doet elke volgende aanroep weer één poging,
/// zoals vóór #1779.
/// </para>
/// <para>
/// Tijdsgrens: een herhaalde poging duurt hoogstens <see cref="HerhaalBudget.PogingLimiet"/> en
/// nooit voorbij de runbrede deadline (<see cref="HerhaalBudget.Looptijd"/> na de start van de
/// run); na die deadline start geen herhaling meer. De eerste poging van elke aanroep houdt de
/// time-out van de <see cref="HttpClient"/>, zoals vóór #1779.
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
                response = await GetPogingAsync(client, url, budget, poging, ct);
            }
            catch (HttpRequestException) when (MagOpnieuw(poging, budget, meld, "netwerkfout", wacht))
            {
                await WachtAsync(wacht, budget, ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && MagOpnieuw(poging, budget, meld, "time-out", wacht))
            {
                await WachtAsync(wacht, budget, ct);
                continue;
            }

            if (!IsTransiente(response.StatusCode)
                || !MagOpnieuw(poging, budget, meld, $"HTTP {(int)response.StatusCode}", wacht))
                return response;
            response.Dispose();
            await WachtAsync(wacht, budget, ct);
        }
    }

    /// <summary>
    /// Eén GET. Een herhaalde poging (poging &gt; 1) wordt afgebroken na
    /// <see cref="HerhaalBudget.PogingLimiet"/> of bij de runbrede deadline, wat het eerst komt; die
    /// afbreking is een <see cref="TaskCanceledException"/> zonder geannuleerde <paramref name="ct"/>
    /// en telt dus als time-out, niet als annulering door de aanroeper.
    /// </summary>
    private static async Task<HttpResponseMessage> GetPogingAsync(
        HttpClient client, string url, HerhaalBudget budget, int poging, CancellationToken ct)
    {
        if (poging == 1)
            return await client.GetAsync(url, ct);

        var limiet = budget.ResterendeLooptijd < budget.PogingLimiet ? budget.ResterendeLooptijd : budget.PogingLimiet;
        using var pogingCts = new CancellationTokenSource(limiet > TimeSpan.Zero ? limiet : TimeSpan.Zero, budget.Klok);
        using var gekoppeld = CancellationTokenSource.CreateLinkedTokenSource(ct, pogingCts.Token);
        return await client.GetAsync(url, gekoppeld.Token);
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

    private static bool MagOpnieuw(int poging, HerhaalBudget budget, Action<string>? meld, string reden, TimeSpan wacht)
    {
        if (poging >= MaxPogingen)
            return false;
        if (budget.ResterendeLooptijd <= wacht)
        {
            meld?.Invoke($"{reden}; looptijd voor herhalingen van deze run is op, geen nieuwe poging");
            return false;
        }
        if (!budget.Neem())
        {
            meld?.Invoke($"{reden}; herhaalbudget van deze run is op, geen nieuwe poging");
            return false;
        }
        meld?.Invoke($"{reden}; opnieuw proberen");
        return true;
    }

    private static Task WachtAsync(TimeSpan wacht, HerhaalBudget budget, CancellationToken ct)
        => wacht > TimeSpan.Zero ? Task.Delay(wacht, budget.Klok, ct) : Task.CompletedTask;
}

/// <summary>
/// Runbreed budget voor extra pogingen (#1779): één instantie per sync-run, aangemaakt aan het
/// begin van de run en gedeeld door alle dataservice-GET's van die run. Begrenst het aantal
/// aanroepen op <c>aantal GET's + <see cref="Resterend"/></c> en de tijd die herhalingen kosten:
/// elke herhaling (wachttijd plus herhaalde poging) valt vóór de deadline <see cref="Looptijd"/>
/// na de start, en een herhaalde poging duurt hoogstens <see cref="PogingLimiet"/>. Ook als de
/// dataservice hangt in plaats van een fout te geven, kosten herhalingen dus hoogstens
/// <c>StandaardPerRun × (wachttijd + PogingLimiet)</c> en nooit tijd na de deadline.
/// </summary>
public sealed class HerhaalBudget
{
    /// <summary>
    /// Standaard extra pogingen per run. Een korte storing raakt doorgaans een handvol aanroepen;
    /// zes herhalingen kosten hoogstens zes extra aanroepen van elk hoogstens
    /// <see cref="StandaardPogingLimiet"/>, plus 6 × 2 s wachttijd.
    /// </summary>
    public const int StandaardPerRun = 6;

    /// <summary>
    /// Runbrede deadline voor herhalingen: ruim onder de <c>functionTimeout</c> van 10 minuten in
    /// <c>host.json</c> van beide tiers, zodat merge, reconciliatie en foutrapportage (#1081) na
    /// de laatste herhaling nog tijd hebben. Een test bewaakt die marge tegen beide host.json's.
    /// </summary>
    public static readonly TimeSpan StandaardLooptijd = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximale duur van één herhaalde poging; korter dan de standaardtime-out van 100 s van een
    /// <see cref="HttpClient"/>, zodat een hangende dataservice het budget niet in minuten opmaakt.
    /// </summary>
    public static readonly TimeSpan StandaardPogingLimiet = TimeSpan.FromSeconds(30);

    private int _resterend;
    private readonly long _start;

    public HerhaalBudget(
        int extraPogingen = StandaardPerRun, TimeSpan? looptijd = null,
        TimeSpan? pogingLimiet = null, TimeProvider? klok = null)
    {
        _resterend = Math.Max(0, extraPogingen);
        Looptijd = looptijd ?? StandaardLooptijd;
        PogingLimiet = pogingLimiet ?? StandaardPogingLimiet;
        Klok = klok ?? TimeProvider.System;
        _start = Klok.GetTimestamp();
    }

    public int Resterend => Volatile.Read(ref _resterend);

    /// <summary>Tijd na de start van de run waarna geen herhaling meer loopt.</summary>
    public TimeSpan Looptijd { get; }

    /// <summary>Maximale duur van één herhaalde poging.</summary>
    public TimeSpan PogingLimiet { get; }

    /// <summary>Klok voor deadline, poginglimiet en wachttijd (vervangbaar in tests).</summary>
    public TimeProvider Klok { get; }

    /// <summary>Tijd tot de runbrede deadline; nul of negatief als die voorbij is.</summary>
    public TimeSpan ResterendeLooptijd => Looptijd - Klok.GetElapsedTime(_start);
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
