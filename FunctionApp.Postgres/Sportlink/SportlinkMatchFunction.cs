using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// <c>GET /api/sportlink/match/{wedstrijdcode}</c> (#991, epic #986) — read-only paneel-endpoint
/// voor Dagplanning. Eerste echte gebruik van <c>RequireWedstrijdzaken</c> (#988 Besluit 1: die
/// granulaire rol-gating komt pas bij het eerste echte lees-/mutatie-endpoint).
/// <para>
/// Verbindt drie stukken die elk in een aparte issue/PR gebouwd zijn: de gedeelde
/// <see cref="ISportlinkClubClient"/> (#991/#998, <c>Planner.Shared</c>), de Postgres-tier
/// <see cref="PostgresSportlinkClubTokenStore"/> (#991) en de PublicMatchId-reverse-lookup-cache
/// (#991/#1016, <see cref="SportlinkPublicMatchIdRepository"/>).
/// </para>
/// </summary>
public static class SportlinkMatchFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    [Function("SportlinkMatchGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/match/{wedstrijdcode}")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkMatchGet"), "sportlink-match ophalen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
                    return new ObjectResult(new { error = "Sportlink Web Extension staat uit." }) { StatusCode = 409 };

                if (!EgressGuard.ExternalIntegrationsAllowed())
                    return new ObjectResult(new { error = "Uitgaande integraties staan hier niet toe." }) { StatusCode = 503 };

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                if (sportlinkClient == null)
                    return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var wedstrijd = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(connection, wedstrijdcodeValue, clubCode);
                if (wedstrijd == null)
                    return new NotFoundObjectResult(new { error = $"Wedstrijd met wedstrijdcode {wedstrijdcodeValue} niet gevonden." });

                var publicMatchId = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(connection, wedstrijdcodeValue, clubCode);

                if (publicMatchId == null)
                {
                    var lookup = await sportlinkClient.ResolvePublicMatchIdAsync(RolNaam, wedstrijd.Wedstrijdnummer, wedstrijd.Datum);
                    var lookupFout = VertaalStatusNaarFout(lookup.Status);
                    if (lookupFout != null) return lookupFout;
                    if (lookup.Data == null)
                        return new NotFoundObjectResult(new { error = "Wedstrijd nog niet bekend bij Sportlink voor deze datum." });

                    publicMatchId = lookup.Data.PublicMatchId;
                    await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(connection, wedstrijdcodeValue, clubCode, publicMatchId);
                }

                var matchResult = await sportlinkClient.GetMatchAsync(RolNaam, publicMatchId);
                var matchFout = VertaalStatusNaarFout(matchResult.Status);
                if (matchFout != null) return matchFout;
                if (matchResult.Data == null)
                    return new NotFoundObjectResult(new { error = "Sportlink kent dit PublicMatchId niet (meer)." });

                return new OkObjectResult(matchResult.Data);
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een HTTP-foutrespons — nooit de
    /// onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel). Retourneert <c>null</c>
    /// bij <c>Ok</c> (aanroeper gaat verder met de data).</summary>
    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status) => status switch
    {
        SportlinkClubCallStatus.Ok => null,
        SportlinkClubCallStatus.RolNietGekoppeld => new ObjectResult(new
        {
            error = $"Geen Sportlink-koppeling gevonden voor rol '{RolNaam}' — registreer eerst een refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        SportlinkClubCallStatus.HerkoppelingVereist => new ObjectResult(new
        {
            error = $"De Sportlink-koppeling voor rol '{RolNaam}' is verlopen — registreer een nieuw refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        _ => new ObjectResult(new { error = "Sportlink is momenteel niet bereikbaar." }) { StatusCode = 502 },
    };
}
