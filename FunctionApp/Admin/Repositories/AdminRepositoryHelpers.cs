using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminRepositoryHelpers
{
    internal static async Task<SqlConnection> OpenConnectionAsync(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    internal static Dictionary<string, object?> LeesAlleKolommen(SqlDataReader r)
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++)
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        return row;
    }

    internal static Dictionary<string, object?> LeesAlleKolommenMetUtcDatums(SqlDataReader r)
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++)
        {
            var raw = r.IsDBNull(i) ? null : r.GetValue(i);
            row[r.GetName(i)] = raw is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : raw;
        }
        return row;
    }
}
