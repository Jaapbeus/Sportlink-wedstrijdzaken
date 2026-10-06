using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Npgsql;
using Planner.Endpoints.Leren;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminTeamAliassenFunction.cs</c> (#887).
/// Vertaling: <c>SqlException.Number == 208</c> → <c>PostgresErrorCodes.UndefinedTable</c>.
/// </summary>
public static class AdminTeamAliassenFunction
{
    [Function("AdminTeamAliassenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teamaliassen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenGet"), "teamaliassen ophalen",
            async clubCode =>
            {
                var (statusFilter, limit, fout) = TeamAliasEndpointCore.LeesLijstFilter(req.Query);
                if (fout is not null) return fout;

                var cs = PostgresDatabaseConfig.ConnectionString;
                try
                {
                    var (count, lim, items) = await AdminTeamAliassenRepository.GetAsync(
                        clubCode, statusFilter, limit, cs);
                    var (pending, validated, rejected) = await AdminTeamAliassenRepository.GetStatsAsync(clubCode, cs);
                    return new OkObjectResult(new { count, limit = lim, pending, validated, rejected, items });
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    return new OkObjectResult(new
                    {
                        count = 0, limit, pending = 0, validated = 0, rejected = 0,
                        items = new List<object>()
                    });
                }
            });

    private static PostgresTeamAliasStore AliasStore() => new(PostgresDatabaseConfig.ConnectionString);
    private static PostgresOnbekendeTeamTekstStore Wachtrij() => new(PostgresDatabaseConfig.ConnectionString);

    /// <summary>Aanmaken door een beheerder (#1568 deel C): bron <c>CoordinatorCorrectie</c>, direct <c>validated</c>.</summary>
    [Function("AdminTeamAliassenPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teamaliassen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenPost"), "teamalias aanmaken",
            async clubCode => await TeamAliasEndpointCore.AanmakenAsync(
                clubCode, await TeamAliasEndpointCore.LeesBodyAsync(req), EasyAuthHelper.GetLerenAanroeper(req),
                AliasStore(), Wachtrij(), context.GetLogger("AdminTeamAliassenPost")));

    [Function("AdminTeamAliassenValideer")]
    public static Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/teamaliassen/{id:int}/valideer")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenValideer"), "teamalias valideren",
            async clubCode => await TeamAliasEndpointCore.ValideerAsync(
                id, await TeamAliasEndpointCore.LeesBodyAsync(req), EasyAuthHelper.GetLerenAanroeper(req),
                (aliasId, status, wie) => AdminTeamAliassenRepository.ZetStatusAsync(
                    aliasId, status, clubCode, wie, PostgresDatabaseConfig.ConnectionString)));

    [Function("AdminTeamAliassenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/teamaliassen/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenDelete"), "teamalias verwijderen",
            clubCode => TeamAliasEndpointCore.VerwijderAsync(
                id, context.GetLogger("AdminTeamAliassenDelete"),
                aliasId => AdminTeamAliassenRepository.DeleteAsync(aliasId, clubCode, PostgresDatabaseConfig.ConnectionString)));
}
