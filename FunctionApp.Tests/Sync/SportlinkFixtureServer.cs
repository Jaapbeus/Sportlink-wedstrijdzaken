using System.Net;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Minimale, wegwerpbare lokale HTTP-server die opgenomen antwoorden teruggeeft op de vier
/// Sportlink-endpoints die <c>SportlinkSyncPipeline</c> aanroept (#867). Geen nieuwe NuGet-
/// afhankelijkheid: <see cref="HttpListener"/> zit al in de BCL en is voldoende voor "geef dit
/// vaste antwoord terug op dit pad".
/// <para>
/// <b>Waarom een echte, luisterende server in plaats van een gemockte <c>HttpMessageHandler</c>:</b>
/// <c>SportlinkSyncPipeline</c> gebruikt intern een eigen, niet-injecteerbare <c>HttpClient</c>
/// (op één na — <see cref="SportlinkFunction.SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync"/>
/// accepteert al een optionele client, #476). De enige testbare naad is de <c>sportlinkApiUrl</c>-
/// parameter van <c>RunSyncAsync</c> zelf — die moet dus een écht bereikbaar adres zijn.
/// </para>
/// <para>
/// <b>"Geen enkele externe dienst geraakt" — aantoonbaar, niet aangenomen:</b> <see cref="Requests"/>
/// registreert elk binnengekomen verzoek (pad + query). Een test kan zo exact verifiëren welke
/// endpoints zijn aangeroepen, in plaats van te vertrouwen op de afwezigheid van een fout — precies
/// het onderscheid dat #867 vereist ("te controleren zonder de betrokken diensten te hoeven
/// vertrouwen"). Omdat <c>sportlinkApiUrl</c> de ENIGE plek is waar de pipeline een basis-adres
/// vandaan haalt, is er geen ander pad waarlangs de pipeline een andere host zou kunnen bereiken.
/// </para>
/// </summary>
public sealed class SportlinkFixtureServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, Func<HttpListenerRequest, string>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public string BaseUrl { get; }

    /// <summary>Elk binnengekomen verzoek: absolute pad + querystring, in aankomstvolgorde.</summary>
    public List<string> Requests { get; } = new();

    public SportlinkFixtureServer()
    {
        var port = GetFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Registreert een JSON-antwoord voor een pad (bijv. "/teams") — genegeerd wordt de querystring.</summary>
    public void RespondWithJson(string path, Func<HttpListenerRequest, string> jsonFactory)
        => _routes[path] = jsonFactory;

    public void RespondWithJson(string path, string json) => RespondWithJson(path, _ => json);

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                return; // Stop() geworpen tijdens Dispose — normale afsluiting.
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            lock (Requests)
                Requests.Add(path + (ctx.Request.Url?.Query ?? ""));

            if (_routes.TryGetValue(path, out var factory))
            {
                var json = factory(ctx.Request);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
