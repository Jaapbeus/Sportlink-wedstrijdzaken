using Database.Postgres;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

internal static partial class PostgresSyncPipeline
{
    private static async Task RondAfNaMergeAsync(string connectionString, string clubCode, ILogger log, SyncLogWriter? syncLog)
    {
        await SyncLogReporting.BeginPhaseAsync(syncLog, "planner-view");
        try
        {
            await EnsurePlannerViewAsync(connectionString, log);
            await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(connectionString, clubCode, log);
            await SyncLogReporting.CompletePhaseAsync(syncLog, "planner-view", "succeeded");
        }
        catch
        {
            await SyncLogReporting.CompletePhaseAsync(syncLog, "planner-view", "failed", reasonCode: "operation_failed");
            throw;
        }

        await SyncLogReporting.BeginPhaseAsync(syncLog, "canonicalisatie");
        var failed = !await CanonicaliseerBestEffortAsync(connectionString, clubCode, clubCode, log);
        if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            failed |= !await CanonicaliseerBestEffortAsync(connectionString, "ALLSTARS", "democlub ALLSTARS", log);
        await SyncLogReporting.CompletePhaseAsync(syncLog, "canonicalisatie", failed ? "partial" : "succeeded",
            reasonCode: failed ? "operation_failed" : null);
    }

    private static async Task<bool> ReconcileVerdwenenAsync(
        PostgresMergeOrchestrator orchestrator, string clubCode, bool teamsFailed, bool matchesFailed,
        DateOnly ondergrens, ILogger log)
    {
        var failed = false;
        if (!teamsFailed)
        {
            try
            {
                var deleted = await orchestrator.ReconcileFullScopeAsync(KnownEntities.Teams, clubCode);
                if (deleted > 0)
                    log.LogInformation("RECONCILIATIE - {Aantal} his.teams-rij(en) gemarkeerd als verwijderd voor {ClubCode}", deleted, clubCode);
            }
            catch (Exception ex)
            {
                failed = true;
                log.LogError(ex, "RECONCILIATIE - his.teams mislukt voor {ClubCode}", clubCode);
            }
        }

        if (!matchesFailed)
        {
            try
            {
                var deleted = await orchestrator.ReconcileWindowedAsync(KnownEntities.Matches, clubCode, "kaledatum", ondergrens: ondergrens);
                if (deleted > 0)
                    log.LogInformation("RECONCILIATIE - {Aantal} his.matches-rij(en) gemarkeerd als verwijderd voor {ClubCode}", deleted, clubCode);
            }
            catch (Exception ex)
            {
                failed = true;
                log.LogError(ex, "RECONCILIATIE - his.matches mislukt voor {ClubCode}", clubCode);
            }
        }
        return failed;
    }

    private static async Task EnsurePlannerViewAsync(string connectionString, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(PostgresPlannerViewGenerator.CreateView, connection);
        await command.ExecuteNonQueryAsync();
        log.LogInformation("PLANNERVIEW - {View} aangemaakt of bijgewerkt", PostgresPlannerViewGenerator.ViewName);
    }

    private static async Task<bool> CanonicaliseerBestEffortAsync(
        string connectionString, string clubCode, string omschrijving, ILogger log)
    {
        try
        {
            await TeamResolution.TeamCanonicalisatieService.RefreshAsync(connectionString, clubCode, log);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMS CANONICALISATIE - mislukt voor {Omschrijving}", omschrijving);
            return false;
        }
    }

    private static async Task SaveLastSyncTimestampAsync(string connectionString, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.appsettings SET lastsynctimestamp = NOW() WHERE clubcode = @clubcode", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await command.ExecuteNonQueryAsync();
        log.LogInformation("lastsynctimestamp bijgewerkt voor club {ClubCode}", clubCode);
    }
}
