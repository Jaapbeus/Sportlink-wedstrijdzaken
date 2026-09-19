using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Infrastructure;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/SportlinkEndpointSupport.cs</c>
/// (#1266, epic #986): de gedeelde voorbereiding van élk Sportlink Web Extension-endpoint en élke
/// Sportlink-timer op deze tier.
/// <para>
/// <b>Geen kopie.</b> Alle beslislogica — de toggle+EgressGuard-controle, de vertaling van
/// <see cref="SportlinkClubCallStatus"/> naar een HTTP-status, de rolnaam, de dry-run-polariteit en
/// de audit-afronding — staat in <see cref="SportlinkEndpointCore"/> (Planner.Shared) en wordt door
/// beide tiers aangeroepen. Wat hier staat is uitsluitend tier-plumbing: de SQL Server-
/// instellingenlezer, de EgressGuard van deze assembly, de DI-lookup van de client en de vertaling
/// naar <see cref="IActionResult"/>. Zelfde scheiding als ThemeCore (#1248) en FeedbackCore (#1130).
/// </para>
/// </summary>
internal static class SportlinkEndpointSupport
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    internal const string RolWedstrijdzaken = SportlinkEndpointCore.RolWedstrijdzaken;

    /// <summary>Instellingenlezer van deze tier — één plek, zodat de cache-semantiek
    /// (<c>null</c> = nog niet geladen) overal gelijk is.</summary>
    private static Func<string, string?> LeesInstelling => SystemUtilities.AppSettings.GetSetting;

    /// <summary>Vertaalt een gedeelde foutuitkomst naar de HTTP-respons van deze tier.</summary>
    private static IActionResult NaarActionResult(SportlinkEndpointFout fout)
        => new ObjectResult(new { error = fout.Foutmelding }) { StatusCode = fout.HttpStatus };

    /// <summary>Toggle (#988) + EgressGuard (#857): <c>null</c> als de aanroep door mag, anders de
    /// 409/503-fout die de client toont.</summary>
    internal static IActionResult? ControleerToggleEnEgress()
    {
        var fout = SportlinkEndpointCore.ControleerToggleEnEgress(
            LeesInstelling, EgressGuard.ExternalIntegrationsAllowed);
        return fout == null ? null : NaarActionResult(fout);
    }

    /// <summary>Dry-run-stand van deze club (#998) — fail-safe: alles behalve een expliciet geladen
    /// <c>"0"</c> is dry-run. Zie <see cref="SportlinkEndpointCore.IsDryRunActief"/>.</summary>
    internal static bool IsDryRunActief() => SportlinkEndpointCore.IsDryRunActief(LeesInstelling);

    /// <summary>De <see cref="ISportlinkClubClient"/> uit DI, of een 503 als hij niet geregistreerd
    /// is (Program.cs registreert hem alleen als de EgressGuard het toestaat).</summary>
    internal static (ISportlinkClubClient? Client, IActionResult? Fout) ClientOfFout(FunctionContext context)
    {
        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        return client == null
            ? (null, NaarActionResult(SportlinkEndpointCore.ClientNietGeconfigureerdFout))
            : (client, null);
    }

    /// <summary>Timer-variant van de drie controles hierboven: logt waarom er niets gebeurt en geeft
    /// <c>null</c> terug, zodat elke timer met één regel kan afbreken.</summary>
    internal static ISportlinkClubClient? ClientVoorTimer(FunctionContext context, ILogger log, string taak)
    {
        switch (SportlinkEndpointCore.BepaalTimerStatus(LeesInstelling, EgressGuard.ExternalIntegrationsAllowed))
        {
            case SportlinkTimerStatus.ExtensieUit:
                log.LogInformation(SportlinkEndpointCore.TimerExtensieUitLog, taak);
                return null;
            case SportlinkTimerStatus.EgressGeblokkeerd:
                log.LogInformation(SportlinkEndpointCore.TimerEgressGeblokkeerdLog, taak);
                return null;
        }

        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (client == null)
            log.LogWarning(SportlinkEndpointCore.TimerClientOntbreektLog, taak);
        return client;
    }

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een HTTP-foutrespons — nooit de
    /// onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel). <c>null</c> bij <c>Ok</c>.</summary>
    internal static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
    {
        var fout = SportlinkEndpointCore.VertaalStatusNaarFout(status);
        return fout == null ? null : NaarActionResult(fout);
    }

    /// <summary>Request-body als DTO; <c>null</c> bij een lege body.</summary>
    internal static async Task<T?> LeesBodyAsync<T>(HttpRequest req) where T : class
        => JsonConvert.DeserializeObject<T>(await new StreamReader(req.Body).ReadToEndAsync());

    /// <summary>Audit-<c>resultaat</c> voor een mutatie-uitkomst (#998, uitgebreid #994).</summary>
    internal static string BepaalAuditResultaat(SportlinkMutationResult r)
        => SportlinkEndpointCore.BepaalAuditResultaat(r);

    /// <summary>
    /// De afronding die élke mutatie deelt: transportfout → audit "Failure" + vertaalde fout; lege
    /// respons → audit "Failure" + 502; anders audit met <see cref="BepaalAuditResultaat"/> en
    /// <paramref name="ok"/>. Altijd HTTP 200 bij een inhoudelijke afwijzing door Sportlink —
    /// <c>IsSuccess</c>/<c>Violations</c> dragen de uitkomst.
    /// </summary>
    internal static async Task<IActionResult> RondMutatieAfAsync<T>(
        SportlinkClubResponse<T> mutationResult,
        ISportlinkMutationAuditService? auditService,
        long? auditId,
        Func<T, SportlinkMutationResult> naarMutatieResultaat,
        Func<T, IActionResult> ok)
        where T : class
    {
        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(mutationResult, naarMutatieResultaat);
        await VoltooiAsync(auditService, auditId, afronding.AuditResultaat, afronding.AuditSamenvatting);
        return afronding.Fout != null ? NaarActionResult(afronding.Fout) : ok(afronding.Data!);
    }

    private static Task VoltooiAsync(ISportlinkMutationAuditService? auditService, long? auditId, string resultaat, string? samenvatting)
        => auditService != null && auditId.HasValue
            ? auditService.VoltooiAsync(auditId.Value, resultaat, samenvatting)
            : Task.CompletedTask;
}
