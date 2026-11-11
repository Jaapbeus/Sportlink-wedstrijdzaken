using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared;
using Planner.Shared.Planning;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1814: echte SQL Server-view en productielezer, met de repositoryschema's op een
/// wegwerpdatabase. Elke test draait alle DDL en synthetische data terug; de assembly
/// schakelt testparallellisme uit. Geen gedeelde ontwikkel-/productiedatabase gebruiken.
/// </summary>
public class SqlServerPlannerViewIntegrationTests
{
    internal const string Club = "ZPLANNER1814";
    private static readonly DateOnly Datum = new(2026, 9, 5);

    [SqlServerFact]
    public async Task BronduurZonderTeamOfSpeeltijd_BlijftZichtbaarMetVijftienMinutenOpslag()
    {
        await using var db = await Fixture.OpenAsync();
        await db.MatchAsync(duration: 60);

        var rows = await db.ReadAsync();

        var row = rows.Should().ContainSingle().Subject;
        row.AanvangsTijd.Should().Be(new TimeOnly(10, 0));
        row.EindTijd.Should().Be(new TimeOnly(11, 15));
        row.VeldDeelGebruik.Should().Be(1m);
        row.LeeftijdsCategorie.Should().BeNull();
        row.VeldNummer.Should().Be(-1814);
        row.VeldSubpositie.Should().Be("A");
    }

    [SqlServerFact]
    public async Task ZonderBeideDuurbronnen_ViewBewaartNullEnLezerBlokkeert()
    {
        await using var db = await Fixture.OpenAsync();
        await db.MatchAsync();

        await using (var cmd = db.Command("SELECT EindTijd FROM planner.AlleWedstrijdenOpVeld WHERE ClubCode = @club"))
            (await cmd.ExecuteScalarAsync()).Should().Be(DBNull.Value,
                "de onopgeloste bezetting blijft in de view staan met onbekende eindtijd");
        var read = () => db.ReadAsync();
        await read.Should().ThrowAsync<PlanningDuurOnbekendException>()
            .WithMessage("Planning geblokkeerd: wedstrijdduur onbekend voor bestaande veldbezetting.");
    }

    [SqlServerFact]
    public async Task ZonderBronduur_GebruiktSpeeltijdZonderExtraOpslag()
    {
        await using var db = await Fixture.OpenAsync();
        await db.SpeeltijdAsync();
        await db.MatchAsync();

        var row = (await db.ReadAsync()).Should().ContainSingle().Subject;
        row.EindTijd.Should().Be(new TimeOnly(11, 0));
        row.VeldDeelGebruik.Should().Be(0.5m);
    }

    [SqlServerFact]
    public async Task PositieveBronduur_GaatVoorSpeeltijd()
    {
        await using var db = await Fixture.OpenAsync();
        await db.SpeeltijdAsync();
        await db.MatchAsync(duration: 80);

        (await db.ReadAsync()).Should().ContainSingle().Which.EindTijd.Should().Be(new TimeOnly(11, 35));
    }

    [SqlServerFact]
    public async Task NietPositieveBronduur_ValtTerugOpSpeeltijd()
    {
        await using var db = await Fixture.OpenAsync();
        await db.SpeeltijdAsync();
        foreach (var duration in new[] { 0, -1 })
        {
            await db.MatchAsync(duration);
            (await db.ReadAsync()).Should().ContainSingle().Which.EindTijd.Should().Be(new TimeOnly(11, 0));
        }
    }

    [SqlServerFact]
    public async Task VerwijderdeBronduur_BlokkeertZonderSpeeltijd()
    {
        await using var db = await Fixture.OpenAsync();
        await db.MatchAsync(duration: 60);
        await db.ExecAsync("UPDATE his.matchdetails SET mta_deleted = SYSUTCDATETIME() WHERE ClubCode = @club");

        var read = () => db.ReadAsync();
        await read.Should().ThrowAsync<PlanningDuurOnbekendException>();
    }

    [SqlServerFact]
    public async Task OntbrekendOfOnbekendVeldZonderDuur_BlokkeertAndereVeldenNiet()
    {
        await using var db = await Fixture.OpenAsync();
        foreach (var field in new string?[] { null, "Onbekend veld" })
        {
            await db.MatchAsync(field: field);
            (await db.ReadAsync()).Should().BeEmpty();
        }
    }

    internal sealed class Fixture(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        private readonly List<string> _syncEnabledClubs = [];

        public static async Task<Fixture> OpenAsync()
        {
            var conn = new SqlConnection(Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar));
            await conn.OpenAsync();
            var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            var db = new Fixture(conn, tx);
            try
            {
                await using (var enabled = db.Command("SELECT ClubCode FROM dbo.AppSettings WHERE SyncEnabled = 1"))
                await using (var reader = await enabled.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) db._syncEnabledClubs.Add(reader.GetString(0));
                // CI heeft teams/matches na de gesimuleerde sync, maar nog geen matchdetails/view.
                // Gebruik de echte bronschema's, geen versimpelde testtabellen of schemaversoepeling.
                foreach (var table in new[] { "Teams", "Matches", "MatchDetails" })
                {
                    await using var exists = db.Command("SELECT OBJECT_ID(@name)");
                    exists.Parameters.AddWithValue("@name", $"his.{table}");
                    if (await exists.ExecuteScalarAsync() is DBNull)
                        await db.ExecAsync(await File.ReadAllTextAsync(Source($"Database/his/Tables/{table}.sql")));
                }
                var view = await File.ReadAllTextAsync(Source("Database/planner/Views/AlleWedstrijdenOpVeld.sql"));
                await db.ExecAsync("DROP VIEW IF EXISTS planner.AlleWedstrijdenOpVeld");
                await db.ExecAsync(view);
                await db.ExecAsync("""
                    UPDATE dbo.AppSettings SET SyncEnabled = 0;
                    INSERT INTO dbo.AppSettings
                        (ClubName, ClubCode, SportlinkApiUrl, SportlinkClientId, SeasonStartMonth, Accommodatie, SyncEnabled)
                    VALUES ('Planner Test', @club, 'https://allstars-fc.test', 'test-client', 7, 'Test accommodatie', 1);
                    INSERT INTO dbo.Velden (VeldNummer, VeldNaam, HeeftKunstlicht, ClubCode)
                    VALUES (-1814, 'Veld 1', 1, @club);
                    """);
                return db;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public SqlCommand Command(string sql)
        {
            var cmd = new SqlCommand(sql, connection, transaction);
            if (sql.Contains("@club", StringComparison.Ordinal))
                cmd.Parameters.AddWithValue("@club", Club);
            return cmd;
        }

        public async Task ExecAsync(string sql)
        {
            await using var cmd = Command(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MatchAsync(int? duration = null, string? field = "Veld 1 A")
        {
            await using var cmd = Command("""
                DELETE FROM his.matchdetails WHERE ClubCode = @club;
                DELETE FROM his.matches WHERE ClubCode = @club;
                INSERT INTO his.matches
                    (bk_matches, wedstrijdcode, kaledatum, aanvangstijd, accommodatie, status, teamnaam, wedstrijd, veld, ClubCode)
                VALUES ('planner1814', 1814, '2026-09-05', '10:00', 'Test accommodatie', 'Vastgesteld',
                        'ALLSTARS JO99-1', 'ALLSTARS JO99-1 - Gastteam', @field, @club);
                IF @duration IS NOT NULL
                    INSERT INTO his.matchdetails (bk_InternCode, WedstrijdCode, InternCode, Duration, ClubCode)
                    VALUES (1814, 1814, 1814, @duration, @club);
                """);
            cmd.Parameters.AddWithValue("@field", (object?)field ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", (object?)duration ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        internal async Task CommitEmailFixtureAsync(DateOnly date)
        {
            await using var cmd = Command("""
                UPDATE his.matches SET kaledatum = @datum WHERE ClubCode = @club;
                INSERT INTO dbo.Speeltijden (Leeftijd, Veldafmeting, WedstrijdTotaal, WedstrijdHelft, WedstrijdRust, ClubCode)
                VALUES ('JO13', 1, 60, 25, 10, @club);
                INSERT INTO dbo.VeldBeschikbaarheid (VeldNummer, DagVanWeek, BeschikbaarVanaf, BeschikbaarTot, GebruikZonsondergang, ClubCode)
                VALUES (-1814, @dag, '08:00', '20:00', 0, @club);
                """);
            cmd.Parameters.AddWithValue("@datum", date.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@dag", date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
            await cmd.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public Task SpeeltijdAsync() => ExecAsync("""
            INSERT INTO his.teams (bk_teams, teamnaam, leeftijdscategorie, ClubCode)
            VALUES ('planner1814', 'ALLSTARS JO99-1', 'Onder 99', @club);
            INSERT INTO dbo.Speeltijden (Leeftijd, Veldafmeting, WedstrijdTotaal, WedstrijdHelft, WedstrijdRust, ClubCode)
            VALUES ('JO99', 0.5, 60, 25, 10, @club);
            """);

        public Task<List<BestaandeWedstrijd>> ReadAsync() =>
            PlannerAvailabilityRepository.ReadMatchOccupationsAsync(connection, Datum, Club, transaction);

        public async ValueTask DisposeAsync()
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync();
            else
            {
                await using var clean = new SqlCommand("""
                    DELETE FROM his.matchdetails WHERE ClubCode = @club;
                    DELETE FROM his.matches WHERE ClubCode = @club;
                    DELETE FROM dbo.VeldBeschikbaarheid WHERE ClubCode = @club;
                    DELETE FROM dbo.Speeltijden WHERE ClubCode = @club;
                    DELETE FROM dbo.Velden WHERE ClubCode = @club;
                    DELETE FROM dbo.AppSettings WHERE ClubCode = @club;
                    """, connection);
                clean.Parameters.AddWithValue("@club", Club);
                await clean.ExecuteNonQueryAsync();
                foreach (var club in _syncEnabledClubs)
                {
                    await using var restore = new SqlCommand("UPDATE dbo.AppSettings SET SyncEnabled = 1 WHERE ClubCode = @club", connection);
                    restore.Parameters.AddWithValue("@club", club);
                    await restore.ExecuteNonQueryAsync();
                }
            }
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static string Source(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Sportlink-wedstrijdzaken.slnf")) ||
                    Directory.Exists(Path.Combine(dir.FullName, ".githooks")))
                    return Path.Combine(dir.FullName, relativePath);
            throw new DirectoryNotFoundException("Repository root niet gevonden.");
        }
    }
}
