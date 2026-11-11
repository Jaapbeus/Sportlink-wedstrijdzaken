using System.Text.RegularExpressions;
using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.TeamResolution;
using SportlinkFunction;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Echte-database-tests voor <see cref="SqlAfzenderTeamBron"/> (#1862) op de SQL Server-tier: hoofdletter- en
/// spatieonafhankelijke adresvergelijking, hard gescoped op club, en het plan van de productiequery
/// (<c>SHOWPLAN_TEXT</c>) gebruikt de index op de persisted computed column. Tegenhanger:
/// <c>FunctionApp.Postgres.Tests/AfzenderTeamBronIntegrationTests.cs</c>.
/// <para>
/// Draait in CI tegen de verse database van de job "PostDeployment op verse database" (filter in build.yml); lokaal
/// tegen een wegwerpcontainer met <c>SQLSERVER_TEST_CONNECTION_STRING</c> (nooit een wachtwoord in een bestand of log).
/// Zonder die variabele worden de tests overgeslagen. Uitsluitend fictieve adressen (<c>@allstars-fc.test</c>).
/// </para>
/// </summary>
public sealed class SqlAfzenderTeamBronIntegrationTests : IDisposable
{
    private const string Adres = "trainer@allstars-fc.test";
    private readonly string _club = "zatb" + Guid.NewGuid().ToString("N")[..8];

    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    public SqlAfzenderTeamBronIntegrationTests() => SystemUtilities.AppSettings.ResetForTests();

    public void Dispose()
    {
        SystemUtilities.AppSettings.ResetForTests();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar))) return;
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("DELETE FROM [avg].[Teambegeleiding] WHERE [ClubCode] IN (@c, @c2)", conn);
        cmd.Parameters.AddWithValue("@c", _club);
        cmd.Parameters.AddWithValue("@c2", _club + "x");
        cmd.ExecuteNonQuery();
    }

    private async Task<SqlConnection> OpstellingAsync()
    {
        // De primaire club voor rijen zonder clubstempel (ClubScope.LegacyFilter); hier gelijk aan de testclub.
        SystemUtilities.AppSettings.SetForTests("clubCode", _club);
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ZetAsync(SqlConnection conn, string clubCode, string team, string emailadres)
    {
        using var cmd = new SqlCommand(@"
            INSERT INTO [avg].[Teambegeleiding] ([Team], [Naam], [Teamrol], [Emailadres], [ClubCode])
            VALUES (@team, N'Jan', N'Trainer', @email, @club)", conn);
        cmd.Parameters.AddWithValue("@team", team);
        cmd.Parameters.AddWithValue("@email", emailadres);
        cmd.Parameters.AddWithValue("@club", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    private SqlAfzenderTeamBron Bron() => new(() => ConnectionString);

    [SqlServerFact]
    public async Task EenTeam_ZonderOnderscheidVanHoofdlettersEnSpaties_EnAndereClubLektNietMee()
    {
        await using var conn = await OpstellingAsync();
        await ZetAsync(conn, _club, "O23-1", "  Trainer@Allstars-FC.test ");
        await ZetAsync(conn, _club, "", Adres);
        await ZetAsync(conn, _club + "x", "JO13-2", Adres);

        var teams = await Bron().ZoekTeamsVanAfzenderAsync(AfzenderTeamBronHulp.NormaliseerAdres(" TRAINER@allstars-fc.TEST"), _club);

        teams.Should().Equal("O23-1");
    }

    [SqlServerFact]
    public async Task Opzoeking_GebruiktDeIndexOpDeComputedColumn()
    {
        await using var conn = await OpstellingAsync();
        // Genoeg rijen dat een scan duurder is dan een seek; anders mag de optimizer bij een minitabel elk plan kiezen.
        using (var vul = new SqlCommand(@"
            INSERT INTO [avg].[Teambegeleiding] ([Team], [Naam], [Teamrol], [Emailadres], [ClubCode])
            SELECT TOP (500) N'JO11-1', N'Jan', N'Trainer',
                   CONCAT(N'begeleider', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'@allstars-fc.test'), @club
            FROM sys.all_objects", conn))
        {
            vul.Parameters.AddWithValue("@club", _club);
            await vul.ExecuteNonQueryAsync();
        }
        await ZetAsync(conn, _club, "O23-1", Adres);

        var plan = await ShowplanAsync(conn, AfzenderTeamBronHulp.NormaliseerAdres(Adres));

        plan.Should().Contain("IX_avg_Teambegeleiding_EmailadresGenormaliseerd",
            "de LOWER(LTRIM(RTRIM()))-vergelijking moet de index op de persisted computed column gebruiken, geen scan");
        plan.Should().Contain("Index Seek");
    }

    /// <summary>
    /// Het geschatte plan (<c>SHOWPLAN_TEXT</c>) van de productieopdracht zelf, met clubfilter en parameters, zodat een
    /// wijziging aan het predicaat hier zichtbaar wordt.
    /// </summary>
    private async Task<string> ShowplanAsync(SqlConnection conn, string adres)
    {
        using (var aan = new SqlCommand("SET SHOWPLAN_TEXT ON", conn)) await aan.ExecuteNonQueryAsync();
        var regels = new List<string>();
        try
        {
            using var cmd = SqlAfzenderTeamBron.BouwOpdracht(conn, adres, _club);
            using var reader = await cmd.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0)) regels.Add(reader.GetValue(0).ToString() ?? "");
            } while (await reader.NextResultAsync());
        }
        finally
        {
            using var uit = new SqlCommand("SET SHOWPLAN_TEXT OFF", conn);
            await uit.ExecuteNonQueryAsync();
        }
        return string.Join("\n", regels);
    }
}

/// <summary>
/// Tekstuele bewaking (#1862) zonder database: elk SQL Server-seedscript in <c>scripts/migrations/</c> dat in
/// <c>avg.Teambegeleiding</c> schrijft, zet eerst <c>QUOTED_IDENTIFIER</c> aan. De tabel heeft een persisted computed
/// column met index; sqlcmd zet die optie standaard uit en de INSERT/UPDATE faalt dan met Msg 1934.
/// </summary>
public class SeedScriptsQuotedIdentifierTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    [Fact]
    public void SchrijvendeSeedscriptsZettenQuotedIdentifierAanVoorDeEersteSchrijfactie()
    {
        var scripts = Directory.GetFiles(Path.Combine(RepoRoot(), "scripts", "migrations"), "*.sql")
            .Where(p => !p.EndsWith("-postgres.sql", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var schrijvend = 0;

        foreach (var pad in scripts)
        {
            var inhoud = File.ReadAllText(pad);
            var schrijf = Regex.Match(inhoud, @"^\s*(INSERT\s+INTO|UPDATE)\s+\[avg\]\.\[Teambegeleiding\]",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!schrijf.Success) continue;
            schrijvend++;

            // Regelanker op het statement, zodat de toelichting in commentaar niet meetelt.
            var set = Regex.Match(inhoud, @"^SET\s+QUOTED_IDENTIFIER\s+ON\s*;", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            set.Success.Should().BeTrue($"{Path.GetFileName(pad)} schrijft in avg.Teambegeleiding zonder SET QUOTED_IDENTIFIER ON (Msg 1934 onder sqlcmd)");
            set.Index.Should().BeLessThan(schrijf.Index, $"{Path.GetFileName(pad)}: de SET moet vóór de eerste schrijfactie staan");
        }

        schrijvend.Should().BePositive("zonder treffers bewaakt deze test niets");
    }
}
