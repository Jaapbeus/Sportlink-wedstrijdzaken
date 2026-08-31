using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.TeamResolution;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Maakt de verificatie van #923 (§28) blijvend. Die ronde toonde de vertaling van
/// <see cref="TeamCanonicalisatieService"/> aan met een wegwerp-consoleproject; deze klasse
/// herhaalt de kernscenario's als env-gestuurde integratietest (#866), zodat een regressie
/// zichtbaar wordt in plaats van pas bij de volgende handmatige meting.
///
/// <para>
/// De drie scenario's hieronder zijn niet willekeurig gekozen: het zijn precies de drie waarvoor
/// §28 een <b>negatieve controle</b> heeft vastgelegd — een plausibele naïeve vertaling die de
/// betreffende assertie daadwerkelijk rood maakt. Een test zonder zo'n bewezen faalpad bewaakt
/// niets.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class TeamCanonicalisatieIntegrationTests
{
    private const string ClubCode = "testclub-canon";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    /// <summary>
    /// Kernscenario: Sportlink levert hetzelfde fysieke team in twee schrijfwijzen zonder gedeelde
    /// sleutel (lokaal <c>JO10-1</c>, bondsnotatie <c>[club] O10-1</c>) én meerdere keren per poule.
    /// Dat moet één canoniek team opleveren met beide schrijfwijzen als gevalideerde alias.
    /// <para>
    /// Negatieve controle uit §28: met <c>ON CONFLICT</c> op de kale kolommen in plaats van op de
    /// expression-based index (#820) faalt dit met <c>42P10</c> en blijft <c>public.teams</c> leeg.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task RefreshAsync_TweeSchrijfwijzen_LeverenEenCanoniekTeamMetBeideAliassen()
    {
        await SchoonAsync();
        await HisTeamAsync("TESTCANON JO10-1", "Onder 10", "lokaal");
        await HisTeamAsync("TESTCANON O10-1", "Onder 10", "bond");
        await HisTeamAsync("TESTCANON JO10-1", "Onder 10", "lokaal");   // zelfde naam, andere poule

        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);

        (await CountAsync("SELECT count(*) FROM public.teams WHERE clubcode = @club"))
            .Should().Be(1, "drie his.teams-rijen van hetzelfde fysieke team horen tot één canoniek team te leiden");

        (await ScalarAsync<string?>("SELECT teamnaam FROM public.teams WHERE clubcode = @club"))
            .Should().Be("TESTCANON O10-1",
                "de bondsnotatie heeft voorkeur als weergavenaam — die vorm staat ook in his.matches.wedstrijd");

        (await ScalarAsync<string?>("SELECT leeftijdscategorie FROM public.teams WHERE clubcode = @club"))
            .Should().Be("JO10", "'Onder 10' wordt genormaliseerd door Planner.Shared.LeeftijdNormalisatie");

        var aliassen = await LijstAsync(
            "SELECT ruwetekst FROM public.teamaliassen WHERE clubcode = @club AND status = 'validated' ORDER BY 1");
        aliassen.Should().BeEquivalentTo(["TESTCANON JO10-1", "TESTCANON O10-1"],
            "beide bronschrijfwijzen horen als gevalideerde Sync-alias vastgelegd te worden (#700)");

        // Idempotentie: een tweede identieke run mag niets toevoegen.
        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);
        (await CountAsync("SELECT count(*) FROM public.teams WHERE clubcode = @club")).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM public.teamaliassen WHERE clubcode = @club")).Should().Be(2);
    }

    /// <summary>
    /// CLAUDE.md's harde regel: *"een geleerde alias is pas waarheid na goedkeuring"*. De sync mag
    /// een alias met <c>bron &lt;&gt; 'Sync'</c> dus niet aanraken.
    /// <para>
    /// Negatieve controle uit §28: zonder de <c>WHERE</c>-clausule op <c>DO UPDATE</c> springt de
    /// status van <c>pending</c> naar <c>validated</c> — een stille schending van die regel.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task RefreshAsync_GeleerdeAliasBlijftPending_EnWordtNietDoorDeSyncGevalideerd()
    {
        await SchoonAsync();
        await HisTeamAsync("TESTCANON JO11-1", "Onder 11", "bond");
        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);

        await ExecAsync(
            "UPDATE public.teamaliassen SET bron = 'Leren', status = 'pending' WHERE clubcode = @club",
            ("club", ClubCode));

        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);

        (await ScalarAsync<string?>("SELECT status FROM public.teamaliassen WHERE clubcode = @club"))
            .Should().Be("pending",
                "een geleerde alias is pas waarheid na goedkeuring door een coordinator — de sync mag hem "
                + "niet stilzwijgend op 'validated' zetten (CLAUDE.md, regel 4 onder Teamnaam-resolutie)");
        (await ScalarAsync<string?>("SELECT bron FROM public.teamaliassen WHERE clubcode = @club"))
            .Should().Be("Leren");
    }

    /// <summary>
    /// #820: Postgres' default-collatie is hoofdlettergevoelig. Een opgeslagen sleutel met
    /// afwijkende kast moet nog steeds als dezelfde rij herkend worden, anders valt de upsert in de
    /// INSERT-tak en botst hij op de unique index over <c>teamnaam</c> — waarna het team stilzwijgend
    /// wordt overgeslagen en door de deactiveringsstap op <c>isactief = false</c> belandt.
    /// </summary>
    [PostgresFact]
    public async Task RefreshAsync_AfwijkendeCasingInDeOpgeslagenSleutel_LeidtNietTotEenDuplicaat()
    {
        await SchoonAsync();
        await HisTeamAsync("TESTCANON MO13-2", "JO13 Meiden", "bond");
        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);

        (await ScalarAsync<string?>("SELECT leeftijdscategorie FROM public.teams WHERE clubcode = @club"))
            .Should().Be("MO13", "'JO13 Meiden' is Sportlinks schrijfwijze voor een meidenteam (#486)");

        await ExecAsync(
            "UPDATE public.teams SET teamnaamgenormaliseerd = lower(teamnaamgenormaliseerd) WHERE clubcode = @club",
            ("club", ClubCode));

        await TeamCanonicalisatieService.RefreshAsync(ConnectionString, ClubCode, NullLogger.Instance);

        (await CountAsync("SELECT count(*) FROM public.teams WHERE clubcode = @club"))
            .Should().Be(1, "de upsert moet op UPPER(...) infereren; anders komt er een tweede rij bij of faalt hij");
        (await ScalarAsync<bool>("SELECT isactief FROM public.teams WHERE clubcode = @club"))
            .Should().BeTrue("het team bestaat nog in his.teams en hoort dus actief te blijven");
    }

    private static async Task SchoonAsync()
    {
        // his.teams bestaat op een verse database pas na de eerste EnsureHisTableAsync, én kan door
        // een andere testsuite in een afwijkende vorm zijn achtergelaten — zie HisTabelVorm. Altijd
        // via de echte productie-generator, nooit handgeschreven DDL.
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams);

        await ExecAsync(
            "DELETE FROM his.teams WHERE clubcode = @club; " +
            "DELETE FROM public.teamaliassen WHERE clubcode = @club; " +
            "DELETE FROM public.teams WHERE clubcode = @club;", ("club", ClubCode));
    }

    private static async Task HisTeamAsync(string teamnaam, string leeftijdscategorie, string teamsoort)
    {
        // bk_teams is een GENERATED-kolom (#818) — niet zelf invullen. teamcode/lokaleteamcode/
        // poulecode vormen de business key, dus elke rij krijgt een eigen combinatie.
        await ExecAsync(@"
            INSERT INTO his.teams
                (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, teamsoort, clubcode,
                 mta_inserted, mta_modified)
            VALUES (@code, @code, @code, @teamnaam, @cat, @soort, @club, NOW(), NOW())",
            ("code", Random.Shared.NextInt64(1, 100_000_000)),
            ("teamnaam", teamnaam), ("cat", leeftijdscategorie), ("soort", teamsoort), ("club", ClubCode));
    }

    private static async Task ExecAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string sql) => await ScalarAsync<long>(sql);

    private static async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private static async Task<List<string>> LijstAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        var resultaten = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) resultaten.Add(reader.GetString(0));
        return resultaten;
    }
}
