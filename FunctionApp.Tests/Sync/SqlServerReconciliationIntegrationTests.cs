using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Reconciliatie op de SQL Server-tier tegen een echte database (#1558). Bewijst op echte T-SQL wat
/// de Postgres-tier met <c>PostgresReconciliationIntegrationTests</c> bewijst: een door Sportlink
/// geschrapte toekomstige wedstrijd wordt zacht verwijderd, een gespeelde/vandaag-wedstrijd en een
/// andere club blijven staan, en een terugkerende wedstrijd wordt door <c>sp_MergeStgToHis</c> hersteld.
///
/// Opzet (wegwerpcontainer, zelfde als <see cref="SportlinkFixtureSyncIntegrationTests"/>): voer
/// <c>Database/Script.PostDeployment1.sql</c> uit op een lege database en zet BEIDE variabelen op
/// dezelfde verbinding — <c>SQLSERVER_TEST_CONNECTION_STRING</c> (schakelt de test in) en
/// <c>SqlConnectionString</c> (leest <see cref="SystemUtilities.DatabaseConfig"/> bij het opstarten).
/// </summary>
public class SqlServerReconciliationIntegrationTests
{
    private const string Club = "recon1558";
    private const string AndereClub = "recon1558x";
    private static readonly DateOnly Vandaag = new(2026, 10, 7);
    private static readonly DateOnly Ondergrens = new(2026, 10, 8);

    private static string Cs => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task Reconcile_MarkeertVerdwenenToekomstigeWedstrijd_EnHerstelWanneerHijTerugkomt()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await CreateStagingTable.ExecuteAsync("teams");
        await CreateStagingTable.ExecuteAsync("matches");
        await Exec(conn, "IF OBJECT_ID('his.matches') IS NOT NULL DELETE FROM his.matches WHERE ClubCode IN (@c, @x); " +
                         "IF OBJECT_ID('his.teams') IS NOT NULL DELETE FROM his.teams WHERE ClubCode IN (@c, @x);");

        // Eerste sync: drie toekomstige wedstrijden, één van vandaag, één van een andere club.
        await VulMatches(conn, (Club, 1, "2026-10-10"), (Club, 2, "2026-10-11"), (Club, 3, "2026-10-12"),
                               (Club, 4, "2026-10-07"), (AndereClub, 9, "2026-10-11"));
        await VulTeams(conn, (Club, 11), (Club, 12));
        var log = NullLogger.Instance;
        await new MergeStgToHis("stg", "matches", "his", "matches").ExecuteAsync(log);
        await new MergeStgToHis("stg", "teams", "his", "teams").ExecuteAsync(log);

        // Tweede sync: wedstrijd 2 en wedstrijd 4 ontbreken, team 12 ontbreekt.
        await Exec(conn, "DELETE FROM stg.matches; DELETE FROM stg.teams;");
        await VulMatches(conn, (Club, 1, "2026-10-10"), (Club, 3, "2026-10-12"));
        await VulTeams(conn, (Club, 11));
        await new MergeStgToHis("stg", "matches", "his", "matches").ExecuteAsync(log);

        (await SqlServerReconciliation.ReconcileMatchesAsync(Club, Ondergrens)).Should().Be(1);
        (await SqlServerReconciliation.ReconcileTeamsAsync(Club)).Should().Be(1);

        (await Verwijderd(conn, "matches", 2)).Should().BeTrue("een verdwenen toekomstige wedstrijd wordt zacht verwijderd");
        (await Verwijderd(conn, "matches", 1)).Should().BeFalse();
        (await Verwijderd(conn, "matches", 3)).Should().BeFalse();
        (await Verwijderd(conn, "matches", 4)).Should().BeFalse("een wedstrijd van vandaag valt onder de ondergrens");
        (await Verwijderd(conn, "matches", 9)).Should().BeFalse("een andere club wordt nooit geraakt");
        (await Verwijderd(conn, "teams", 12)).Should().BeTrue();

        // Idempotent: een tweede run verandert niets meer.
        (await SqlServerReconciliation.ReconcileMatchesAsync(Club, Ondergrens)).Should().Be(0);

        // Wedstrijd 2 komt terug in Sportlink: merge herstelt hem, ook zonder inhoudelijke wijziging.
        await VulMatches(conn, (Club, 2, "2026-10-11"));
        await new MergeStgToHis("stg", "matches", "his", "matches").ExecuteAsync(log);
        (await Verwijderd(conn, "matches", 2)).Should().BeFalse("sp_MergeStgToHis herstelt een terugkerende rij");
    }

    [SqlServerFact]
    public async Task ReconcileMatches_ZonderStgRijenVoorDeClub_DoetNiets()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await CreateStagingTable.ExecuteAsync("matches");
        await Exec(conn, "IF OBJECT_ID('his.matches') IS NOT NULL DELETE FROM his.matches WHERE ClubCode IN (@c, @x);");
        await VulMatches(conn, (Club, 1, "2026-10-10"));
        await new MergeStgToHis("stg", "matches", "his", "matches").ExecuteAsync(NullLogger.Instance);
        await Exec(conn, "DELETE FROM stg.matches;"); // mislukte/lege fetch

        (await SqlServerReconciliation.ReconcileMatchesAsync(Club, Ondergrens)).Should().Be(0);
        (await Verwijderd(conn, "matches", 1)).Should().BeFalse();
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@c", Club);
        cmd.Parameters.AddWithValue("@x", AndereClub);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task VulMatches(SqlConnection conn, params (string Club, long Code, string Datum)[] rijen)
    {
        foreach (var r in rijen)
        {
            await using var cmd = new SqlCommand(
                "INSERT INTO stg.matches (wedstrijdcode, kaledatum, ClubCode) VALUES (@code, @datum, @club)", conn);
            cmd.Parameters.AddWithValue("@code", r.Code);
            cmd.Parameters.AddWithValue("@datum", r.Datum + " 00:00:00.00");
            cmd.Parameters.AddWithValue("@club", r.Club);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task VulTeams(SqlConnection conn, params (string Club, long Code)[] rijen)
    {
        foreach (var r in rijen)
        {
            await using var cmd = new SqlCommand(
                "INSERT INTO stg.teams (teamcode, lokaleteamcode, poulecode, ClubCode) VALUES (@code, 1, 1, @club)", conn);
            cmd.Parameters.AddWithValue("@code", r.Code);
            cmd.Parameters.AddWithValue("@club", r.Club);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> Verwijderd(SqlConnection conn, string tabel, long code)
    {
        var kolom = tabel == "matches" ? "wedstrijdcode" : "teamcode";
        await using var cmd = new SqlCommand(
            $"SELECT CASE WHEN [mta_deleted] IS NULL THEN 0 ELSE 1 END FROM his.{tabel} WHERE {kolom} = @code", conn);
        cmd.Parameters.AddWithValue("@code", code);
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }
}
