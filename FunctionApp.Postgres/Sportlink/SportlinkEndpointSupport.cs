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
/// </summary>
internal static class SportlinkEndpointSupport
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    internal const string RolWedstrijdzaken = "Wedstrijdzaken";

    /// <summary>Toggle (#988) + EgressGuard (#857): <c>null</c> als de aanroep door mag, anders de
    /// 409/503-fout die de client toont.</summary>
    internal static IActionResult? ControleerToggleEnEgress()
    {
        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
            return new ObjectResult(new { error = "Sportlink Web Extension staat uit." }) { StatusCode = 409 };
        if (!EgressGuard.ExternalIntegrationsAllowed())
            return new ObjectResult(new { error = "Uitgaande integraties staan hier niet toe." }) { StatusCode = 503 };
        return null;
    }

    /// <summary>De <see cref="ISportlinkClubClient"/> uit DI, of een 503 als hij niet geregistreerd
    /// is (Program.cs registreert hem alleen als de EgressGuard het toestaat).</summary>
    internal static (ISportlinkClubClient? Client, IActionResult? Fout) ClientOfFout(FunctionContext context)
    {
        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        return client == null
            ? (null, new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 })
            : (client, null);
    }

    /// <summary>Timer-variant van de drie controles hierboven: logt waarom er niets gebeurt en geeft
    /// <c>null</c> terug, zodat elke timer met één regel kan afbreken.</summary>
    internal static ISportlinkClubClient? ClientVoorTimer(FunctionContext context, ILogger log, string taak)
    {
        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
        {
            log.LogInformation("Sportlink Web Extension staat uit — {Taak} overgeslagen.", taak);
            return null;
        }
        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — {Taak} overgeslagen (#857).", taak);
            return null;
        }
        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (client == null)
            log.LogWarning("ISportlinkClubClient niet geregistreerd — {Taak} kan niet draaien.", taak);
        return client;
    }

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een HTTP-foutrespons — nooit de
    /// onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel). <c>null</c> bij <c>Ok</c>.</summary>
    internal static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status) => status switch
    {
        SportlinkClubCallStatus.Ok => null,
        SportlinkClubCallStatus.RolNietGekoppeld => new ObjectResult(new
        {
            error = $"Geen Sportlink-koppeling gevonden voor rol '{RolWedstrijdzaken}' — registreer eerst een refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        SportlinkClubCallStatus.HerkoppelingVereist => new ObjectResult(new
        {
            error = $"De Sportlink-koppeling voor rol '{RolWedstrijdzaken}' is verlopen — registreer een nieuw refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        _ => new ObjectResult(new { error = "Sportlink is momenteel niet bereikbaar." }) { StatusCode = 502 },
    };

    /// <summary>Request-body als DTO; <c>null</c> bij een lege body.</summary>
    internal static async Task<T?> LeesBodyAsync<T>(HttpRequest req) where T : class
        => JsonConvert.DeserializeObject<T>(await new StreamReader(req.Body).ReadToEndAsync());

    /// <summary>
    /// Audit-<c>resultaat</c> voor een mutatie-uitkomst (#998, uitgebreid #994). <c>IsForcedDryRun</c>
    /// gaat vóór <c>IsDryRun</c>, dat vóór <c>IsSuccess</c>: een code-gelockte, nog niet live
    /// bevestigde mutatie moet in de audit herkenbaar blijven naast een dry-run door de
    /// club-instelling — bij beide is <c>IsSuccess</c> altijd <c>true</c> (gesimuleerd succes).
    /// </summary>
    internal static string BepaalAuditResultaat(SportlinkMutationResult r) =>
        r.IsForcedDryRun ? "DryRunLocked" : r.IsDryRun ? "DryRun" : r.IsSuccess ? "Success" : "Failure";

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
        var fout = VertaalStatusNaarFout(mutationResult.Status);
        if (fout != null)
        {
            await VoltooiAsync(auditService, auditId, "Failure", mutationResult.FoutmeldingVoorLog);
            return fout;
        }
        if (mutationResult.Data == null)
        {
            await VoltooiAsync(auditService, auditId, "Failure", "Geen respons-data van Sportlink");
            return new ObjectResult(new { error = "Sportlink gaf geen bruikbare respons." }) { StatusCode = 502 };
        }

        var resultaat = naarMutatieResultaat(mutationResult.Data);
        var violations = resultaat.Violations is { Count: > 0 } ? string.Join(", ", resultaat.Violations) : null;
        await VoltooiAsync(auditService, auditId, BepaalAuditResultaat(resultaat), violations);
        return ok(mutationResult.Data);
    }

    private static Task VoltooiAsync(ISportlinkMutationAuditService? auditService, long? auditId, string resultaat, string? samenvatting)
        => auditService != null && auditId.HasValue
            ? auditService.VoltooiAsync(auditId.Value, resultaat, samenvatting)
            : Task.CompletedTask;
}
