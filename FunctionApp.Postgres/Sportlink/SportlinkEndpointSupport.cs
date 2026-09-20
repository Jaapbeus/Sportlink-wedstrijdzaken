using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Tier-dun omhulsel om <see cref="SportlinkEndpointSupportCore"/> (Planner.Endpoints, #1271): de
/// Postgres-instellingenlezer, de Postgres-EgressGuard en de Postgres-<see cref="AdminEndpoint"/>
/// als delegate meegeven aan de gedeelde orkestratie. De methodesignaturen blijven ongewijzigd
/// zodat geen enkele aanroeper in deze tier hoeft te wijzigen.
/// <para>
/// Vóór #1271 stond hier de volledige orkestratielogica nog een keer, woordelijk gelijk aan de
/// SQL Server-tegenhanger op de databaseaanroep na — zie de toelichting bij
/// <c>SportlinkEndpointSupportCore</c> voor de meting die dat bevestigde.
/// </para>
/// </summary>
internal static class SportlinkEndpointSupport
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    internal const string RolWedstrijdzaken = SportlinkEndpointSupportCore.RolWedstrijdzaken;

    internal static IActionResult? ControleerToggleEnEgress()
        => SportlinkEndpointSupportCore.ControleerToggleEnEgress(
            PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed);

    internal static bool IsDryRunActief()
        => SportlinkEndpointSupportCore.IsDryRunActief(PostgresAppSettings.GetSetting);

    /// <summary>
    /// Voert een Sportlink-endpoint uit met BEIDE poorten: eerst de functionele rol
    /// <c>Wedstrijdzaken</c>, daarna de gewone admin-controle van
    /// <see cref="AdminEndpoint.ExecuteAsync"/>. Beide tiers gebruiken sinds #1272 dezelfde
    /// volgorde — zie <see cref="SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync"/>.
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
            context, log, taak, PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed);

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
