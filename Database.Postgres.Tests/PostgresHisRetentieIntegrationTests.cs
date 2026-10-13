using AwesomeAssertions;
using Npgsql;
using Planner.Shared.Avg;
using Planner.Shared.Tests.Avg;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Seizoensretentie van de persoonskolommen in de historietabellen (#1698) tegen een echte
/// Postgres-instantie, plus RLS op de door de generator aangemaakte his- en stg-tabellen. Zelfde
/// opzet als <see cref="PostgresReconciliationIntegrationTests"/>. Testdata uitsluitend volgens
/// ARCHITECTUUR.md §8.1.5. De SQL Server-tegenhanger is <c>SqlServerHisRetentieIntegrationTests</c>.
/// </summary>
public class PostgresHisRetentieIntegrationTests : IAsyncLifetime
{
    private const string Club = "ALLSTARS";
    private static readonly DateOnly Grens = new(2026, 6, 30);
    private string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            "DROP TABLE IF EXISTS his.\"matches\" CASCADE; DROP TABLE IF EXISTS his.\"matchdetails\" CASCADE; " +
            "DROP TABLE IF EXISTS stg.\"matches\" CASCADE;", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task WisTotEnMet_MaaktAlleenPersoonskolommenVanAfgerondeSeizoenenLeeg_EnIsIdempotent()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await VoegMatchToe(connection, 1, "2026-06-30T14:30:00+02:00"); // laatste dag van het afgeronde seizoen
        await VoegMatchToe(connection, 2, "2026-07-01T14:30:00+02:00"); // nieuw seizoen
        await VoegMatchToe(connection, 3, "onbekend");                   // afwijkende vorm: niet raden
        await VoegMatchToe(connection, 4, "2026-02-30T10:00:00+01:00"); // onmogelijke datum: niet wissen
        await VoegMatchToe(connection, 5, "2024-02-29T10:00:00+01:00"); // geldige schrikkeldag: wel wissen
        await VoegMatchToe(connection, 6, "2025-02-29T10:00:00+01:00"); // geen schrikkeljaar: niet wissen
        await VoegDetailToe(connection, 11, new DateOnly(2026, 5, 2), null);
        await VoegDetailToe(connection, 12, new DateOnly(2026, 8, 2), null);
        await VoegDetailToe(connection, 13, null, new DateTime(2026, 4, 4, 12, 0, 0)); // alleen matchdatetime
        await VoegDetailToe(connection, 14, null, null);                                // geen datum

        var (matches, details) = await PostgresHisRetentie.WisTotEnMetAsync(connection, Club, Grens);

        matches.Should().Be(2);
        details.Should().Be(2);
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "scheidsrechter")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "rijders")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "thuisteam")).Should().Be("AllStars JO11-1", "de wedstrijd zelf blijft staan");
        (await Tekst(connection, "matches", "wedstrijdcode", 2, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matches", "wedstrijdcode", 3, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matches", "wedstrijdcode", 4, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matches", "wedstrijdcode", 5, "scheidsrechter")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 6, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matchdetails", "interncode", 11, "thuisteamemail")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 11, "opmerkingen")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 11, "thuisteam")).Should().Be("AllStars JO11-1");
        (await Tekst(connection, "matchdetails", "interncode", 12, "rijder")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matchdetails", "interncode", 13, "rijder")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 14, "rijder")).Should().Be("Jan de Vries");

        (await PostgresHisRetentie.WisTotEnMetAsync(connection, Club, Grens)).Should().Be((0, 0), "een tweede run raakt niets meer");
    }

    [PostgresFact]
    public async Task WisTotEnMet_DatuminvoerVolgtDeGedeeldeTabel_GelijkAanSqlServer()
    {
        // Codex-review ronde 2: welke waarden als datum tellen, ligt vast in één tabel die ook de
        // SQL Server-suite en de unit-test draaien.
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var gevallen = HisWedstrijddatumGevallen.Alle;
        for (var i = 0; i < gevallen.Count; i++)
            await VoegMatchToe(connection, 100 + i, gevallen[i].Waarde);

        var (matches, _) = await PostgresHisRetentie.WisTotEnMetAsync(connection, Club, HisWedstrijddatumGevallen.Grens);

        for (var i = 0; i < gevallen.Count; i++)
            (await Tekst(connection, "matches", "wedstrijdcode", 100 + i, "scheidsrechter")).Should().Be(
                gevallen[i].Wissen ? null : "Jan de Vries", $"geval {i}: {gevallen[i].Reden}");
        matches.Should().Be(gevallen.Count(g => g.Wissen));
        (await PostgresHisRetentie.WisTotEnMetAsync(connection, Club, HisWedstrijddatumGevallen.Grens)).Should().Be((0, 0));
    }

    [PostgresFact]
    public async Task WisTotEnMet_RaaktAlleenDeOpgegevenClub_BijTweeClubsMetVerschillendeSeizoenseinde()
    {
        // #1715: de retentie leest per club het seizoenseinde. Zelfde wedstrijddatum, twee clubs: alleen
        // de club waarvoor de grens is aangeleverd wordt leeggemaakt, de andere blijft staan.
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await VoegMatchToe(connection, 201, "2026-07-15T14:30:00+02:00", "ALLSTARS");
        await VoegMatchToe(connection, 202, "2026-07-15T14:30:00+02:00", "CLUBTWEE");
        await VoegDetailToe(connection, 211, new DateOnly(2026, 7, 15), null, "ALLSTARS");
        await VoegDetailToe(connection, 212, new DateOnly(2026, 7, 15), null, "CLUBTWEE");
        var julGrens = new DateOnly(2026, 8, 31); // club met septemberstart: seizoen tot en met 31 augustus

        var (matches, details) = await PostgresHisRetentie.WisTotEnMetAsync(connection, "CLUBTWEE", julGrens);

        (matches, details).Should().Be((1, 1));
        (await Tekst(connection, "matches", "wedstrijdcode", 202, "scheidsrechter")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 201, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matchdetails", "interncode", 212, "rijder")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 211, "rijder")).Should().Be("Jan de Vries");
    }

    [PostgresFact]
    public async Task WisTotEnMet_ZonderHistorietabellen_DoetNiets()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        (await PostgresHisRetentie.WisTotEnMetAsync(connection, Club, Grens)).Should().Be((0, 0));
    }

    [PostgresFact]
    public async Task LeesSeizoenseinden_LeestDeSeizoenstabel()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var telling = new NpgsqlCommand("SELECT COUNT(*) FROM public.season WHERE dateuntil IS NOT NULL", connection);
        var verwacht = Convert.ToInt32(await telling.ExecuteScalarAsync());

        (await PostgresHisRetentie.LeesSeizoenseindenAsync(connection)).Should().HaveCount(verwacht);
    }

    [PostgresFact]
    public async Task AlsOpslag_OpentEenEigenVerbinding_VoorLezenEnWissen()
    {
        // Zo roept de dagelijkse AVG-retentierun (CleanupFeedback) de tier aan: alleen met de
        // connectiestring, via de poort IHisRetentieOpslag.
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await VoegMatchToe(connection, 1, "2026-06-30T14:30:00+02:00");
        await VoegDetailToe(connection, 11, new DateOnly(2026, 5, 2), null);
        await using var telling = new NpgsqlCommand("SELECT COUNT(*) FROM public.season WHERE dateuntil IS NOT NULL", connection);
        var seizoenen = Convert.ToInt32(await telling.ExecuteScalarAsync());

        IHisRetentieOpslag opslag = new PostgresHisRetentie(ConnectionString);

        (await opslag.LeesSeizoenseindenAsync()).Should().HaveCount(seizoenen);
        (await opslag.WisTotEnMetAsync(Club, Grens)).Should().Be((1, 1));
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "scheidsrechter")).Should().BeNull();
        (await opslag.WisTotEnMetAsync(Club, Grens)).Should().Be((0, 0));
    }

    [PostgresFact]
    public async Task Generator_ZetRowLevelSecurityAanOpHisEnStg_OokBijHerhaling()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches); // idempotent, tweede keer no-op
        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        (await HeeftRls(connection, "his", "matches")).Should().BeTrue();
        (await HeeftRls(connection, "stg", "matches")).Should().BeTrue();
    }

    private static async Task VoegMatchToe(NpgsqlConnection c, long code, string? datum, string clubCode = Club)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO his.\"matches\" (\"wedstrijdcode\", \"wedstrijddatum\", \"thuisteam\", \"scheidsrechter\", " +
            "\"scheidsrechters\", \"rijders\", \"clubcode\", \"mta_inserted\", \"mta_modified\") " +
            "VALUES (@code, @datum, 'AllStars JO11-1', 'Jan de Vries', 'Jan de Vries', 'Jan de Vries', @club, NOW(), NOW())", c);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("club", clubCode);
        cmd.Parameters.Add(new NpgsqlParameter("datum", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)datum ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task VoegDetailToe(NpgsqlConnection c, int interncode, DateOnly? datum, DateTime? datumtijd, string clubCode = Club)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO his.\"matchdetails\" (\"wedstrijdcode\", \"interncode\", \"matchdate\", \"matchdatetime\", \"thuisteam\", " +
            "\"rijder\", \"opmerkingen\", \"thuisteamemail\", \"clubcode\", \"mta_inserted\", \"mta_modified\") " +
            "VALUES (1, @interncode, @datum, @datumtijd, 'AllStars JO11-1', 'Jan de Vries', 'Jan de Vries rijdt', " +
            "'trainer@voorbeeld.nl', @club, NOW(), NOW())", c);
        cmd.Parameters.AddWithValue("interncode", interncode);
        cmd.Parameters.AddWithValue("club", clubCode);
        cmd.Parameters.Add(new NpgsqlParameter("datum", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)datum ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("datumtijd", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = (object?)datumtijd ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Tekst(NpgsqlConnection c, string tabel, string sleutel, long waarde, string kolom)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {PostgresIdentifier.Quote(kolom)} FROM his.{PostgresIdentifier.Quote(tabel)} WHERE {PostgresIdentifier.Quote(sleutel)} = @w", c);
        cmd.Parameters.AddWithValue("w", waarde);
        var resultaat = await cmd.ExecuteScalarAsync();
        return resultaat is DBNull or null ? null : (string)resultaat;
    }

    private static async Task<bool> HeeftRls(NpgsqlConnection c, string schema, string tabel)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relrowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @s AND c.relname = @t", c);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", tabel);
        return await cmd.ExecuteScalarAsync() is true;
    }
}
