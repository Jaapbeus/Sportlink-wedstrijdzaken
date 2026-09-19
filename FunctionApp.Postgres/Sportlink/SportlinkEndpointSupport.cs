using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// De stappen die élk Sportlink Web Extension-endpoint en élke Sportlink-timer deelt (#1122, epic
/// #986). Tot deze klasse bestond stonden ze gekopieerd: de toggle+EgressGuard-controle zes keer,
/// de statusvertaling drie keer, de audit-afronding drie keer en de rolnaam zes keer. Een nieuw
/// endpoint dat één van deze stappen vergeet is precies het risico dat #857 (EgressGuard) en #998
/// (audit) wilden uitsluiten — daarom één plek.
/// <para>
/// Sinds #1266 staat de beslislogica zelf in <see cref="SportlinkEndpointCore"/> (Planner.Shared),
/// omdat de SQL Server-tier dezelfde regels nodig heeft. Wat hier overblijft is tier-plumbing: de
/// Postgres-instellingenlezer, de Postgres-EgressGuard, de DI-lookup van de client en de vertaling
/// naar <see cref="IActionResult"/>.
/// </para>
/// </summary>
internal static class SportlinkEndpointSupport
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    internal const string RolWedstrijdzaken = SportlinkEndpointCore.RolWedstrijdzaken;

    /// <summary>Vertaalt een gedeelde foutuitkomst naar de HTTP-respons van deze tier.</summary>
    private static IActionResult NaarActionResult(SportlinkEndpointFout fout)
        => new ObjectResult(new { error = fout.Foutmelding }) { StatusCode = fout.HttpStatus };

    /// <summary>Toggle (#988) + EgressGuard (#857): <c>null</c> als de aanroep door mag, anders de
    /// 409/503-fout die de client toont.</summary>
    internal static IActionResult? ControleerToggleEnEgress()
    {
        var fout = SportlinkEndpointCore.ControleerToggleEnEgress(
            PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed);
        return fout == null ? null : NaarActionResult(fout);
    }

    /// <summary>Dry-run-stand van deze club (#998) — fail-safe, zie
    /// <see cref="SportlinkEndpointCore.IsDryRunActief"/>.</summary>
    internal static bool IsDryRunActief() => SportlinkEndpointCore.IsDryRunActief(PostgresAppSettings.GetSetting);

    /// <summary>
    /// Voert een Sportlink-endpoint uit met BEIDE poorten: eerst de functionele rol
    /// <c>Wedstrijdzaken</c>, daarna de gewone admin-controle van
    /// <see cref="AdminEndpoint.ExecuteAsync"/>.
    /// <para>
    /// <b>Gewijzigd bij #1272.</b> Tot dan gaf deze tier <c>requireRole:</c> mee aan
    /// <c>AdminEndpoint.ExecuteAsync</c>, waar het de admin-controle <i>verving</i> in plaats van
    /// er bovenop te komen. Dat sprak §3.4 van docs/SPORTLINK-WEB-EXTENSION.md tegen, dat de rol
    /// uitdrukkelijk omschrijft als een extra slot "bovenop de bestaande admin-toegang", en het
    /// leverde een recht op dat alleen buiten de applicatie om bruikbaar was: de Admin GUI poort
    /// in App.razor op <c>admin</c> of <c>user</c>, dus iemand met alléén <c>Wedstrijdzaken</c>
    /// kon de interface niet laden maar deze endpoints wél rechtstreeks aanroepen.
    /// </para>
    /// <para>
    /// Beide tiers gebruiken nu dezelfde wrapper met dezelfde volgorde — geen tierverschil meer.
    /// </para>
    /// </summary>
    internal static Task<IActionResult> ExecuteWedstrijdzakenAsync(
        HttpRequest req, ILogger log, string errorContext, Func<string, Task<IActionResult>> work)
    {
        var rolFout = EasyAuthHelper.RequireWedstrijdzaken(req);
        if (rolFout != null) return Task.FromResult(rolFout);
        return AdminEndpoint.ExecuteAsync(req, log, errorContext, work);
    }

    /// <summary>De 503 voor "client niet in DI geregistreerd", als losse respons — voor de paden die
    /// de client zelf uit <see cref="FunctionContext.InstanceServices"/> halen in plaats van via
    /// <see cref="ClientOfFout"/>. Sinds #1266 komt de melding uit
    /// <see cref="SportlinkEndpointCore"/>, zodat beide tiers dezelfde tekst en status geven.</summary>
    internal static IActionResult ClientNietGeconfigureerdFout()
        => NaarActionResult(SportlinkEndpointCore.ClientNietGeconfigureerdFout);

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
        switch (SportlinkEndpointCore.BepaalTimerStatus(
                    PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed))
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
    /// <c>IsSuccess</c>/<c>Violations</c> dragen de uitkomst (consistent met AdminApiClient).
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
