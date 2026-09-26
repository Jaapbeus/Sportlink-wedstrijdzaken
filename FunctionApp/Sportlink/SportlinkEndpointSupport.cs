using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Admin;
using SportlinkFunction.Infrastructure;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// Tier-dun omhulsel om <see cref="SportlinkEndpointSupportCore"/> (Planner.Endpoints, #1271): de
/// SQL Server-instellingenlezer, de SQL Server-EgressGuard en de SQL Server-<see cref="AdminEndpoint"/>
/// als delegate meegeven aan de gedeelde orkestratie. De methodesignaturen blijven ongewijzigd
/// zodat geen enkele aanroeper in deze tier hoeft te wijzigen.
/// <para>
/// Vóór #1271 stond hier de volledige orkestratielogica nog een keer, woordelijk gelijk aan de
/// Postgres-tegenhanger op de databaseaanroep na — zie de toelichting bij
/// <c>SportlinkEndpointSupportCore</c> voor de meting die dat bevestigde.
/// </para>
/// </summary>
internal static class SportlinkEndpointSupport
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    internal const string RolWedstrijdzaken = SportlinkEndpointSupportCore.RolWedstrijdzaken;

    /// <summary>Instellingenlezer van deze tier — één plek, zodat de cache-semantiek
    /// (<c>null</c> = nog niet geladen) overal gelijk is.</summary>
    private static Func<string, string?> LeesInstelling => SystemUtilities.AppSettings.GetSetting;

    internal static IActionResult? ControleerToggleEnEgress()
        => SportlinkEndpointSupportCore.ControleerToggleEnEgress(
            LeesInstelling, EgressGuard.ExternalIntegrationsAllowed);

    internal static bool IsDryRunActief()
        => SportlinkEndpointSupportCore.IsDryRunActief(LeesInstelling);

    /// <summary>
    /// De endpointwrapper voor élk Sportlink Web Extension-endpoint van deze tier (#1266, #1272).
    /// Zet, net als de Postgres-tier sinds #1272, de functionele rolcheck bovenop de bestaande
    /// admin-gate — zie <see cref="SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync"/>.
    /// </summary>
    internal static Task<IActionResult> ExecuteWedstrijdzakenAsync(
        HttpRequest req, ILogger log, string errorContext, Func<string, Task<IActionResult>> work)
        => SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync(
            req, log, errorContext, work,
            EasyAuthHelper.RequireWedstrijdzaken,
            AdminEndpoint.ExecuteAsync);

    internal static IActionResult ClientNietGeconfigureerdFout()
        => SportlinkEndpointSupportCore.ClientNietGeconfigureerdFout();

    internal static (ISportlinkClubClient? Client, IActionResult? Fout) ClientOfFout(FunctionContext context)
        => SportlinkEndpointSupportCore.ClientOfFout(context);

    internal static ISportlinkClubClient? ClientVoorTimer(FunctionContext context, ILogger log, string taak)
        => SportlinkEndpointSupportCore.ClientVoorTimer(
            context, log, taak, LeesInstelling, EgressGuard.ExternalIntegrationsAllowed);

    internal static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
        => SportlinkEndpointSupportCore.VertaalStatusNaarFout(status);

    internal static Task<T?> LeesBodyAsync<T>(HttpRequest req) where T : class
        => SportlinkEndpointSupportCore.LeesBodyAsync<T>(req);

    internal static string BepaalAuditResultaat(SportlinkMutationResult r)
        => SportlinkEndpointSupportCore.BepaalAuditResultaat(r);

    internal static Task<IActionResult> RondMutatieAfAsync<T>(
        SportlinkClubResponse<T> mutationResult,
        ISportlinkMutationAuditService? auditService,
        long? auditId,
        Func<T, SportlinkMutationResult> naarMutatieResultaat,
        Func<T, IActionResult> ok)
        where T : class
        => SportlinkEndpointSupportCore.RondMutatieAfAsync(
            mutationResult,
            VoltooiAuditDelegate(auditService, auditId),
            naarMutatieResultaat,
            ok);

    private static Func<string, string?, Task>? VoltooiAuditDelegate(
        ISportlinkMutationAuditService? auditService, long? auditId)
        => auditService != null && auditId.HasValue
            ? (resultaat, samenvatting) => auditService.VoltooiAsync(auditId.Value, resultaat, samenvatting)
            : null;
}
