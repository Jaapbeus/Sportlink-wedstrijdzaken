using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Integratietests voor de Postgres-vertaling van <c>planner.AlleWedstrijdenOpVeld</c> (#819) —
/// zelfde draaiwijze als <see cref="PostgresMergeOrchestratorIntegrationTests"/> (zie die
/// klasse-doc-comment voor de wegwerpcontainer-instructies).
/// <para>
/// Deze tests zijn de "rij-voor-rij-vergelijkingstest" uit #819's testplan, benaderd als
/// hand-berekende verwachte waarden tegen een échte Postgres-instantie (niet als een live diff
/// tegen een draaiende SQL Server-instantie — dezelfde pragmatische keuze als #818 al maakte,
/// en bewust: de lokale SQL Server-devdatabase is gedeeld met andere, gelijktijdig actieve
/// sessies op dit project, en seedtest-data daarin zou die sessies kunnen raken).
/// </para>
/// </summary>
public class PostgresPlannerViewIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";
    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie PostgresMergeOrchestratorIntegrationTests.");

    private PostgresMergeOrchestrator Orchestrator => new(ConnectionString);
    private const string ClubCode = "vrc";

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"""
            DROP VIEW IF EXISTS {PostgresPlannerViewGenerator.ViewName};
            DROP TABLE IF EXISTS his."matches";
            DROP TABLE IF EXISTS stg."matches";
            DROP TABLE IF EXISTS his."teams";
            DROP TABLE IF EXISTS stg."teams";
            DROP TABLE IF EXISTS planner.geplandewedstrijden;
            DROP TABLE IF EXISTS public.speeltijden;
            DROP TABLE IF EXISTS public.velden;
            DROP TABLE IF EXISTS public.appsettings;
            """, connection);
        await drop.ExecuteNonQueryAsync();

        // stg/his bestaan hier niet automatisch (#818's tests veronderstellen ze al aanwezig via
        // een niet-geautomatiseerde stap — geen scope van #819 om dat te verhelpen); voor deze
        // zelfstandige test scheppen we ze hier expliciet.
        await using var schemas = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS stg; CREATE SCHEMA IF NOT EXISTS his;", connection);
        await schemas.ExecuteNonQueryAsync();

        foreach (var ddl in PostgresPlannerSupportSchema.AllInOrder)
        {
            await using var cmd = new NpgsqlCommand(ddl, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        await Orchestrator.RecreateStgTableAsync(KnownEntities.Matches);
        await Orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await Orchestrator.RecreateStgTableAsync(KnownEntities.Teams);
        await Orchestrator.EnsureHisTableAsync(KnownEntities.Teams);

        await using var createView = new NpgsqlCommand(PostgresPlannerViewGenerator.CreateView, connection);
        await createView.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, Action<NpgsqlParameterCollection>? bind = null)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        bind?.Invoke(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedAppSettingsAsync(NpgsqlConnection connection, string accommodatie = "Sportpark")
        => await ExecAsync(connection,
            "INSERT INTO public.appsettings (clubcode, accommodatie, syncenabled) VALUES (@c, @a, true)",
            p => { p.AddWithValue("c", ClubCode); p.AddWithValue("a", accommodatie); });

    private async Task SeedVeldAsync(NpgsqlConnection connection, int veldNummer, string veldNaam)
        => await ExecAsync(connection,
            "INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode) VALUES (@n, @naam, true, @c)",
            p => { p.AddWithValue("n", veldNummer); p.AddWithValue("naam", veldNaam); p.AddWithValue("c", ClubCode); });

    private async Task SeedSpeeltijdenAsync(NpgsqlConnection connection, string leeftijd, int wedstrijdTotaal)
        => await ExecAsync(connection,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES (@l, 1.00, @t, @c)",
            p => { p.AddWithValue("l", leeftijd); p.AddWithValue("t", wedstrijdTotaal); p.AddWithValue("c", ClubCode); });

    private async Task SeedMatchAsync(
        NpgsqlConnection connection, long wedstrijdcode, string teamnaam, string veld,
        string kaledatum = "2026-09-05", string aanvangstijd = "10:00", string accommodatie = "Sportpark Oost")
        => await ExecAsync(connection, """
            INSERT INTO stg."matches"
                ("wedstrijdcode", "teamnaam", "veld", "kaledatum", "aanvangstijd", "accommodatie", "status", "wedstrijd", "ClubCode")
            VALUES (@code, @team, @veld, @datum, @tijd, @acc, 'Bevestigd', @team || ' - Uit', @club)
            """, p =>
        {
            p.AddWithValue("code", wedstrijdcode);
            p.AddWithValue("team", teamnaam);
            p.AddWithValue("veld", veld);
            p.AddWithValue("datum", kaledatum);
            p.AddWithValue("tijd", aanvangstijd);
            p.AddWithValue("acc", accommodatie);
            p.AddWithValue("club", ClubCode);
        });

    private async Task SeedTeamAsync(NpgsqlConnection connection, string teamnaam, string leeftijdscategorie)
        => await ExecAsync(connection, """
            INSERT INTO stg."teams" ("teamcode", "lokaleteamcode", "poulecode", "teamnaam", "leeftijdscategorie", "ClubCode")
            VALUES (1, 1, 1, @team, @leeftijd, @club)
            """, p =>
        {
            p.AddWithValue("team", teamnaam);
            p.AddWithValue("leeftijd", leeftijdscategorie);
            p.AddWithValue("club", ClubCode);
        });

    /// <summary>
    /// De #719-regressie: "veld 10" mag niet als bezetting op "veld 1" belanden. Dit is de
    /// rechtstreekse Postgres-tier-tegenhanger van de reden waarom deze view/laag bestaat.
    /// </summary>
    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task GetFieldOccupationsAsync_Veld10_ResolvedNaarVeld10NietVeld1()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedAppSettingsAsync(connection);
        await SeedVeldAsync(connection, 1, "Veld 1");
        await SeedVeldAsync(connection, 10, "Veld 10");
        await SeedSpeeltijdenAsync(connection, "JO13", 75);
        await SeedTeamAsync(connection, "VRC JO13-1", "Onder 13");
        await SeedMatchAsync(connection, 111, "VRC JO13-1", "Veld 10");
        await Orchestrator.MergeStgToHisAsync(KnownEntities.Matches);
        await Orchestrator.MergeStgToHisAsync(KnownEntities.Teams);

        var rijen = await PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync(
            connection, new DateOnly(2026, 9, 5), ClubCode);

        rijen.Should().ContainSingle();
        rijen[0].VeldNummer.Should().Be(10, "'veld 10' mag nooit op 'veld 1' matchen (#719)");
    }

    /// <summary>Een veldstring die geen enkel veld matcht valt uit de bezetting — geen crash, geen valse rij.</summary>
    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task GetFieldOccupationsAsync_OnbekendVeld_RijValtWeg()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedAppSettingsAsync(connection);
        await SeedVeldAsync(connection, 1, "Veld 1");
        await SeedSpeeltijdenAsync(connection, "JO13", 75);
        await SeedTeamAsync(connection, "VRC JO13-1", "Onder 13");
        await SeedMatchAsync(connection, 222, "VRC JO13-1", "Onbekend Veld X");
        await Orchestrator.MergeStgToHisAsync(KnownEntities.Matches);
        await Orchestrator.MergeStgToHisAsync(KnownEntities.Teams);

        var rijen = await PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync(
            connection, new DateOnly(2026, 9, 5), ClubCode);

        rijen.Should().BeEmpty();
    }

    /// <summary>G-team-detectie via de regex-operator ~ (vertaling van SQL Server's LIKE-bracket-patroon).</summary>
    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task GetFieldOccupationsAsync_GTeam_GebruiktGSpeeltijdIStandaardLeeftijdscategorie()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedAppSettingsAsync(connection);
        await SeedVeldAsync(connection, 1, "Veld 1");
        await SeedSpeeltijdenAsync(connection, "G", 50); // G-teams: eigen, kortere speeltijd
        await SeedSpeeltijdenAsync(connection, "JO9", 60);
        // Geen team-rij nodig: G-detectie loopt via teamnaam zelf, niet via leeftijdscategorie.
        await SeedMatchAsync(connection, 333, "VRC G7-1", "Veld 1");
        await Orchestrator.MergeStgToHisAsync(KnownEntities.Matches);

        var rijen = await PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync(
            connection, new DateOnly(2026, 9, 5), ClubCode);

        rijen.Should().ContainSingle();
        rijen[0].EindTijd.Should().Be(rijen[0].AanvangsTijd.AddMinutes(50),
            "G7-1 moet de Speeltijden-rij voor 'G' gebruiken (50 min), niet de generieke JO-omzetting");
    }

    /// <summary>De "Planner"-tak levert al een resolved veldnummer — geen matching, direct doorgezet.</summary>
    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task GetFieldOccupationsAsync_PlannerRij_VeldnummerRechtstreeksDoorgezetZonderMatching()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedAppSettingsAsync(connection);
        await SeedVeldAsync(connection, 3, "Veld 3");
        await ExecAsync(connection, """
            INSERT INTO planner.geplandewedstrijden
                (datum, aanvangstijd, eindtijd, veldnummer, teamnaam, tegenstander, status, isvervallen, clubcode)
            VALUES ('2026-09-05', '18:00', '19:15', 3, 'VRC JO15-1', 'Gastteam', 'Te bevestigen', false, @club)
            """, p => p.AddWithValue("club", ClubCode));

        var rijen = await PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync(
            connection, new DateOnly(2026, 9, 5), ClubCode);

        rijen.Should().ContainSingle();
        rijen[0].VeldNummer.Should().Be(3);
        rijen[0].Bron.Should().Be("Planner");
        rijen[0].Wedstrijd.Should().Be("VRC JO15-1 - Gastteam");
    }
}
