using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.TeamResolution;
using Npgsql;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Gedrag van <see cref="PostgresAfzenderTeamBron"/> (#1862) tegen een echte Postgres: hoofdletter- en spatieonafhankelijke
/// adresvergelijking, alleen teamwaarden terug, hard gescoped op club, en gebruik van de expressie-index uit
/// migratie 049. Vereist een gemigreerde database (zie <see cref="PostgresSyncFixtureIntegrationTests"/>); zonder de
/// omgevingsvariabele worden de tests overgeslagen. Uitsluitend fictieve gegevens (<c>@allstars-fc.test</c>).
/// </summary>
public class AfzenderTeamBronIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    private const string Club = "atb-club";
    private const string AndereClub = "atb-club-2";
    private const string Adres = "trainer@allstars-fc.test";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet; zie klasse-doc-comment.");

    private static PostgresAfzenderTeamBron Bron() => new(() => ConnectionString);

    [PostgresFact]
    public async Task EenTeam_ZonderOnderscheidVanHoofdlettersEnSpaties()
    {
        await using var conn = await OpstellingAsync();
        await ZetAsync(conn, Club, "O23-1", "  Trainer@Allstars-FC.test ");

        var teams = await Bron().ZoekTeamsVanAfzenderAsync(AfzenderTeamBronHulp.NormaliseerAdres(" TRAINER@allstars-fc.TEST"), Club);

        teams.Should().Equal("O23-1");
    }

    [PostgresFact]
    public async Task TweeRijenVoorHetzelfdeTeam_GevenEenTeamwaarde_EnTweeTeamsGevenTwee()
    {
        await using var conn = await OpstellingAsync();
        await ZetAsync(conn, Club, "O23-1", Adres);
        await ZetAsync(conn, Club, "O23-1", Adres);
        await ZetAsync(conn, Club, "JO13-2", Adres);

        var teams = await Bron().ZoekTeamsVanAfzenderAsync(AfzenderTeamBronHulp.NormaliseerAdres(Adres), Club);

        teams.Should().BeEquivalentTo(["O23-1", "JO13-2"]);
    }

    [PostgresFact]
    public async Task OnbekendAdres_LegeTeamwaardeEnAndereClub_GevenNiets()
    {
        await using var conn = await OpstellingAsync();
        await ZetAsync(conn, Club, "", Adres);
        await ZetAsync(conn, Club, "O23-1", "ander@allstars-fc.test");
        await ZetAsync(conn, AndereClub, "O23-1", Adres);

        var teams = await Bron().ZoekTeamsVanAfzenderAsync(AfzenderTeamBronHulp.NormaliseerAdres(Adres), Club);

        teams.Should().BeEmpty("een andere club mag nooit meelekken en een rij zonder team is geen team");
    }

    [PostgresFact]
    public async Task Opzoeking_GebruiktDeExpressieIndex()
    {
        await using var conn = await OpstellingAsync();
        await ZetAsync(conn, Club, "O23-1", Adres);
        await using (var uit = new NpgsqlCommand("SET enable_seqscan = off", conn)) await uit.ExecuteNonQueryAsync();

        // De productieopdracht zelf (met clubfilter en parameters), zodat een wijziging aan het predicaat hier zichtbaar wordt.
        await using var cmd = PostgresAfzenderTeamBron.BouwOpdracht(conn, AfzenderTeamBronHulp.NormaliseerAdres(Adres), Club);
        cmd.CommandText = "EXPLAIN " + cmd.CommandText.TrimStart();
        var plan = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));

        string.Join("\n", plan).Should().Contain("ix_teambegeleiding_email_lower");
    }

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        PostgresAppSettings.SetForTests("clubCode", Club);
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM avg.teambegeleiding WHERE clubcode = @club OR clubcode = @andere", conn);
        cmd.Parameters.AddWithValue("club", Club);
        cmd.Parameters.AddWithValue("andere", AndereClub);
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task ZetAsync(NpgsqlConnection conn, string clubCode, string team, string emailadres)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (team, naam, teamrol, emailadres, clubcode)
            VALUES (@team, 'Jan', 'Trainer', @email, @club)
            """, conn);
        cmd.Parameters.AddWithValue("team", team);
        cmd.Parameters.AddWithValue("email", emailadres);
        cmd.Parameters.AddWithValue("club", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }
}
