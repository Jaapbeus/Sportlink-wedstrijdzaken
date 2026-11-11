using System.Net;

namespace Planner.Shared.Sync;

/// <summary>
/// Begrensde herhaalpogingen voor idempotente GET-aanroepen naar de Sportlink-dataservice (#1779).
/// Eén tijdelijke 5xx/429/time-out mag een dagelijkse sync niet laten falen (#1081 blijft: een
/// fout die na de pogingen blijft, telt nog steeds als mislukte fase). Gedeeld door beide
/// database-tiers; alleen voor GET's, nooit voor mutaties.
/// </summary>
public static class SportlinkDataserviceHttp
{
    public const int MaxPogingen = 3;
    public static readonly TimeSpan StandaardWachttijd = TimeSpan.FromSeconds(2);

    /// <summary>Tijdelijke statuscodes die het herhalen waard zijn: 5xx, 429 en 408.</summary>
    public static bool IsTransiente(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    /// <summary>
    /// GET met maximaal <see cref="MaxPogingen"/> pogingen. Geeft de laatste respons terug (de
    /// aanroeper roept <c>EnsureSuccessStatusCode</c> aan); een netwerkfout of time-out bij de laatste
    /// poging wordt doorgegooid. Logt nooit de URL (#1200: de clientId zit in de querystring).
    /// </summary>
    public static async Task<HttpResponseMessage> GetMetHerhalingAsync(
        HttpClient client, string url, Action<string>? meld = null,
        TimeSpan? wachttijd = null, CancellationToken ct = default)
    {
        var wacht = wachttijd ?? StandaardWachttijd;
        for (var poging = 1; ; poging++)
        {
            var laatste = poging >= MaxPogingen;
            try
            {
                var response = await client.GetAsync(url, ct);
                if (laatste || !IsTransiente(response.StatusCode))
                    return response;
                meld?.Invoke($"HTTP {(int)response.StatusCode}");
                response.Dispose();
            }
            catch (HttpRequestException) when (!laatste)
            {
                meld?.Invoke("netwerkfout");
            }
            catch (TaskCanceledException) when (!laatste && !ct.IsCancellationRequested)
            {
                meld?.Invoke("time-out");
            }
            if (wacht > TimeSpan.Zero)
                await Task.Delay(wacht, ct);
        }
    }
}
