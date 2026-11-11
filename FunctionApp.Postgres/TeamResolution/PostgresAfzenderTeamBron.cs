using Npgsql;
using Planner.Shared.TeamResolution;
using FunctionApp.Postgres.Planner;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier van <see cref="IAfzenderTeamBron"/> (#1862): de teamwaarden van een afzender uit
/// <c>avg.teambegeleiding</c>. De vergelijking staat op <c>LOWER(TRIM(emailadres))</c> en gebruikt daarmee de
/// expressie-index uit migratie 049. Alleen de teamkolom komt terug: nooit naam, telefoonnummer of rol, en het
/// adres zit alleen als parameter in de query (nooit in log of trace). Zie <see cref="PostgresClubScope.LegacyFilter"/>
/// voor de clubafbakening, gelijk aan de rest van de begeleiding-opzoekingen.
/// </summary>
internal sealed class PostgresAfzenderTeamBron(Func<string>? connectionString = null) : IAfzenderTeamBron
{
    public async Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode)
    {
        await using var conn = new NpgsqlConnection((connectionString ?? (() => PostgresDatabaseConfig.ConnectionString))());
        await conn.OpenAsync();
        await using var cmd = BouwOpdracht(conn, afzender, clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await AfzenderTeamBronHulp.LeesTeamsAsync(reader);
    }

    /// <summary>De productiequery met parameters; de integratietest legt hiermee ook het EXPLAIN-plan vast.</summary>
    internal static NpgsqlCommand BouwOpdracht(NpgsqlConnection conn, string afzender, string clubCode)
    {
        var cmd = new NpgsqlCommand($@"
            SELECT DISTINCT tb.team
            FROM avg.teambegeleiding tb
            WHERE LOWER(TRIM(tb.emailadres)) = @adres
              AND tb.team IS NOT NULL AND TRIM(tb.team) <> ''
              AND {PostgresClubScope.LegacyFilter("tb")}
        ", conn);
        cmd.Parameters.AddWithValue("adres", afzender);
        PostgresClubScope.AddHisParams(cmd, clubCode);
        return cmd;
    }
}
