using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="PostgresCleanupProcedures.CleanupAppSettingsAuditAsync"/> vast
/// (#781/#861) — de AVG-bewaartermijn op <c>public.appsettingsaudit</c>.
///
/// <para>
/// <b>Waarom dit een eigen testklasse verdient en niet één happy-path-assertie:</b> de procedure
/// kiest de bewaartermijn via een drietraps-terugval (primaire club → willekeurige club → default
/// 730 dagen). Elke trap kan afzonderlijk stilzwijgend verkeerd gaan, en de gevolgen liggen in
/// tegengestelde richtingen: een te ruime termijn is een AVG-overtreding (art. 5 lid 1 sub e), een
/// te krappe termijn vernietigt een audittrail die er nog had moeten zijn. Beide zijn onzichtbaar
/// zonder gerichte meting, want de taak draait maandelijks en logt alleen "geslaagd".
/// </para>
///
/// <para>
/// Elke test controleert daarom niet alleen wát verdwijnt maar ook wát blijft staan — een test die
/// alleen "er is iets verwijderd" aantoont, zou een procedure die álles verwijdert groen laten.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class AppSettingsAuditCleanupIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task PrimaireClubIsLeidend_DemoclubWaardeWordtGenegeerd()
    {
        await using var conn = await OpenAsync();
        // Democlub roept 9999 dagen; primaire club 10. Wint de democlub, dan blijft de 40-dagenrij
        // ten onrechte staan.
        await ZetClubsAsync(conn, ("ALLSTARS", 9999, false), ("zprimary", 10, true));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 5 },
            "de primaire club (10 dagen) is leidend, niet de democlub");
    }

    [PostgresFact]
    public async Task AlleenDemoclubAanwezig_VangnetGebruiktDiensWaarde()
    {
        await using var conn = await OpenAsync();
        // Verse fork vóór de eerste echte configuratie: dan telt de democlubwaarde wél mee.
        await ZetClubsAsync(conn, ("ALLSTARS", 10, false));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 5 });
    }

    [PostgresFact]
    public async Task OnzinnigeWaarde_ValtTerugOpDefaultEnVerwijdertNietAlles()
    {
        await using var conn = await OpenAsync();
        // 0 letterlijk toepassen zou 'ouder dan nu' betekenen: alles weg. De terugval op 730
        // dagen moet de 400-dagenrij juist laten staan.
        await ZetClubsAsync(conn, ("zprimary", 0, true));
        await SeedAuditAsync(conn, 400);

        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 400 },
            "een waarde <= 0 valt terug op 730 dagen in plaats van alles te verwijderen");
    }

    [PostgresFact]
    public async Task DefaultBewaartermijn_VerwijdertWelDegelijkOudereRijen()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("zprimary", 0, true));
        await SeedAuditAsync(conn, 400, 800);

        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 400 },
            "800 dagen is ouder dan de default van 730 en moet wél verdwijnen");
    }

    [PostgresFact]
    public async Task GeenEnkeleAppSettingsRij_GebruiktDefaultZonderFout()
    {
        await using var conn = await OpenAsync();
        await VerwijderClubsAsync(conn);
        await SeedAuditAsync(conn, 400, 800);

        var act = async () => await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        await act.Should().NotThrowAsync();
        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 400 });
    }

    [PostgresFact]
    public async Task TweedeAanroep_IsIdempotent()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("zprimary", 10, true));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);
        var naEerste = await LeeftijdenAsync(conn);
        await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(naEerste);
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task VerwijderClubsAsync(NpgsqlConnection conn) =>
        ExecAsync(conn, "DELETE FROM public.appsettings");

    private static async Task ZetClubsAsync(
        NpgsqlConnection conn, params (string Club, int BewaarDagen, bool SyncEnabled)[] clubs)
    {
        await VerwijderClubsAsync(conn);
        foreach (var (club, dagen, sync) in clubs)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO public.appsettings (clubcode, syncenabled, appsettingsauditbewaardagen)
                  VALUES (@club, @sync, @dagen)", conn);
            cmd.Parameters.AddWithValue("club", club);
            cmd.Parameters.AddWithValue("sync", sync);
            cmd.Parameters.AddWithValue("dagen", dagen);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Vult de audittabel met rijen van precies de opgegeven leeftijden in dagen.</summary>
    private static async Task SeedAuditAsync(NpgsqlConnection conn, params int[] leeftijdenInDagen)
    {
        await ExecAsync(conn, "DELETE FROM public.appsettingsaudit");
        foreach (var dagen in leeftijdenInDagen)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO public.appsettingsaudit
                      (tijdstip, gewijzigddoor, veld, nieuwewaarde, clubcode)
                  VALUES (NOW() - make_interval(days => @dagen), 'tester', 'veld', 'x', 'testclub-audit')",
                conn);
            cmd.Parameters.AddWithValue("dagen", dagen);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>De leeftijden (in hele dagen) van de overgebleven rijen — stabieler dan tijdstippen.</summary>
    private static async Task<List<int>> LeeftijdenAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT ROUND(EXTRACT(EPOCH FROM (NOW() - tijdstip)) / 86400)::int FROM public.appsettingsaudit ORDER BY 1",
            conn);
        var resultaat = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) resultaat.Add(reader.GetInt32(0));
        return resultaat;
    }
}
