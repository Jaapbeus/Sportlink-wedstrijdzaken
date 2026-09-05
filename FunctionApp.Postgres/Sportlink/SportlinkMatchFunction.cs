using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Npgsql;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// <c>GET /api/sportlink/match/{wedstrijdcode}</c> (#991, epic #986) — read-only paneel-endpoint
/// voor Dagplanning. Eerste echte gebruik van <c>RequireWedstrijdzaken</c> (#988 Besluit 1: die
/// granulaire rol-gating komt pas bij het eerste echte lees-/mutatie-endpoint).
/// </summary>
public static class SportlinkMatchFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    // Kale HttpClient's, geen Polly — zelfde precedent als Sync/PostgresSyncPipeline.cs.
    private static readonly HttpClient TokenHttp = new();
    private static readonly HttpClient ClubHttp = new() { BaseAddress = new Uri(SportlinkClubClient.BaseUrl) };

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

                var log = context.GetLogger("SportlinkMatchGet");

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var wedstrijd = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(connection, wedstrijdcodeValue, clubCode);
                if (wedstrijd == null)
                    return new NotFoundObjectResult(new { error = $"Wedstrijd met wedstrijdcode {wedstrijdcodeValue} niet gevonden." });

                var publicMatchId = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(connection, wedstrijdcodeValue, clubCode);

                string accessToken;
                try
                {
                    accessToken = await SportlinkClubTokenProvider.GetAccessTokenAsync(
                        PostgresDatabaseConfig.ConnectionString, RolNaam, clubCode, TokenHttp, log);
                }
                catch (SportlinkNietGekoppeldException ex)
                {
                    return new ObjectResult(new { error = ex.Message }) { StatusCode = 409 };
                }
                catch (SportlinkTokenVerlopenException ex)
                {
                    return new ObjectResult(new { error = ex.Message }) { StatusCode = 409 };
                }

                if (publicMatchId == null)
                {
                    publicMatchId = await SportlinkClubClient.ResolvePublicMatchIdAsync(
                        ClubHttp, wedstrijd.Wedstrijdnummer, wedstrijd.Datum, accessToken, log);
                    if (publicMatchId == null)
                        return new NotFoundObjectResult(new { error = "Wedstrijd nog niet bekend bij Sportlink voor deze datum." });

                    await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(connection, wedstrijdcodeValue, clubCode, publicMatchId);
                }

                var matchInfo = await SportlinkClubClient.GetMatchAsync(ClubHttp, publicMatchId, accessToken, log);
                if (matchInfo == null)
                    return new NotFoundObjectResult(new { error = "Sportlink kent dit PublicMatchId niet (meer)." });

                return new OkObjectResult(matchInfo);
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);
}
