using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.Avg;
using Planner.Shared.Tests.Avg;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Avg;

/// <summary>
/// Seizoensretentie van de persoonskolommen in de historietabellen (#1698) op de SQL Server-tier,
/// tegen een echte database. Dezelfde gevallen als <c>PostgresHisRetentieIntegrationTests</c>.
/// De tabellen komen uit <c>Database/his/Tables/</c> (de canonieke vorm). Opzet: wegwerpcontainer
/// met <c>Database/Script.PostDeployment1.sql</c>, <c>SQLSERVER_TEST_CONNECTION_STRING</c> gezet (zie
/// <see cref="SqlServerFactAttribute"/>). Testdata uitsluitend volgens ARCHITECTUUR.md §8.1.5.
/// </summary>
public class SqlServerHisRetentieIntegrationTests
{
    private static readonly DateOnly Grens = new(2026, 6, 30);
    private static string Cs => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task WisTotEnMet_MaaktAlleenPersoonskolommenVanAfgerondeSeizoenenLeeg_EnIsIdempotent()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await MaakHisTabellen(conn);

        await VoegMatchToe(conn, 1, "2026-06-30T14:30:00+02:00");
        await VoegMatchToe(conn, 2, "2026-07-01T14:30:00+02:00");
        await VoegMatchToe(conn, 3, "onbekend");
        await VoegMatchToe(conn, 4, "2026-02-30T10:00:00+01:00"); // onmogelijke datum: niet wissen
        await VoegMatchToe(conn, 5, "2024-02-29T10:00:00+01:00"); // geldige schrikkeldag: wel wissen
        await VoegMatchToe(conn, 6, "2025-02-29T10:00:00+01:00"); // geen schrikkeljaar: niet wissen
        await VoegDetailToe(conn, 11, new DateTime(2026, 5, 2), null);
        await VoegDetailToe(conn, 12, new DateTime(2026, 8, 2), null);
        await VoegDetailToe(conn, 13, null, new DateTime(2026, 4, 4, 12, 0, 0));
        await VoegDetailToe(conn, 14, null, null);

        var (matches, details) = await SqlServerHisRetentie.WisTotEnMetAsync(conn, Grens);

        matches.Should().Be(2);
        details.Should().Be(2);
        (await Tekst(conn, "matches", "wedstrijdcode", 1, "scheidsrechter")).Should().BeNull();
        (await Tekst(conn, "matches", "wedstrijdcode", 1, "rijders")).Should().BeNull();
        (await Tekst(conn, "matches", "wedstrijdcode", 1, "thuisteam")).Should().Be("AllStars JO11-1");
        (await Tekst(conn, "matches", "wedstrijdcode", 2, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(conn, "matches", "wedstrijdcode", 3, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(conn, "matches", "wedstrijdcode", 4, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(conn, "matches", "wedstrijdcode", 5, "scheidsrechter")).Should().BeNull();
        (await Tekst(conn, "matches", "wedstrijdcode", 6, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(conn, "matchdetails", "InternCode", 11, "ThuisTeamEmail")).Should().BeNull();
        (await Tekst(conn, "matchdetails", "InternCode", 11, "Opmerkingen")).Should().BeNull();
        (await Tekst(conn, "matchdetails", "InternCode", 11, "ThuisTeam")).Should().Be("AllStars JO11-1");
        (await Tekst(conn, "matchdetails", "InternCode", 12, "Rijder")).Should().Be("Jan de Vries");
        (await Tekst(conn, "matchdetails", "InternCode", 13, "Rijder")).Should().BeNull();
        (await Tekst(conn, "matchdetails", "InternCode", 14, "Rijder")).Should().Be("Jan de Vries");

        (await SqlServerHisRetentie.WisTotEnMetAsync(conn, Grens)).Should().Be((0, 0));
    }

    [SqlServerFact]
    public async Task WisTotEnMet_DatuminvoerVolgtDeGedeeldeTabel_GelijkAanPostgres()
    {
        // Codex-review ronde 2: welke waarden als datum tellen, ligt vast in één tabel die ook de
        // Postgres-suite en de unit-test draaien. Waarden die onder de standaard-collation gelijk
        // zijn aan een geldige datum (fullwidth, superscript) staan ernaast en blijven staan.
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await MaakHisTabellen(conn);
        var gevallen = HisWedstrijddatumGevallen.Alle;
        for (var i = 0; i < gevallen.Count; i++)
            await VoegMatchToe(conn, 100 + i, gevallen[i].Waarde);

        var (matches, _) = await SqlServerHisRetentie.WisTotEnMetAsync(conn, HisWedstrijddatumGevallen.Grens);

        for (var i = 0; i < gevallen.Count; i++)
            (await Tekst(conn, "matches", "wedstrijdcode", 100 + i, "scheidsrechter")).Should().Be(
                gevallen[i].Wissen ? null : "Jan de Vries", $"geval {i}: {gevallen[i].Reden}");
        matches.Should().Be(gevallen.Count(g => g.Wissen));
        (await SqlServerHisRetentie.WisTotEnMetAsync(conn, HisWedstrijddatumGevallen.Grens)).Should().Be((0, 0));
    }

    [SqlServerFact]
    public async Task WisTotEnMet_ZonderHistorietabellen_DoetNiets()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await Exec(conn, "DROP TABLE IF EXISTS [his].[matches]; DROP TABLE IF EXISTS [his].[matchdetails];");

        (await SqlServerHisRetentie.WisTotEnMetAsync(conn, Grens)).Should().Be((0, 0));
    }

    [SqlServerFact]
    public async Task LeesSeizoenseinden_LeestDeSeizoenstabel()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await using var telling = new SqlCommand("SELECT COUNT(*) FROM [dbo].[Season] WHERE [DateUntil] IS NOT NULL", conn);
        var verwacht = (int)(await telling.ExecuteScalarAsync())!;

        (await SqlServerHisRetentie.LeesSeizoenseindenAsync(conn)).Should().HaveCount(verwacht);
    }

    [SqlServerFact]
    public async Task AlsOpslag_OpentEenEigenVerbinding_VoorLezenEnWissen()
    {
        // Zo roept de dagelijkse AVG-retentierun (CleanupFeedback) de tier aan: alleen met de
        // connectiestring, via de poort IHisRetentieOpslag.
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await MaakHisTabellen(conn);
        await VoegMatchToe(conn, 1, "2026-06-30T14:30:00+02:00");
        await VoegDetailToe(conn, 11, new DateTime(2026, 5, 2), null);
        await using var telling = new SqlCommand("SELECT COUNT(*) FROM [dbo].[Season] WHERE [DateUntil] IS NOT NULL", conn);
        var seizoenen = (int)(await telling.ExecuteScalarAsync())!;

        IHisRetentieOpslag opslag = new SqlServerHisRetentie(Cs);

        (await opslag.LeesSeizoenseindenAsync()).Should().HaveCount(seizoenen);
        (await opslag.WisTotEnMetAsync(Grens)).Should().Be((1, 1));
        (await Tekst(conn, "matches", "wedstrijdcode", 1, "scheidsrechter")).Should().BeNull();
        (await opslag.WisTotEnMetAsync(Grens)).Should().Be((0, 0));
    }

    private static async Task MaakHisTabellen(SqlConnection conn)
    {
        await Exec(conn, "DROP TABLE IF EXISTS [his].[matches]; DROP TABLE IF EXISTS [his].[matchdetails];");
        var map = Path.Combine(RepoRoot(), "Database", "his", "Tables");
        foreach (var bestand in new[] { "Matches.sql", "MatchDetails.sql" })
            await Exec(conn, (await File.ReadAllTextAsync(Path.Combine(map, bestand))).TrimStart('﻿'));
    }

    private static string RepoRoot()
    {
        var map = new DirectoryInfo(AppContext.BaseDirectory);
        while (map is not null && !File.Exists(Path.Combine(map.FullName, "AGENTS.md")))
            map = map.Parent;
        return map?.FullName ?? throw new InvalidOperationException("Repositorywortel niet gevonden.");
    }

    private static async Task VoegMatchToe(SqlConnection c, long code, string? datum)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO [his].[matches] ([bk_matches], [wedstrijdcode], [wedstrijddatum], [thuisteam], [scheidsrechter], " +
            "[scheidsrechters], [rijders], [ClubCode]) " +
            "VALUES (CAST(@code AS NVARCHAR(100)), @code, @datum, N'AllStars JO11-1', N'Jan de Vries', N'Jan de Vries', N'Jan de Vries', N'ALLSTARS')", c);
        cmd.Parameters.AddWithValue("@code", code);
        cmd.Parameters.Add(new SqlParameter("@datum", System.Data.SqlDbType.NVarChar, 50) { Value = (object?)datum ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task VoegDetailToe(SqlConnection c, int interncode, DateTime? datum, DateTime? datumtijd)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO [his].[matchdetails] ([bk_InternCode], [WedstrijdCode], [InternCode], [MatchDate], [MatchDateTime], " +
            "[ThuisTeam], [Rijder], [Opmerkingen], [ThuisTeamEmail], [ClubCode]) " +
            "VALUES (@interncode, 1, @interncode, @datum, @datumtijd, N'AllStars JO11-1', N'Jan de Vries', N'Jan de Vries rijdt', " +
            "N'trainer@voorbeeld.nl', N'ALLSTARS')", c);
        cmd.Parameters.AddWithValue("@interncode", interncode);
        cmd.Parameters.Add(new SqlParameter("@datum", System.Data.SqlDbType.Date) { Value = (object?)datum ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@datumtijd", System.Data.SqlDbType.DateTime) { Value = (object?)datumtijd ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Tekst(SqlConnection c, string tabel, string sleutel, long waarde, string kolom)
    {
        await using var cmd = new SqlCommand($"SELECT [{kolom}] FROM [his].[{tabel}] WHERE [{sleutel}] = @w", c);
        cmd.Parameters.AddWithValue("@w", waarde);
        var resultaat = await cmd.ExecuteScalarAsync();
        return resultaat is DBNull or null ? null : (string)resultaat;
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
