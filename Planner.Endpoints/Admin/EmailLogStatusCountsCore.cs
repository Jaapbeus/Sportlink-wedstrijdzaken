using System.Data;
using System.Data.Common;

namespace Planner.Endpoints.Admin;

public static class EmailLogStatusCountsCore
{
    public static string BuildWhere(string clubColumn, string receivedColumn, DateTime? vanaf, DateTime? tot, string? status)
    {
        var where = $" WHERE {clubColumn} = @clubCode";
        if (vanaf.HasValue) where += $" AND {receivedColumn} >= @vanaf";
        if (tot.HasValue) where += $" AND {receivedColumn} < @tot";
        if (!string.IsNullOrWhiteSpace(status)) where += " AND status = @status";
        return where;
    }

    public static async Task<Dictionary<string, int>> ReadAsync(
        DbConnection connection, string sql, string clubCode, DateTime? vanaf, DateTime? tot, string? status)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@clubCode", clubCode);
        if (vanaf.HasValue) AddParameter(command, "@vanaf", vanaf.Value);
        if (tot.HasValue) AddParameter(command, "@tot", tot.Value);
        if (!string.IsNullOrWhiteSpace(status)) AddParameter(command, "@status", status);
        await using var reader = await command.ExecuteReaderAsync();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) counts[reader.IsDBNull(0) ? "" : reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
