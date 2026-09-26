using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Infrastructure;
using SportlinkFunction.Sportlink;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Admin/SportlinkExtensieHealthFunction.cs</c>
/// (#1266, #998, epic #986): <c>GET /api/beheer/sportlink-extensie/health</c> — de statussectie op
/// de Instellingen-pagina met token-geldigheid, laatste mutatiefout en laatste contract-check.
/// Zonder <c>?live=true</c> doet dit endpoint GEEN Sportlink-aanroep; alles komt uit onze eigen
/// database. Rapporteert nooit een tokenwaarde en nooit Match-data met persoonsgegevens.
/// <para>
/// <b>Eén tierverschil, bewust.</b> De Postgres-tier bewaart refresh-tokens in een eigen DB-tabel
/// en kent daardoor het moment van de laatste verversing. Deze tier bewaart ze in een Function
/// App-instelling via de Azure Management API (<see cref="SportlinkClubAppSettingsTokenStore"/>,
/// #1020, herbevestigd bij #1266) — die opslag heeft geen tijdstempels. <c>LaatstVerverstOp</c> en
/// <c>RefreshTokenVervaltOp</c> zijn hier dus <c>null</c>: "niet bekend", niet "verlopen". De vorm
/// van het antwoord is verder identiek, want beide tiers bouwen hem met
/// <see cref="SportlinkEndpointCore.BouwRolStatus"/>.
/// </para>
/// </summary>
public static class SportlinkExtensieHealthFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    [Function("SportlinkExtensieHealthGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/sportlink-extensie/health")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkExtensieHealthGet"), "sportlink-extensie-health ophalen",
            async clubCode =>
            {
                var live = req.Query["live"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

                var extensionEnabled = SystemUtilities.AppSettings.GetSetting(
                    SportlinkEndpointCore.InstellingExtensieIngeschakeld) == "1";
                // #1266: dezelfde fail-safe polariteit als Program.cs en de Postgres-tier — één plek.
                var dryRun = SportlinkEndpointSupport.IsDryRunActief();
                var egressAllowed = EgressGuard.ExternalIntegrationsAllowed();

                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var rollen = LeesRolStatus(context);
                var (laatsteFout, laatsteFoutOp) = await LeesLaatsteMutatieFoutAsync(connection, clubCode);
                var laatsteContractCheck = await LeesLaatsteContractCheckAsync(connection, clubCode);

                SportlinkLiveControle? liveResultaat = null;
                if (live)
                {
                    // #998 harde grens: geen enkele "even snel testen"-uitzondering — dit pad raakt
                    // écht de Sportlink Club-omgeving aan, en gebeurt uitsluitend op een expliciete
                    // gebruikersklik (nooit automatisch getriggerd door dit endpoint zelf).
                    var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                    if (toggleFout != null) return toggleFout;
                    var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                    if (clientFout != null) return clientFout;

                    liveResultaat = await VoerLiveControleUitAsync(connection, sportlinkClient!, clubCode);
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

    /// <summary>
    /// "Is deze rol gekoppeld?" is op deze tier één vraag aan de tokenopslag: bestaat de Function
    /// App-instelling met het refresh-token. Bewust via <see cref="ISportlinkClubTokenStore"/> en
    /// niet door hier zelf de omgevingsvariabelenaam samen te stellen — die naamgeving hoort op één
    /// plek te staan. De tokenwaarde zelf wordt alleen op aanwezigheid getoetst en nooit
    /// teruggegeven of gelogd.
    /// </summary>
    private static List<SportlinkRolStatus> LeesRolStatus(FunctionContext context)
    {
        // Program.cs registreert de tokenopslag alleen als de EgressGuard uitgaand verkeer toestaat.
        // Ontbreekt hij, dan is er hier niets te melden behalve "niet gekoppeld" — de losse
        // EgressAllowed-vlag in het antwoord vertelt de beheerder waaróm.
        var tokenStore = context.InstanceServices.GetService<ISportlinkClubTokenStore>();
        var gekoppeld = !string.IsNullOrWhiteSpace(tokenStore?.LeesRefreshToken(RolNaam));

        return new List<SportlinkRolStatus>
        {
            // LaatstVerverstOp/RefreshTokenVervaltOp: zie de klassedocumentatie — deze tokenopslag
            // houdt geen momenten bij, dus null ("onbekend"), en daarmee ook nooit een vals
            // "vermoedelijk verlopen".
            SportlinkEndpointCore.BouwRolStatus(
                RolNaam,
                gekoppeld,
                laatstVerverstOpUtc: null,
                refreshTokenVervaltOpUtc: null,
                nuUtc: DateTime.UtcNow)
        };
    }

    private static async Task<(string? Fout, DateTime? Op)> LeesLaatsteMutatieFoutAsync(SqlConnection connection, string clubCode)
    {
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [Tijdstip], [FoutmeldingSamenvatting]
            FROM [dbo].[SportlinkMutationAudit]
            WHERE [ClubCode] = @ClubCode AND [Resultaat] = 'Failure'
            ORDER BY [Tijdstip] DESC", connection);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        // DATETIME2 komt als Kind=Unspecified terug; de kolom wordt met GETUTCDATE() gevuld, dus
        // expliciet als UTC markeren (#246) — anders telt de Blazor-GUI er nog eens een offset bij op.
        return (reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
    }

    private static async Task<object?> LeesLaatsteContractCheckAsync(SqlConnection connection, string clubCode)
    {
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [UitgevoerdOp], [IsOk], [HttpStatus], [AfwijkendeVelden], [FoutmeldingSamenvatting]
            FROM [dbo].[SportlinkContractCheck]
            WHERE [ClubCode] = @ClubCode
            ORDER BY [UitgevoerdOp] DESC", connection);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new
        {
            UitgevoerdOp = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
            IsOk = reader.GetBoolean(1),
            HttpStatus = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            AfwijkendeVelden = reader.IsDBNull(3) ? null : reader.GetString(3),
            FoutmeldingSamenvatting = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }

    /// <summary>Eén tokenverversing + één GET op de meest recent gecachte PublicMatchId — alleen
    /// HTTP-status/resultaataard teruggeven, nooit responsdata. Lege cache is geen fout.</summary>
    private static async Task<SportlinkLiveControle> VoerLiveControleUitAsync(
        SqlConnection connection, ISportlinkClubClient sportlinkClient, string clubCode)
    {
        var refreshStatus = await sportlinkClient.VerversTokenAsync(RolNaam);

        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [PublicMatchId] FROM [dbo].[SportlinkPublicMatchIdCache]
            WHERE [ClubCode] = @ClubCode
            ORDER BY [OpgehaaldOp] DESC", connection);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        var publicMatchId = (await cmd.ExecuteScalarAsync()) as string;

        if (publicMatchId == null)
            return SportlinkEndpointCore.BouwLiveControle(refreshStatus);

        var matchResult = await sportlinkClient.GetMatchAsync(RolNaam, publicMatchId);
        return SportlinkEndpointCore.BouwLiveControle(refreshStatus, matchResult.Status, matchResult.HttpStatusCode);
    }
}
