using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Planner.Endpoints.Instellingen;

/// <summary>
/// Laat de delegate eenmaal slagen: thread-safe, goedkoop na succes, een mislukte of gooiende
/// poging wordt bij de volgende aanroep opnieuw gedaan en propageert nooit.
/// </summary>
/// <remarks>#1515: op Flex Consumption draait elke niet-HTTP-trigger op een eigen instantie; de procesbrede instellingencache werd alleen door enkele endpoints gevuld, dus timers lazen <c>sportlinkExtensionEnabled</c> = null. De tier levert alleen de laad-delegate (true = geslaagd); de Program.cs van elke tier roept deze guard aan via <c>UseMiddleware</c> vóór elke functie.</remarks>
public sealed class InstellingenLaadGuard
{
    private readonly Func<ILogger, Task<bool>> _lader;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private volatile bool _geladen;

    public InstellingenLaadGuard(Func<ILogger, Task<bool>> lader) => _lader = lader;

    public bool IsGeladen => _geladen;

    public async Task EnsureLoadedAsync(ILogger log)
    {
        if (_geladen) return;
        await _slot.WaitAsync();
        try
        {
            if (_geladen) return;
            try
            {
                _geladen = await _lader(log);
                if (!_geladen)
                    log.LogWarning("Instellingen laden mislukt voor deze instantie; volgende aanroep probeert opnieuw.");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Instellingen laden gaf een fout voor deze instantie; volgende aanroep probeert opnieuw.");
            }
        }
        finally
        {
            _slot.Release();
        }
    }
}

/// <summary>#1515: registratie van de guard als worker-middleware — één regel per tier.</summary>
public static class InstellingenLaadRegistratie
{
    public static IFunctionsWorkerApplicationBuilder UseInstellingenLader(
        this IFunctionsWorkerApplicationBuilder builder, Func<ILogger, Task<bool>> lader)
    {
        var guard = new InstellingenLaadGuard(lader);
        return builder.UseMiddleware(async (ctx, next) =>
        {
            await guard.EnsureLoadedAsync(ctx.GetLogger("Instellingen"));
            await next();
        });
    }
}
