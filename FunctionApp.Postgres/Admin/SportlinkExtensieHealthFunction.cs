using FunctionApp.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>GET /api/beheer/sportlink-extensie/health</c> (#998, epic #986) — statussectie op de
/// Instellingen-pagina: token-geldigheid, laatste mutatiefout, laatste contract-check. Zonder
/// <c>?live=true</c> doet dit endpoint GEEN Sportlink-aanroep — alles komt uit onze eigen DB (de
/// uur-keep-alive-timer en de dagelijkse contract-check houden dit al bij). Rapporteert nooit een
/// tokenwaarde en nooit Match-data met persoonsgegevens.
/// </summary>
public static class SportlinkExtensieHealthFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    // Ouder dan dit: de uur-keep-alive-timer hoort elk uur te verversen — als het langer geleden is,
    // is de koppeling vermoedelijk niet meer geldig (informatief, geen harde blokkade).
    private static readonly TimeSpan VermoedelijkVerlopenNa = TimeSpan.FromHours(2);

    [Function("SportlinkExtensieHealthGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/sportlink-extensie/health")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkExtensieHealthGet"), "sportlink-extensie-health ophalen",
            async clubCode =>
            {
                var live = req.Query["live"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

                var extensionEnabled = PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") == "1";
                var dryRun = PostgresAppSettings.GetSetting("sportlinkDryRun") != "0";
                var egressAllowed = EgressGuard.ExternalIntegrationsAllowed();

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var rollen = await LeesRolStatusAsync(connection, clubCode);
                var (laatsteFout, laatsteFoutOp) = await LeesLaatsteMutatieFoutAsync(connection, clubCode);
                var laatsteContractCheck = await LeesLaatsteContractCheckAsync(connection, clubCode);

                object? liveResultaat = null;
                if (live)
                {
                    // #998 harde grens: geen enkele "even snel testen"-uitzondering — dit pad raakt
                    // écht club.sportlink.com/idm.sportlink.com aan, en gebeurt uitsluitend op een
                    // expliciete gebruikersklik (nooit automatisch getriggerd door dit endpoint zelf).
                    if (!extensionEnabled)
                        return new ObjectResult(new { error = "Sportlink Web Extension staat uit." }) { StatusCode = 409 };
                    if (!egressAllowed)
                        return new ObjectResult(new { error = "Uitgaande integraties staan hier niet toe." }) { StatusCode = 503 };

                    var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                    if (sportlinkClient == null)
                        return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                    liveResultaat = await VoerLiveControleUitAsync(connection, sportlinkClient, clubCode);
                }

                return new OkObjectResult(new
                {
                    ExtensionEnabled = extensionEnabled,
                    DryRun = dryRun,
                    EgressAllowed = egressAllowed,
                    Rollen = rollen,
                    LaatsteMutatieFout = laatsteFout,
                    LaatsteMutatieFoutOp = laatsteFoutOp,
                    LaatsteContractCheck = laatsteContractCheck,
                    Live = liveResultaat
                });
            });

    private static async Task<List<object>> LeesRolStatusAsync(NpgsqlConnection connection, string clubCode)
    {
        var resultaat = new List<object>();
        await using var cmd = new NpgsqlCommand(
            "SELECT rolnaam, bijgewerktop, refreshtokenvervaltop FROM public.sportlinkservicetokens WHERE clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);

        var gevonden = new Dictionary<string, (DateTime BijgewerktOp, DateTime VervaltOp)>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                gevonden[reader.GetString(0)] = (reader.GetDateTime(1), reader.GetDateTime(2));
        }

        gevonden.TryGetValue(RolNaam, out var info);
        resultaat.Add(new
        {
            RolNaam,
            Gekoppeld = info != default,
            LaatstVerverstOp = info == default ? (DateTime?)null : info.BijgewerktOp,
            RefreshTokenVervaltOp = info == default ? (DateTime?)null : info.VervaltOp,
            VermoedelijkNietMeerGeldig = info != default && (DateTime.UtcNow - info.BijgewerktOp) > VermoedelijkVerlopenNa
        });
        return resultaat;
    }

    private static async Task<(string? Fout, DateTime? Op)> LeesLaatsteMutatieFoutAsync(NpgsqlConnection connection, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT tijdstip, foutmeldingsamenvatting
            FROM public.sportlinkmutationaudit
            WHERE clubcode = @clubcode AND resultaat = 'Failure'
            ORDER BY tijdstip DESC
            LIMIT 1", connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetDateTime(0));
    }

    private static async Task<object?> LeesLaatsteContractCheckAsync(NpgsqlConnection connection, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT uitgevoerdop, isok, httpstatus, afwijkendevelden, foutmeldingsamenvatting
            FROM public.sportlinkcontractcheck
            WHERE clubcode = @clubcode
            ORDER BY uitgevoerdop DESC
            LIMIT 1", connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new
        {
            UitgevoerdOp = reader.GetDateTime(0),
            IsOk = reader.GetBoolean(1),
            HttpStatus = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            AfwijkendeVelden = reader.IsDBNull(3) ? null : reader.GetString(3),
            FoutmeldingSamenvatting = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }

    /// <summary>Eén tokenverversing + één GET op de meest recent gecachte PublicMatchId — alleen
    /// HTTP-status/resultaataard teruggeven, nooit responsdata. Lege cache is geen fout.</summary>
    private static async Task<object> VoerLiveControleUitAsync(
        NpgsqlConnection connection, ISportlinkClubClient sportlinkClient, string clubCode)
    {
        var refreshStatus = await sportlinkClient.VerversTokenAsync(RolNaam);
        var tokenRefreshGelukt = refreshStatus == SportlinkClubCallStatus.Ok;

        await using var cmd = new NpgsqlCommand(
            "SELECT publicmatchid FROM public.sportlinkpublicmatchidcache WHERE clubcode = @clubcode ORDER BY opgehaaldop DESC LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        var publicMatchId = (string?)await cmd.ExecuteScalarAsync();

        if (publicMatchId == null)
            return new { TokenRefreshGelukt = tokenRefreshGelukt, MatchCheckResultaat = "Overgeslagen: geen bekende wedstrijd in de cache.", MatchCheckHttpStatus = (int?)null };

        var matchResult = await sportlinkClient.GetMatchAsync(RolNaam, publicMatchId);
        return new
        {
            TokenRefreshGelukt = tokenRefreshGelukt,
            MatchCheckResultaat = matchResult.Status.ToString(),
            MatchCheckHttpStatus = matchResult.HttpStatusCode
        };
    }
}
