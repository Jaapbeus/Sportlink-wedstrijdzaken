using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Seizoenen per club op de SQL Server-tier (#1715) tegen een echte database, met de gevallen uit
/// Codex-review #1743 ronde 1: doorrol van januari-/februariseizoenen vóór de jaarwisseling
/// (<c>dbo.sp_UpdateClubSeasonTable</c>), een afgebroken en twee overlappende wijzigingen van
/// <c>SeasonStartMonth</c> (<see cref="AdminSettingsFunction.PasWijzigingenToeAsync"/>), en de overgang:
/// de leesquery's van de vorige codeversie op het clubneutrale <c>dbo.Season</c> blijven werken.
/// Opzet: database met <c>Database/Script.PostDeployment1.sql</c>, <c>SQLSERVER_TEST_CONNECTION_STRING</c>
/// gezet (CI-job "PostDeployment op verse database"). Testdata synthetisch, eigen testclub.
/// </summary>
public class SqlServerSeizoenIntegrationTests
{
    private const string Club = "ZSEIZOEN1715";
    private static readonly DateOnly Vandaag = new(2026, 10, 13);
    private static string Cs => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task Procedure_JanuariOfFebruari_RoltVoorDeJaarwisselingDoor_EnJuliBlijftGelijk()
    {
        foreach (var (maand, voor, op) in new[]
                 {
                     (1, new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1)),
                     (2, new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 1)),
                 })
        {
            await using var conn = await OpenSchoonAsync(maand, metSeizoenen: false);
            await ProcedureAsync(conn, voor);
            (await NamenAsync(conn)).Should().NotContain("2027-2028", $"startmaand {maand}: één dag vóór de drempel");

            await ProcedureAsync(conn, op);
            (await NamenAsync(conn)).Should().Contain("2027-2028", $"startmaand {maand}: op de drempel");
            (await GrenzenAsync(conn, "2027-2028")).Van.Should().Be(new DateOnly(2027, maand, 1));
        }

        await using var juli = await OpenSchoonAsync(7, metSeizoenen: false);
        await ProcedureAsync(juli, new DateOnly(2026, 4, 30));
        (await NamenAsync(juli)).Should().Equal("2024-2025", "2025-2026");
        await ProcedureAsync(juli, new DateOnly(2026, 5, 1));
        (await NamenAsync(juli)).Should().Equal("2024-2025", "2025-2026", "2026-2027");
        (await GrenzenAsync(juli, "2026-2027")).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)));
    }

    [SqlServerFact]
    public async Task PasWijzigingenToe_SeasonStartMonth_WijzigtMaandAuditEnKalenderSamen()
    {
        await using var conn = await OpenSchoonAsync(7);

        await AdminSettingsFunction.PasWijzigingenToeAsync(conn, Wijziging("9"), Club, "test", Vandaag);

        (await MaandAsync(conn)).Should().Be(9);
        (await AuditAsync(conn)).Should().Equal(("7", "9"));
        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31)));
    }

    [SqlServerFact]
    public async Task PasWijzigingenToe_HerbouwFaalt_DraaitMaandEnAuditTerug()
    {
        await using var conn = await OpenSchoonAsync(7);
        await Exec(conn, $@"
            CREATE OR ALTER TRIGGER [dbo].[TR_Test_WeigerSeizoen1715] ON [dbo].[ClubSeason] AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE [ClubCode] = '{Club}')
                    THROW 50001, 'test: herbouw afgebroken', 1;
            END");
        try
        {
            await using var wijziger = await OpenAsync();
            var actie = () => AdminSettingsFunction.PasWijzigingenToeAsync(wijziger, Wijziging("9"), Club, "test", Vandaag);

            await actie.Should().ThrowAsync<SqlException>();
        }
        finally
        {
            await Exec(conn, "DROP TRIGGER IF EXISTS [dbo].[TR_Test_WeigerSeizoen1715];");
        }

        (await MaandAsync(conn)).Should().Be(7, "de maand gaat samen met de kalender terug");
        (await AuditAsync(conn)).Should().BeEmpty("een teruggedraaide wijziging komt niet in de audit");
        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)));
    }

    [SqlServerFact]
    public async Task PasWijzigingenToe_TweeOverlappendeWijzigingen_AuditKetenEnKalenderVolgenDeLaatste()
    {
        await using var conn = await OpenSchoonAsync(7);

        // Houd de rij van de club vast, start twee wijzigingen en laat ze dan tegelijk los.
        await using var houder = await OpenAsync();
        await using var vast = (SqlTransaction)await houder.BeginTransactionAsync();
        await using (var lockCmd = new SqlCommand(
            "SELECT 1 FROM [dbo].[AppSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [ClubCode] = @c", houder, vast))
        {
            lockCmd.Parameters.AddWithValue("@c", Club);
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
            await AdminSettingsFunction.PasWijzigingenToeAsync(c, Wijziging(waarde), Club, "test", Vandaag);
        }
    }

    /// <summary>
    /// P1, overgang: de vorige codeversie leest tijdens de deploy nog uit <c>dbo.Season</c>. Haar query's
    /// (letterlijk uit origin/main: <c>SystemUtilities.SeasonHelper</c>, <c>PlannerSettingsRepository</c>,
    /// <c>SqlServerHisRetentie</c>) moeten na het PostDeployment-script blijven werken; dbo.Season houdt zijn vorm.
    /// </summary>
    [SqlServerFact]
    public async Task Overgang_LeesqueryVanDeVorigeCode_WerktOpDbo_Season()
    {
        await using var conn = await OpenAsync();
        foreach (var vorigeQuery in new[]
                 {
                     "SELECT MAX(DateUntil) FROM [dbo].[Season]",
                     "SELECT MIN(DateFrom) FROM [dbo].[Season] WHERE YEAR(DateFrom) = 2026",
                     "SELECT TOP 1 [DateFrom], [DateUntil] FROM [dbo].[Season] WHERE [DateUntil] >= '2026-10-13' ORDER BY [DateFrom] ASC",
                     "SELECT [DateUntil] FROM [dbo].[Season] WHERE [DateUntil] IS NOT NULL",
                 })
        {
            await using var cmd = new SqlCommand(vorigeQuery, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }

        await using var kolom = new SqlCommand("SELECT COL_LENGTH('dbo.Season', 'ClubCode')", conn);
        (await kolom.ExecuteScalarAsync()).Should().Be(DBNull.Value, "dbo.Season blijft clubneutraal tot de contract-stap");
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static Dictionary<string, string?> Wijziging(string maand)
        => new(StringComparer.OrdinalIgnoreCase) { ["SeasonStartMonth"] = maand };

    private static async Task ProcedureAsync(SqlConnection conn, DateOnly vandaag)
    {
        await using var cmd = new SqlCommand("EXEC [dbo].[sp_UpdateClubSeasonTable] @ClubCode = @c, @Vandaag = @v", conn);
        cmd.Parameters.AddWithValue("@c", Club);
        cmd.Parameters.Add(new SqlParameter("@v", System.Data.SqlDbType.Date) { Value = vandaag.ToDateTime(TimeOnly.MinValue) });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<SqlConnection> OpenSchoonAsync(int maand, bool metSeizoenen = true)
    {
        var conn = await OpenAsync();
        await using (var cmd = new SqlCommand(@"
            DELETE FROM [dbo].[ClubSeason] WHERE [ClubCode] = @c;
            DELETE FROM [dbo].[AppSettingsAudit] WHERE [ClubCode] = @c;
            DELETE FROM [dbo].[AppSettings] WHERE [ClubCode] = @c;
            INSERT INTO [dbo].[AppSettings] ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [SyncEnabled])
            VALUES (N'Seizoen Test', @c, N'https://sportlink.voorbeeld.test', N'test-client', @m, 0);", conn))
        {
            cmd.Parameters.AddWithValue("@c", Club);
            cmd.Parameters.AddWithValue("@m", maand);
            await cmd.ExecuteNonQueryAsync();
        }
        if (metSeizoenen)
            await ProcedureAsync(conn, Vandaag);
        return conn;
    }

    private static async Task<int> MaandAsync(SqlConnection conn)
    {
        await using var cmd = new SqlCommand("SELECT [SeasonStartMonth] FROM [dbo].[AppSettings] WHERE [ClubCode] = @c", conn);
        cmd.Parameters.AddWithValue("@c", Club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<(string? Oud, string? Nieuw)>> AuditAsync(SqlConnection conn)
    {
        await using var cmd = new SqlCommand(
            "SELECT [OudeWaarde], [NieuweWaarde] FROM [dbo].[AppSettingsAudit] WHERE [ClubCode] = @c ORDER BY [Id]", conn);
        cmd.Parameters.AddWithValue("@c", Club);
        var rijen = new List<(string?, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rijen.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rijen;
    }

    private static async Task<List<string>> NamenAsync(SqlConnection conn)
    {
        await using var cmd = new SqlCommand("SELECT [Name] FROM [dbo].[ClubSeason] WHERE [ClubCode] = @c ORDER BY [Name]", conn);
        cmd.Parameters.AddWithValue("@c", Club);
        var namen = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            namen.Add(reader.GetString(0).Trim());
        return namen;
    }

    private static async Task<(DateOnly Van, DateOnly Tot)> GrenzenAsync(SqlConnection conn, string naam)
    {
        await using var cmd = new SqlCommand(
            "SELECT [DateFrom], [DateUntil] FROM [dbo].[ClubSeason] WHERE [ClubCode] = @c AND [Name] = @n", conn);
        cmd.Parameters.AddWithValue("@c", Club);
        cmd.Parameters.AddWithValue("@n", naam);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"seizoen {naam} moet bestaan");
        return (DateOnly.FromDateTime(reader.GetDateTime(0)), DateOnly.FromDateTime(reader.GetDateTime(1)));
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
