using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Planner.Shared.Feedback;

namespace BlazorAdmin.Services;

/// <summary>
/// Verzamelt de technische context die de feedbackwidget bij een melding kan meesturen (#764):
/// de laatste mislukte API-aanroepen, het navigatiespoor, de console-fouten (uit
/// <c>wwwroot/js/feedback-telemetry.js</c>) en de browser. Alles in een ringbuffer van vijf per
/// soort; er wordt niets bewaard buiten het geheugen van deze sessie.
///
/// <para>
/// Alles wat hier uitkomt is al geredigeerd met <see cref="FeedbackTelemetrieSaneerder"/> — dezelfde
/// regels als op de server, zodat het paneel in de widget letterlijk toont wat er wordt verstuurd.
/// </para>
/// </summary>
public sealed class ClientTelemetryService : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly IJSRuntime _js;
    private readonly List<FeedbackApiFout> _aanroepen = [];
    private readonly List<string> _navigatie = [];
    private readonly object _slot = new();

    public ClientTelemetryService(NavigationManager nav, IJSRuntime js)
    {
        _nav = nav;
        _js = js;
        NoteerRoute(_nav.Uri);
        _nav.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        NoteerRoute(e.Location);

    private void NoteerRoute(string uri)
    {
        var pad = "/" + _nav.ToBaseRelativePath(uri);
        var route = FeedbackRedactie.RedigeerRoute(pad);
        lock (_slot)
        {
            if (_navigatie.Count > 0 && _navigatie[^1] == route) return;
            _navigatie.Add(route);
            while (_navigatie.Count > FeedbackTelemetrieSaneerder.MaxItemsPerSoort + 1)
                _navigatie.RemoveAt(0);
        }
    }

    /// <summary>Legt een mislukte API-aanroep vast (HTTP-fout of netwerkfout, dan status 0).</summary>
    public void MeldMislukteAanroep(string methode, string pad, int status, string? correlatieId)
    {
        lock (_slot)
        {
            _aanroepen.Add(new FeedbackApiFout
            {
                Methode = methode,
                // Alleen het pad: de querystring kan zoekwaarden of namen bevatten.
                Pad = FeedbackRedactie.RedigeerRoute("/" + pad.TrimStart('/')),
                Status = status,
                TijdUtc = DateTime.UtcNow,
                CorrelatieId = correlatieId
            });
            while (_aanroepen.Count > FeedbackTelemetrieSaneerder.MaxItemsPerSoort)
                _aanroepen.RemoveAt(0);
        }
    }

    /// <summary>
    /// De huidige technische context, geredigeerd. Het navigatiespoor bevat de routes vóór de
    /// huidige pagina (de huidige staat al in de melding zelf).
    /// </summary>
    public async Task<FeedbackTelemetrie> VerzamelAsync()
    {
        var consoleFouten = await ProbeerAsync<string[]>("feedbackTelemetry.consoleFouten") ?? [];
        var breedte = await ProbeerAsync<int>("feedbackTelemetry.schermbreedte");
        var browser = await ProbeerAsync<string>("blazorHelpers.getUserAgent");

        FeedbackTelemetrie ruw;
        lock (_slot)
        {
            ruw = new FeedbackTelemetrie
            {
                ConsoleFouten = [.. consoleFouten],
                MislukteAanroepen = [.. _aanroepen],
                Navigatiespoor = _navigatie.Count > 1 ? [.. _navigatie.Take(_navigatie.Count - 1)] : [],
                Browser = browser is { Length: > 120 } b ? b[..120] : browser,
                Schermbreedte = breedte > 0 ? breedte : null
            };
        }
        return FeedbackTelemetrieSaneerder.Saneer(ruw) ?? new FeedbackTelemetrie();
    }

    // Faalt de interop (helper nog niet geladen, of een test zonder browser), dan blijft de widget
    // bruikbaar met een lege waarde — dezelfde terugval als GetUserAgentAsync (#597).
    private async Task<T?> ProbeerAsync<T>(string functie)
    {
        try { return await _js.InvokeAsync<T>(functie); }
        catch (JSException) { return default; }
        catch (InvalidOperationException) { return default; }
    }

    public void Dispose() => _nav.LocationChanged -= OnLocationChanged;
}
