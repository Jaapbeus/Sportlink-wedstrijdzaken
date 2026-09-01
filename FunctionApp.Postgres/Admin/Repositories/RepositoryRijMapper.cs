using Npgsql;

namespace FunctionApp.Postgres.Admin;

internal static class RepositoryRijMapper
{
    internal static Dictionary<string, object?> LeesRij(NpgsqlDataReader r)
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++)
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        return row;
    }
}
