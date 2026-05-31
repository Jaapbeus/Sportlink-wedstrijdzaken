using System.Net.Http.Json;

namespace BlazorAdmin.Services;

public enum DbState { Unknown, Starting, Online, LimietBereikt }

/// <summary>
/// Pollt /api/health en bewaakt de Azure SQL Free-tier database-status.
/// Start bij authenticatie; blokkeert de app-UI als de DB niet online komt.
///
/// State machine:
///   Unknown    → (eerste poll) → Online  (succes binnen 2 min)
///                              → Starting (mislukking, bezig met resumeren)
///                                → ... → Online  (hersteld)
///                                      → LimietBereikt (na 2 min geen succes)
///   LimietBereikt → elke 5 min opnieuw checken (volgende maand of handmatig herstart)
///   Online        → elke 5 min alive-check; bij mislukking terug naar Starting
/// </summary>
public sealed class DatabaseStatusService : IDisposable
{
    public DbState State { get; private set; } = DbState.Unknown;
    public event Action? OnStateChanged;

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private bool _polling;

    public DatabaseStatusService(HttpClient http) => _http = http;

    public void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        _ = PollLoopAsync();
    }

    private async Task PollLoopAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var startedAt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var resp = await _http.GetFromJsonAsync<HealthResponse>("api/health", token);
                var dbOnline = resp?.database == "online";

                if (dbOnline)
                {
                    SetState(DbState.Online);
                    startedAt = DateTime.UtcNow; // reset: volgende storing krijgt weer 2 min
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    continue;
                }

                // DB niet online — bepaal hoe lang we al wachten
                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed > TimeSpan.FromMinutes(2))
                {
                    SetState(DbState.LimietBereikt);
                    // Blijf elke 5 min checken: bij maandwisseling of handmatige herstart
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    startedAt = DateTime.UtcNow; // reset window voor volgende cyclus
                    continue;
                }

                SetState(DbState.Starting);
            }
            catch (OperationCanceledException) { break; }
            catch { /* netwerk/parse-fout — opnieuw proberen */ }

            await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
    }

    private void SetState(DbState next)
    {
        if (State == next) return;
        State = next;
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private sealed class HealthResponse
    {
        public string? status { get; set; }
        public string? database { get; set; }
    }
}
