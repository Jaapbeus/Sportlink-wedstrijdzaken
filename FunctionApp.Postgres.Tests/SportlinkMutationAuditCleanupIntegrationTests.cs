using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync"/>
/// vast (#1114) — de AVG-bewaartermijn op <c>public.sportlinkmutationaudit</c>. Zelfde opzet en
/// dezelfde redenering als <see cref="AppSettingsAuditCleanupIntegrationTests"/>: elke test
/// controleert wát verdwijnt én wát blijft staan, want een procedure die álles verwijdert zou een
/// "er is iets weg"-assertie groen laten en tegelijk een audittrail vernietigen die er nog had
/// moeten zijn.
/// </summary>
public class SportlinkMutationAuditCleanupIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task PrimaireClubIsLeidend_DemoclubWaardeWordtGenegeerd()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("ALLSTARS", 9999, false), ("zprimary", 10, true));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 5 },
            "de primaire club (10 dagen) is leidend, niet de democlub");
    }

    [PostgresFact]
    public async Task AlleenDemoclubAanwezig_VangnetGebruiktDiensWaarde()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("ALLSTARS", 10, false));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 5 });
    }

    [PostgresFact]
    public async Task OnzinnigeWaarde_ValtTerugOpDefault365_EnVerwijdertNietAlles()
    {
        await using var conn = await OpenAsync();
        // 0 letterlijk toepassen zou alles verwijderen; de terugval op 365 moet 300 laten staan
        // en 400 wél verwijderen.
        await ZetClubsAsync(conn, ("zprimary", 0, true));
        await SeedAuditAsync(conn, 300, 400);

        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 300 },
            "een waarde <= 0 valt terug op 365 dagen: 300 blijft, 400 verdwijnt");
    }

    [PostgresFact]
    public async Task GeenEnkeleAppSettingsRij_GebruiktDefaultZonderFout()
    {
        await using var conn = await OpenAsync();
        await VerwijderClubsAsync(conn);
        await SeedAuditAsync(conn, 300, 400);

        var act = async () => await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

        await act.Should().NotThrowAsync();
        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 300 });
    }

    /// <summary>Een rij die nog op <c>Pending</c> staat is per definitie jong (seconden), dus de
    /// leeftijdsgrens raakt hem niet — maar dat moet wel zo blijven als iemand de grens ooit op
    /// uren zou zetten. Bewaakt hier expliciet met een verse Pending-rij.</summary>
    [PostgresFact]
    public async Task VersePendingRij_BlijftStaan()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("zprimary", 1, true));
        await SeedAuditAsync(conn, 0, 5);

        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

        (await LeeftijdenAsync(conn)).Should().BeEquivalentTo(new[] { 0 });
    }

    [PostgresFact]
    public async Task TweedeAanroep_IsIdempotent()
    {
        await using var conn = await OpenAsync();
        await ZetClubsAsync(conn, ("zprimary", 10, true));
        await SeedAuditAsync(conn, 5, 40);

        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);
        var naEerste = await LeeftijdenAsync(conn);
        await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);

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
                @"INSERT INTO public.appsettings (clubcode, syncenabled, sportlinkmutationauditbewaardagen)
                  VALUES (@club, @sync, @dagen)", conn);
            cmd.Parameters.AddWithValue("club", club);
            cmd.Parameters.AddWithValue("sync", sync);
            cmd.Parameters.AddWithValue("dagen", dagen);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Vult de audittabel met rijen van precies de opgegeven leeftijden in dagen. De
    /// actor is een fictieve testwaarde, geen persoonsgegeven.</summary>
    private static async Task SeedAuditAsync(NpgsqlConnection conn, params int[] leeftijdenInDagen)
    {
        await ExecAsync(conn, "DELETE FROM public.sportlinkmutationaudit");
        foreach (var dagen in leeftijdenInDagen)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO public.sportlinkmutationaudit
                      (clubcode, functionelerol, triggerddoor, publicmatchid, actie, resultaat, tijdstip)
                  VALUES ('testclub-audit', 'Wedstrijdzaken', 'tester', 'PM-TEST', 'Test',
                          CASE WHEN @dagen = 0 THEN 'Pending' ELSE 'Success' END,
                          NOW() - make_interval(days => @dagen))",
                conn);
            cmd.Parameters.AddWithValue("dagen", dagen);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<int>> LeeftijdenAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT ROUND(EXTRACT(EPOCH FROM (NOW() - tijdstip)) / 86400)::int FROM public.sportlinkmutationaudit ORDER BY 1",
            conn);
        var resultaat = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) resultaat.Add(reader.GetInt32(0));
        return resultaat;
    }
}
