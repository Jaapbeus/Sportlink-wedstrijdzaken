using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using FunctionApp.Tests.Feedback;
using SportlinkFunction;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// SQL Server counterparts to the Postgres KNVB repository contract tests.
/// Requires a disposable database prepared from Database/Script.PostDeployment1.sql.
/// </summary>
public class KnvbKalenderRepositoryIntegrationTests : IAsyncLifetime
{
    private bool _testDatabaseVerified;

    private const string Seizoen = "2099/2100";
    private const string AnderSeizoen = "2098/2099";
    private const string Regio = "West";
    private const string TestDatabaseCollation = "Latin1_General_100_CI_AS";

    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    public async Task InitializeAsync()
    {
        await ValideerTestDatabaseAsync();
        _testDatabaseVerified = true;
        await OpruimenAsync();
    }

    public Task DisposeAsync() => _testDatabaseVerified ? OpruimenAsync() : Task.CompletedTask;

    private static async Task ValideerTestDatabaseAsync()
    {
        var fixtureDoel = await LeesDatabaseDoelAsync(ConnectionString);
        var repositoryDoel = await LeesDatabaseDoelAsync(SystemUtilities.DatabaseConfig.ConnectionString);

        if (!string.Equals(fixtureDoel.Server, repositoryDoel.Server, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fixtureDoel.Database, repositoryDoel.Database, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SqlServerFactAttribute.EnvVar} and SqlConnectionString must target the same SQL Server instance and catalog.");
        }

        if (!string.Equals(fixtureDoel.Collation, TestDatabaseCollation, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The disposable SQL Server contract-test catalog must use {TestDatabaseCollation}.");
        }
    }

    private static async Task<(string Server, string Database, string Collation)> LeesDatabaseDoelAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')),
                   DB_NAME(),
                   CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new InvalidOperationException("Could not verify the SQL Server test database identity and collation.");

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task OpruimenAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] IN (@seizoen, @anderSeizoen)", conn);
        cmd.Parameters.AddWithValue("@seizoen", Seizoen);
        cmd.Parameters.AddWithValue("@anderSeizoen", AnderSeizoen);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ZetDagAsync(string regio, DateOnly datum, string dagtype, string seizoen = Seizoen)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[KnvbKalenderDag]
                ([Seizoen], [Regio], [Datum], [DagType], [HeeftSenioren], [HeeftJeugd], [HeeftMeiden])
            VALUES (@seizoen, @regio, @datum, @dagtype, 1, 1, 0)
            """, conn);
        cmd.Parameters.AddWithValue("@seizoen", seizoen);
        cmd.Parameters.AddWithValue("@regio", regio);
        cmd.Parameters.AddWithValue("@datum", datum.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@dagtype", dagtype);
        await cmd.ExecuteNonQueryAsync();
    }

    private static readonly DateOnly Zaterdag1 = new(2099, 9, 5);
    private static readonly DateOnly Zaterdag2 = new(2099, 9, 12);
    private static readonly DateOnly Zaterdag3 = new(2099, 9, 19);
    private static readonly DateOnly ZaterdagVoor = new(2099, 8, 29);
    private static readonly DateOnly ZaterdagNa = new(2099, 9, 26);

    [SqlServerFact]
    public async Task FiltertOpDagtype_AlleenCompetitieBekerInhaal()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Vrij");
        await ZetDagAsync(Regio, Zaterdag3, "NC");
        await ZetDagAsync("Oost", Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag1, "Competitie", AnderSeizoen);

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            Regio, Seizoen, Zaterdag1, Zaterdag3, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().ContainSingle().Which.Should().Be(Zaterdag1);
    }

    [SqlServerFact]
    public async Task BehoudtSqlServerDagtypeVergelijking()
    {
        // The canonical SQL Server CHECK and IN query accept case and trailing spaces.
        await ZetDagAsync(Regio, Zaterdag1, "competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Beker ");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            Regio, Seizoen, Zaterdag1, Zaterdag2, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().Equal(Zaterdag1, Zaterdag2);
    }

    [SqlServerFact]
    public async Task SluitReedsBezetteDatumUit()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Beker");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            Regio, Seizoen, Zaterdag1, Zaterdag2,
            new HashSet<DateOnly> { Zaterdag1 }, maxAantal: 5);

        resultaat.Should().ContainSingle().Which.Should().Be(Zaterdag2);
    }

    [SqlServerFact]
    public async Task BegrensdMaxAantalInOplopendeVolgorde()
    {
        await ZetDagAsync(Regio, ZaterdagVoor, "Competitie");
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Competitie");
        await ZetDagAsync(Regio, Zaterdag3, "Competitie");
        await ZetDagAsync(Regio, ZaterdagNa, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            Regio, Seizoen, Zaterdag1, Zaterdag3, new HashSet<DateOnly>(), maxAantal: 2);

        resultaat.Should().Equal(Zaterdag1, Zaterdag2);
    }

    [SqlServerFact]
    public async Task OnbekendeRegio_GeeftLegeLijst()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            "OnbekendeRegio", Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task LegeRegioOfSeizoen_GeeftLegeLijstZonderQuery()
    {
        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            "", Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task MaxAantalNulOfNegatief_GeeftLegeLijst()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            Regio, Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 0);

        resultaat.Should().BeEmpty();
    }
}
