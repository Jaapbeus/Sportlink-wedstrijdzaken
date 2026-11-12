using Microsoft.Data.SqlClient;
using Planner.Shared;
using System.Data;
using System.Globalization;

namespace SportlinkFunction.Planner;

internal static partial class AutoPlanService
{
    private static async Task<AutoPlanToepassenResponse> ApplyExactAsync(AutoPlanToepassenRequest request)
    {
        if (!DateOnly.TryParseExact(request.Datum, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var datum) || request.BufferMinuten is < 0 or > 480)
            throw new AutoPlanApplyConflictException();

        using var conn = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await conn.OpenAsync();
        using var transaction = conn.BeginTransaction(IsolationLevel.Serializable);
        return await MapTransactionConflictAsync(async () =>
        {
            var sources = await AllstarsTestDataRepository.GetAllMatchesForDatumAsync(conn, transaction, datum, AllstarsClubCode);
            var velden = await AllstarsTestDataRepository.GetAllstarsVeldenAsync(conn, transaction);
            var speeltijden = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(conn, transaction, AllstarsClubCode);
            if (speeltijden.Count == 0)
                speeltijden = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(conn, transaction);
            var buffers = await TeamRulesRepository.GetAllTeamBuffersAsync(conn, transaction, AllstarsClubCode);
            var choices = request.Toewijzingen!.Select(c => new AutoPlanApplyChoice(
                c.WedstrijdCode, c.HuidigeVeld, c.HuidigeTijd, c.VeldNummer, c.VeldSubpositie, c.Tijd)).ToList();
            var sourceRows = sources.Select(s => new AutoPlanApplySource(s.WedstrijdCode, s.Veld,
                s.AanvangsTijd, s.TeamNaam, s.LeeftijdsCategorie, s.SportlinkSpeelduur)).ToList();
            var writes = AutoPlanExactApply.Validate(choices, sourceRows, velden, speeltijden,
                buffers, request.BufferMinuten ?? PlannerShared.StandardBufferMinutes);
            var updated = await AutoPlanExactApply.PersistAsync(writes,
                write => AllstarsTestDataRepository.UpdateAllstarsMatchAsync(conn, transaction,
                    write.WedstrijdCode, write.VerwachtVeld, write.VerwachteTijd, write.Veld, write.Tijd),
                () => transaction.CommitAsync());
            return new AutoPlanToepassenResponse { Bijgewerkt = updated };
        });
    }

    /// <summary>
    /// Vertaalt een SQL Server-deadlock (1205) of snapshotconflict (3960) naar het veilige conflict;
    /// andere fouten gaan ongewijzigd door. Losse naad (#1888) omdat T-SQL fout 1205 niet zelf kan
    /// opwerpen: zo is de vertaling deterministisch te testen zonder echte deadlock.
    /// </summary>
    internal static async Task<T> MapTransactionConflictAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (SqlException ex) when (AutoPlanApplyConflictException.IsTransactionConflict(null, ex.Number))
        {
            throw new AutoPlanApplyConflictException();
        }
    }
}
