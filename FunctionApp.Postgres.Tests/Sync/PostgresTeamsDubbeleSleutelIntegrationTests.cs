using AwesomeAssertions;
using Database.Postgres;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Sync;
using FunctionApp.Tests.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// #1712: de dagelijkse sync faalde met SQLSTATE 21000 (cardinality violation) op de upsert naar
/// <c>his.teams</c>. <c>/teams</c> leverde een team twee keer met dezelfde business key
/// (<c>teamcode</c>, <c>lokaleteamcode</c>, <c>poulecode</c>); <c>stg.teams</c> nam beide rijen
/// over, en <c>INSERT … ON CONFLICT DO UPDATE</c> mag in één opdracht niet twee keer dezelfde rij
/// raken. Omdat de teams-merge de eerste mergestap is, bleven daardoor ook <c>his.matches</c> en
/// <c>his.matchdetails</c> die run ongewijzigd.
/// <para>
/// Deze test speelt dat pad na met de fixtureserver: zonder ontdubbeling gooit
/// <see cref="PostgresSyncPipeline.RunSyncAsync"/> een <see cref="PostgresException"/> met 21000.
/// De verwachting is "laatste wint" (WZ-ADR-013: Sportlink is de bron van waarheid).
/// </para>
/// </summary>
public class PostgresTeamsDubbeleSleutelIntegrationTests
{
    private const string ClubCode = "testclub-teamsdubbel";
    private const long Wedstrijdcode = 90171201;
    private const int DubbeleTeamcode = 97120;
    private const int AndereTeamcode = 97121;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie PostgresSyncFixtureIntegrationTests.");

    [PostgresTheory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task RunSyncAsync_TeamTweeKeerInTeamsRespons_SlaagtEnLaatsteWint(int? poulecode)
    {
        await SchoonAsync();

        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);
        fixtureServer.RespondWithJson("/teams", TeamsJson(poulecode));

        var run = async () => await PostgresSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0,
            sportlinkApiUrl: fixtureServer.BaseUrl,
            sportlinkClientId: "clientId=fixture-test",
            clubCode: ClubCode,
            connectionString: ConnectionString,
            log: NullLogger.Instance,
            reconciliatieOndergrens: new DateOnly(2026, 9, 1));

        await run.Should().NotThrowAsync(
            "een dubbele business key in /teams mag de upsert niet laten falen met 21000 (#1712)");

        (await ScalarAsync<long>("SELECT count(*) FROM his.teams WHERE clubcode = @club"))
            .Should().Be(2, "twee unieke business keys in de respons geven twee his-rijen");
        (await ScalarAsync<string>(
                "SELECT competitienaam FROM his.teams WHERE clubcode = @club AND teamcode = @teamcode"))
            .Should().Be("Tweede vermelding", "bij een dubbele sleutel wint de laatste vermelding uit Sportlink");
        (await ScalarAsync<long>(
                "SELECT count(*) FROM his.matches WHERE clubcode = @club AND wedstrijdcode = @code"))
            .Should().Be(1, "de mergestappen na de teams moeten ook gedraaid hebben");
    }

    private static string TeamsJson(int? poulecode)
    {
        var poule = poulecode?.ToString() ?? "null";
        return $$"""
            [
              {{Team(DubbeleTeamcode, 1, poule, "Eerste vermelding")}},
              {{Team(AndereTeamcode, 2, poule, "Ander team")}},
              {{Team(DubbeleTeamcode, 1, poule, "Tweede vermelding")}}
            ]
            """;
    }

    private static string Team(int teamcode, int lokaleteamcode, string poulecode, string competitienaam) => $$"""
        {
          "teamcode": {{teamcode}},
          "lokaleteamcode": {{lokaleteamcode}},
          "poulecode": {{poulecode}},
          "teamnaam": "{{ClubCode}} JO13-{{lokaleteamcode}}",
          "competitienaam": "{{competitienaam}}",
          "klasse": "3e klasse",
          "poule": "03 (C)",
          "klassepoule": "3e klasse 03 (C)",
          "spelsoort": "veld",
          "competitiesoort": "regulier",
          "geslacht": "M",
          "teamsoort": "veld",
          "leeftijdscategorie": "Onder 13",
          "kalespelsoort": "veld",
          "speeldag": "zaterdag",
          "speeldagteam": "{{ClubCode}} JO13-{{lokaleteamcode}}",
          "more": ""
        }
        """;

    private static async Task SchoonAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(
            ConnectionString, KnownEntities.Teams, KnownEntities.Matches, KnownEntities.MatchDetails);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var opruimen = new NpgsqlCommand(
            "DELETE FROM his.matchdetails WHERE clubcode = @club; " +
            "DELETE FROM his.matches WHERE clubcode = @club; " +
            "DELETE FROM his.teams WHERE clubcode = @club; " +
            "DELETE FROM public.teamaliassen WHERE clubcode = @club; " +
            "DELETE FROM public.teams WHERE clubcode = @club;", conn);
        opruimen.Parameters.AddWithValue("club", ClubCode);
        await opruimen.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        cmd.Parameters.AddWithValue("code", Wedstrijdcode);
        cmd.Parameters.AddWithValue("teamcode", DubbeleTeamcode);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
