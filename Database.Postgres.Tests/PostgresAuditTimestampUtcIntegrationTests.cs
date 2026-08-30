using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Bewijst dat de audit-tijdstempels (<c>mta_inserted</c>/<c>mta_modified</c>) van een his-tabel
/// altijd de echte UTC-tijd vastleggen, ook als de databasesessie op een niet-UTC tijdzone draait
/// (#854). Zelfde draaiwijze als <see cref="PostgresMergeOrchestratorIntegrationTests"/>.
/// <para>
/// De test zet de sessietijdzone zelf op <c>Europe/Amsterdam</c> via <c>SET TIME ZONE</c> — dat
/// bewijst het probleem/de fix onafhankelijk van hoe de wegwerpcontainer zelf is gestart, en is
/// dus ook betrouwbaar als de container toevallig al op UTC draait. Een test die alleen op UTC
/// draait bewijst hier niets (#854's eigen acceptatiecriterium): het foutscenario ontstaat precies
/// door het verschil tussen sessietijdzone en UTC.
/// </para>
/// </summary>
public class PostgresAuditTimestampUtcIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";
    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie PostgresMergeOrchestratorIntegrationTests.");

    private PostgresMergeOrchestrator Orchestrator => new(ConnectionString);

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // CASCADE: een andere testklasse (PostgresPlannerViewIntegrationTests) kan een view op
        // his.matches hebben laten staan van een vorige run — nu tests sequentieel draaien
        // (AssemblyInfo.cs) is de uitvoeringsvolgorde relevant geworden voor dit soort restjes.
        await using var drop = new NpgsqlCommand(
            "DROP TABLE IF EXISTS his.\"matches\" CASCADE; DROP TABLE IF EXISTS stg.\"matches\" CASCADE;", connection);
        await drop.ExecuteNonQueryAsync();
        // stg/his-schema's worden nu door PostgresMergeOrchestrator zelf aangemaakt (idempotent).
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task MergeStgToHisAsync_SessieOpNietUtcTijdzone_AuditTijdstempelIsTochEchteUtcTijd()
    {
        var entity = TestEntities.SingleKeyNoClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        // Options=-c timezone=... zet de PostgreSQL GUC 'timezone' al bij het opzetten van elke
        // nieuwe connectie op deze connectiestring — dus ook de connectie die de orchestrator zelf
        // intern opent (die opent immers een eigen NpgsqlConnection per methode, niet de connectie
        // hieronder). Een losse 'SET TIME ZONE' op deze ene testconnectie zou de orchestrator's
        // eigen, aparte connectie niet raken.
        var tzConnectionString = ConnectionString.Contains(';')
            ? $"{ConnectionString.TrimEnd(';')};Options=-c timezone=Europe/Amsterdam"
            : $"{ConnectionString};Options=-c timezone=Europe/Amsterdam";
        var tzOrchestrator = new PostgresMergeOrchestrator(tzConnectionString);

        await using var connection = new NpgsqlConnection(tzConnectionString);
        await connection.OpenAsync();

        await using (var checkTz = new NpgsqlCommand("SHOW TIME ZONE;", connection))
            (await checkTz.ExecuteScalarAsync()).Should().Be("Europe/Amsterdam",
                "de test bewijst niets als de sessietijdzone toch op UTC staat (#854's eigen acceptatiecriterium)");

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO stg.\"matches\" (\"matchcode\", \"datum\") VALUES ('M-UTC-1', '2026-09-01 10:00:00')",
            connection))
        {
            await insert.ExecuteNonQueryAsync();
        }

        var voorSchrijven = DateTime.UtcNow;
        await tzOrchestrator.MergeStgToHisAsync(entity);
        var naSchrijven = DateTime.UtcNow;

        await using var read = new NpgsqlCommand(
            "SELECT \"mta_modified\" FROM his.\"matches\" WHERE \"matchcode\" = 'M-UTC-1'", connection);
        var mtaModified = (DateTime)(await read.ExecuteScalarAsync())!;

        mtaModified.Kind.Should().Be(DateTimeKind.Utc,
            "Npgsql moet een TIMESTAMPTZ-kolom teruggeven met Kind=Utc, niet Unspecified");
        mtaModified.Should().BeOnOrAfter(voorSchrijven.AddSeconds(-1))
            .And.BeOnOrBefore(naSchrijven.AddSeconds(1),
                "de weggeschreven waarde moet de echte UTC-tijd zijn, niet de Europe/Amsterdam-sessietijd " +
                "(die op dit moment van het jaar 2 uur zou verschillen)");
    }
}
