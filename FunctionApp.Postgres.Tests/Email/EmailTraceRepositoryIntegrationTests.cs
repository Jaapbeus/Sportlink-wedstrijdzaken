using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using Npgsql;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Bewijst tegen een echte Postgres dat <c>planner.emailtrace</c> idempotent upsert, per club
/// afgeschermd is en de verwerking overleeft (#1568, deel B). Vereist <c>POSTGRES_TEST_CONNECTION_STRING</c>
/// (zie <see cref="PostgresTestEnvironment"/>) en een gemigreerde database; draait in CI, niet lokaal zonder container.
/// </summary>
public class EmailTraceRepositoryIntegrationTests
{
    private const string ClubCode = "testclub-trace";
    private const string AndereClub = "testclub-trace-b";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private static EmailTraceRecord Record(int verwerkingId, string club, string zekerheidstype = "BeschikbaarheidCheck")
        => EmailTraceRecord.Van(verwerkingId, club, zekerheidstype,
            new TraceBuilder().Classificatie(zekerheidstype, true, true, 1, true).Bouw(), "1.0.0.0");

    [PostgresFact]
    public async Task Upsert_BewaartGeenRuweTeamtekst()
    {
        await SchoonAsync();
        var id = 900_002;
        var trace = new TraceBuilder().Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "Pieter komt zaterdag niet vanwege de regen", "Onopgelost", 0, null, null)
            .Bouw();

        await EmailTraceRepository.UpsertAsync(ConnectionString,
            EmailTraceRecord.Van(id, ClubCode, "BeschikbaarheidCheck", trace, "1.0.0.0"));

        var antwoord = await EmailTraceRepository.HaalOpAsync(ConnectionString, ClubCode, id);
        antwoord!.Trace!.Value.GetRawText().Should().NotContain("Pieter").And.NotContain("ruweTekst");
    }

    [PostgresFact]
    public async Task Upsert_TweemaalDezelfdeVerwerking_LevertEenRij()
    {
        await SchoonAsync();
        var id = 900_001;

        await EmailTraceRepository.UpsertAsync(ConnectionString, Record(id, ClubCode, "Overig"));
        await EmailTraceRepository.UpsertAsync(ConnectionString, Record(id, ClubCode, "BeschikbaarheidCheck"));

        (await Telling(id)).Should().Be(1);
        var antwoord = await EmailTraceRepository.HaalOpAsync(ConnectionString, ClubCode, id);
        antwoord.Should().NotBeNull();
        antwoord!.VerzoekType.Should().Be("BeschikbaarheidCheck", "de retry vervangt de eerdere trace");
        antwoord.Trace.Should().NotBeNull();
        antwoord.Status.Should().BeNull("er is geen bijbehorende verwerking; de trace staat op zichzelf");
    }

    [PostgresFact]
    public async Task HaalOp_VanAndereClub_GeeftNull()
    {
        await SchoonAsync();
        await EmailTraceRepository.UpsertAsync(ConnectionString, Record(900_002, ClubCode));

        (await EmailTraceRepository.HaalOpAsync(ConnectionString, AndereClub, 900_002)).Should().BeNull();
    }

    private static async Task<long> Telling(int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM planner.emailtrace WHERE verwerkingid = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task SchoonAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM planner.emailtrace WHERE clubcode IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("a", ClubCode);
        cmd.Parameters.AddWithValue("b", AndereClub);
        await cmd.ExecuteNonQueryAsync();
    }
}
