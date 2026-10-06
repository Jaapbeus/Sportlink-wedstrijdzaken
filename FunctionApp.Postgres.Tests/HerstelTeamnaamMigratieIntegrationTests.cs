using AwesomeAssertions;
using Database.Postgres;
using Database.Postgres.Tests;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1561: migratie 037 herstelt de lege teamnaam bij gespeelde wedstrijden (rijen die het oude
/// sync-gedrag overschreef en die migratie 036 niet herstelde). De afleiding is bewust voorzichtig:
/// alleen als minstens één van beide teams de relatiecode van de eigen club draagt, en alleen voor een
/// club waarvan die code uit al gevulde rijen blijkt. Hier bewezen met het echte migratiebestand tegen
/// een echte database, niet met een nagebouwde SQL-variant.
/// </summary>
public class HerstelTeamnaamMigratieIntegrationTests
{
    private const string Club = "testclub-1561";
    private const string AndereClub = "testclub-1561-b";
    private const string EigenCode = "TESTCODE-A";
    private const string Vreemd = "TESTCODE-X";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie PostgresSyncFixtureIntegrationTests.");

    [PostgresFact]
    public async Task Migratie037_LeidtDeEigenTeamnaamAfUitDeRelatiecode_EnLaatTwijfelrijenStaan()
    {
        await VoorbereidAsync();

        // Voorbeeldrij die de eigen relatiecode vastlegt: thuis- én uitwedstrijd.
        await VoegToeAsync(1, Club, "Eigen JO10-1", "Eigen JO10-1", EigenCode, "Tegen A", Vreemd);
        await VoegToeAsync(2, Club, "Eigen JO10-2", "Tegen B", Vreemd, "Eigen JO10-2", EigenCode);

        await VoegToeAsync(10, Club, "", "Eigen JO11-1", EigenCode, "Tegen C", Vreemd);   // thuis eigen
        await VoegToeAsync(11, Club, null, "Tegen D", Vreemd, "Eigen JO11-2", EigenCode); // uit eigen
        await VoegToeAsync(12, Club, "", "Eigen JO12-1", EigenCode, "Eigen JO12-2", EigenCode); // onderling
        await VoegToeAsync(13, Club, "", "Tegen E", Vreemd, "Tegen F", Vreemd);           // geen van beide
        await VoegToeAsync(14, Club, "", "Tegen G", null, "Eigen JO13-1", null);          // geen codes

        // Andere club zonder enige gevulde rij: er is niets om de code uit af te leiden.
        await VoegToeAsync(20, AndereClub, "", "Andere JO10-1", EigenCode, "Tegen H", Vreemd);

        await PasMigratieToeAsync();

        (await TeamnaamAsync(10)).Should().Be("Eigen JO11-1", "het thuisteam draagt de eigen relatiecode");
        (await TeamnaamAsync(11)).Should().Be("Eigen JO11-2", "het uitteam draagt de eigen relatiecode");
        (await TeamnaamAsync(12)).Should().Be("Eigen JO12-1", "bij een onderlinge wedstrijd is het thuisteam de teamnaam, zoals /programma het doet");
        (await TeamnaamAsync(13)).Should().BeNullOrEmpty("geen van beide teams is een eigen team");
        (await TeamnaamAsync(14)).Should().BeNullOrEmpty("zonder relatiecodes valt niets af te leiden");
        (await TeamnaamAsync(20)).Should().BeNullOrEmpty("een club zonder gevulde rijen blijft ongemoeid");
        (await TeamnaamAsync(1)).Should().Be("Eigen JO10-1", "een gevulde teamnaam wordt nooit overschreven");
        (await TeamnaamAsync(2)).Should().Be("Eigen JO10-2");
    }

    [PostgresFact]
    public async Task Migratie037_IsIdempotent()
    {
        await VoorbereidAsync();
        await VoegToeAsync(1, Club, "Eigen JO10-1", "Eigen JO10-1", EigenCode, "Tegen A", Vreemd);
        await VoegToeAsync(10, Club, "", "Eigen JO11-1", EigenCode, "Tegen C", Vreemd);

        await PasMigratieToeAsync();
        await PasMigratieToeAsync();

        (await TeamnaamAsync(10)).Should().Be("Eigen JO11-1");
    }

    private static async Task VoorbereidAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Matches);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var opruimen = new NpgsqlCommand("DELETE FROM his.matches WHERE clubcode IN (@a, @b)", conn);
        opruimen.Parameters.AddWithValue("a", Club);
        opruimen.Parameters.AddWithValue("b", AndereClub);
        await opruimen.ExecuteNonQueryAsync();
    }

    private static async Task VoegToeAsync(
        long code, string club, string? teamnaam, string thuis, string? thuisCode, string uit, string? uitCode)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO his.matches (wedstrijdcode, clubcode, teamnaam, thuisteam, thuisteamclubrelatiecode,
                                     uitteam, uitteamclubrelatiecode, wedstrijddatum, mta_inserted, mta_modified)
            VALUES (@code, @club, @teamnaam, @thuis, @thuiscode, @uit, @uitcode, '2026-09-26T10:00:00+0200', now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("club", club);
        cmd.Parameters.AddWithValue("teamnaam", (object?)teamnaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("thuis", thuis);
        cmd.Parameters.AddWithValue("thuiscode", (object?)thuisCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("uit", uit);
        cmd.Parameters.AddWithValue("uitcode", (object?)uitCode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> TeamnaamAsync(long code)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT teamnaam FROM his.matches WHERE wedstrijdcode = @code AND clubcode IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("a", Club);
        cmd.Parameters.AddWithValue("b", AndereClub);
        var waarde = await cmd.ExecuteScalarAsync();
        return waarde is DBNull or null ? null : (string)waarde;
    }

    private static async Task PasMigratieToeAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(ZoekMigratiemap(), "037_herstel_lege_teamnaam_gespeelde_wedstrijden.sql"));
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ZoekMigratiemap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var kandidaat = Path.Combine(dir.FullName, "Database.Postgres", "migrations");
            if (Directory.Exists(kandidaat)) return kandidaat;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Database.Postgres/migrations niet gevonden vanaf " + AppContext.BaseDirectory);
    }
}
