using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Codex-review #1743 ronde 1 (P2): een wijziging van <c>SeasonStartMonth</c> via
/// <see cref="AdminSettingsFunction.ApplyChangesAsync"/> houdt configuratie, audit en de afgeleide
/// seizoenskalender (<c>public.clubseason</c>) per club consistent, ook bij een afgebroken en bij twee
/// overlappende wijzigingen. Vóór de fix werd de kalender pas ná de commit in een eigen transactie herbouwd
/// en werd de oude waarde voor de audit zonder lock gelezen.
/// Testdata uitsluitend synthetisch (eigen testclub); andere clubs worden niet aangeraakt.
/// </summary>
public class AdminSettingsSeizoenIntegrationTests
{
    private const string Club = "zseizoen1715";
    private static readonly DateOnly Vandaag = new(2026, 10, 13);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    [PostgresFact]
    public async Task ApplyChanges_SeasonStartMonth_WijzigtMaandAuditEnKalenderSamen()
    {
        await using var conn = await OpenSchoonAsync();

        await AdminSettingsFunction.ApplyChangesAsync(conn, Wijziging("9"), "test", Club, Vandaag);

        (await MaandAsync(conn)).Should().Be(9);
        (await AuditAsync(conn)).Should().Equal(("7", "9"));
        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31)));
    }

    [PostgresFact]
    public async Task ApplyChanges_HerbouwFaalt_DraaitMaandEnAuditTerug()
    {
        await using var conn = await OpenSchoonAsync();
        await Exec(conn, $@"
            CREATE OR REPLACE FUNCTION public.test_weiger_seizoen_1715() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
            BEGIN
                IF NEW.clubcode = '{Club}' THEN RAISE EXCEPTION 'test: herbouw afgebroken'; END IF;
                RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS weiger_seizoen_1715 ON public.clubseason;
            CREATE TRIGGER weiger_seizoen_1715 BEFORE INSERT ON public.clubseason
                FOR EACH ROW EXECUTE FUNCTION public.test_weiger_seizoen_1715();");
        try
        {
            await using var wijziger = await OpenAsync();
            var actie = () => AdminSettingsFunction.ApplyChangesAsync(wijziger, Wijziging("9"), "test", Club, Vandaag);

            await actie.Should().ThrowAsync<PostgresException>();
        }
        finally
        {
            await Exec(conn, "DROP TRIGGER IF EXISTS weiger_seizoen_1715 ON public.clubseason; " +
                "DROP FUNCTION IF EXISTS public.test_weiger_seizoen_1715();");
        }

        (await MaandAsync(conn)).Should().Be(7, "de maand gaat samen met de kalender terug");
        (await AuditAsync(conn)).Should().BeEmpty("een teruggedraaide wijziging komt niet in de audit");
        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)));
    }

    [PostgresFact]
    public async Task ApplyChanges_TweeOverlappendeWijzigingen_AuditKetenEnKalenderVolgenDeLaatste()
    {
        await using var conn = await OpenSchoonAsync();

        // Houd de rij van de club vast, start twee wijzigingen en laat ze dan tegelijk los.
        await using var houder = await OpenAsync();
        await using var vast = await houder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("SELECT 1 FROM public.appsettings WHERE clubcode = @c FOR UPDATE", houder, vast))
        {
            lockCmd.Parameters.AddWithValue("c", Club);
            await lockCmd.ExecuteScalarAsync();
        }
        var acht = WijzigAsync("8");
        var negen = WijzigAsync("9");
        await Task.Delay(300);
        await vast.CommitAsync();
        await Task.WhenAll(acht, negen);

        var maand = await MaandAsync(conn);
        maand.Should().BeOneOf(8, 9);
        var eerste = maand == 9 ? "8" : "9";
        (await AuditAsync(conn)).Should().Equal(("7", eerste), (eerste, maand.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await GrenzenAsync(conn, "2026-2027")).Van.Should().Be(new DateOnly(2026, maand, 1), "de kalender hoort bij de vastgelegde maand");

        static async Task WijzigAsync(string waarde)
        {
            await using var c = await OpenAsync();
            await AdminSettingsFunction.ApplyChangesAsync(c, Wijziging(waarde), "test", Club, Vandaag);
        }
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static Dictionary<string, string?> Wijziging(string maand)
        => new(StringComparer.OrdinalIgnoreCase) { ["SeasonStartMonth"] = maand };

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<NpgsqlConnection> OpenSchoonAsync()
    {
        var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM public.clubseason WHERE clubcode = @c;
            DELETE FROM public.appsettingsaudit WHERE clubcode = @c;
            DELETE FROM public.appsettings WHERE clubcode = @c;
            INSERT INTO public.appsettings (clubcode, syncenabled, seasonstartmonth) VALUES (@c, FALSE, 7);", conn);
        cmd.Parameters.AddWithValue("c", Club);
        await cmd.ExecuteNonQueryAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await Database.Postgres.PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync(conn, tx, Club, Vandaag);
        await tx.CommitAsync();
        return conn;
    }

    private static async Task<int> MaandAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT seasonstartmonth FROM public.appsettings WHERE clubcode = @c", conn);
        cmd.Parameters.AddWithValue("c", Club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<(string? Oud, string? Nieuw)>> AuditAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT oudewaarde, nieuwewaarde FROM public.appsettingsaudit WHERE clubcode = @c ORDER BY id", conn);
        cmd.Parameters.AddWithValue("c", Club);
        var rijen = new List<(string?, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rijen.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rijen;
    }

    private static async Task<(DateOnly Van, DateOnly Tot)> GrenzenAsync(NpgsqlConnection conn, string naam)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT datefrom, dateuntil FROM public.clubseason WHERE clubcode = @c AND name = @n", conn);
        cmd.Parameters.AddWithValue("c", Club);
        cmd.Parameters.AddWithValue("n", naam);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"seizoen {naam} moet bestaan");
        return (reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateOnly>(1));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
