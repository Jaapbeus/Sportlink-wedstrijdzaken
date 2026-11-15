using AwesomeAssertions;
using Database.Postgres.Tests;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1962 (review R1-F2): migratie 054 vult 35+ en VR30+ aan per club die al speeltijden heeft, hoofdletterongevoelig.
/// Een bestaande <c>vr30+</c> met eigen instellingen blijft de enige rij voor die categorie, ook na een herhaalde
/// migratie; een club zonder speeltijden krijgt niets. Draait tegen de gemigreerde testdatabase, binnen een
/// transactie die na afloop wordt teruggedraaid.
/// </summary>
public class Migratie054SpeeltijdenIntegrationTests
{
    private const string ClubMetEigenRij = "mig054a";
    private const string ClubZonderVeteranen = "mig054b";
    private const string ClubZonderSpeeltijden = "mig054c";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private static string MigratieSql()
    {
        var map = new DirectoryInfo(AppContext.BaseDirectory);
        while (map != null && !Directory.Exists(Path.Combine(map.FullName, "Database.Postgres", "migrations")))
            map = map.Parent;
        if (map == null) throw new InvalidOperationException("Database.Postgres/migrations niet gevonden.");
        return File.ReadAllText(Path.Combine(map.FullName, "Database.Postgres", "migrations", "054_speeltijden_helften.sql"));
    }

    [PostgresFact]
    public async Task HerhaaldeMigratie_RespecteertBestaandeCategorieMetAndereHoofdletters_EnClubisolatie()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await VoerUitAsync(conn, tx, """
            INSERT INTO public.appsettings (clubcode, clubname, syncenabled)
            VALUES ('mig054a', 'Migratie A', false), ('mig054b', 'Migratie B', false), ('mig054c', 'Migratie C', false)
            ON CONFLICT DO NOTHING;
            INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, wedstrijdhelften, clubcode)
            VALUES ('vr30+', 1.00, 25, 25, 0, 1, 'mig054a'),
                   ('JO13', 1.00, 75, 30, 15, 2, 'mig054a'),
                   ('JO13', 1.00, 75, 30, 15, 2, 'mig054b');
            """);

        var sql = MigratieSql();
        await VoerUitAsync(conn, tx, sql);
        await VoerUitAsync(conn, tx, sql);

        // Eén logische VR30+-rij per club, en de eigen instellingen van club A blijven staan.
        (await TelAsync(conn, tx, ClubMetEigenRij, "vr30+")).Should().Be(1);
        (await LeesTotaalAsync(conn, tx, ClubMetEigenRij, "vr30+")).Should().Be(25);
        (await TelAsync(conn, tx, ClubMetEigenRij, "35+")).Should().Be(1);
        (await TelAsync(conn, tx, ClubZonderVeteranen, "vr30+")).Should().Be(1);
        (await LeesTotaalAsync(conn, tx, ClubZonderVeteranen, "vr30+")).Should().Be(20);
        (await TelAsync(conn, tx, ClubZonderVeteranen, "35+")).Should().Be(1);
        // Een club zonder speeltijden krijgt niets (zijn speeltijden komen via de beheerder of de democlub-kopie).
        (await TelAsync(conn, tx, ClubZonderSpeeltijden, "35+")).Should().Be(0);
        (await TelAsync(conn, tx, ClubZonderSpeeltijden, "vr30+")).Should().Be(0);

        await tx.RollbackAsync();
    }

    private static async Task VoerUitAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> TelAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string club, string leeftijd)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.speeltijden WHERE clubcode = @c AND LOWER(leeftijd) = LOWER(@l)", conn, tx);
        cmd.Parameters.AddWithValue("c", club);
        cmd.Parameters.AddWithValue("l", leeftijd);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> LeesTotaalAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string club, string leeftijd)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT wedstrijdtotaal FROM public.speeltijden WHERE clubcode = @c AND LOWER(leeftijd) = LOWER(@l)", conn, tx);
        cmd.Parameters.AddWithValue("c", club);
        cmd.Parameters.AddWithValue("l", leeftijd);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
