using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1915: de SQL Server-opslag van de veldplanner-blokken tegen een echte instantie — tegenhanger van
/// <c>PostgresVeldplannerBlokStoreIntegrationTests</c>. Draait alleen met <c>SQLSERVER_TEST_CONNECTION_STRING</c> (lokaal
/// tegen een wegwerpcontainer met het schema uit <c>Database/Script.PostDeployment1.sql</c>). Testdata synthetisch.
/// </summary>
public class SqlServerVeldplannerBlokStoreIntegrationTests
{
    private const string Club = "tc-vpb-a";
    private const string AndereClub = "tc-vpb-b";
    private static readonly DateOnly Dag = new(2030, 1, 5);
    private static readonly DateOnly Vandaag = new(2030, 1, 4);

    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    private static SportlinkVeldplannerBlok Blok(string id, string start = "10:00", string veld = "veld 1 A", string label = "A - B")
        => new(label, veld, start, 0.5m, 75, id);

    private static async Task SchoonAsync()
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("DELETE FROM [dbo].[SportlinkVeldplannerBlok] WHERE [ClubCode] IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("@a", Club);
        cmd.Parameters.AddWithValue("@b", AndereClub);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> TelAsync(string clubCode)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[SportlinkVeldplannerBlok] WHERE [ClubCode] = @c", conn);
        cmd.Parameters.AddWithValue("@c", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task<VeldplannerBlokSynchronisatieResultaat> Sync(
        SqlServerVeldplannerBlokStore store, Dictionary<string, long?> nummers, string club, params SportlinkVeldplannerBlok[] blokken)
        => VeldplannerBlokSynchronisatie.SynchroniseerAsync(store, id => Task.FromResult(nummers.TryGetValue(id, out var n) ? n : null),
            club, Dag, blokken, Vandaag);

    [SqlServerFact]
    public async Task Tabel_HeeftEenSleutelPerClubEnWedstrijd()
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("""
            SELECT STRING_AGG(c.[name], ',') WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID('dbo.SportlinkVeldplannerBlok') AND i.[is_primary_key] = 1
            """, conn);

        (await cmd.ExecuteScalarAsync()).Should().Be("ClubCode,PublicMatchId");
    }

    [SqlServerFact]
    public async Task Synchronisatie_InsertWijzigVerwijder_EnLaatAndereClubEnDagStaan()
    {
        await SchoonAsync();
        var store = new SqlServerVeldplannerBlokStore(ConnectionString);
        await Sync(store, new() { ["M1"] = 101, ["M2"] = 102 }, AndereClub, Blok("M1"));

        var eerste = await Sync(store, new() { ["M1"] = 101, ["M2"] = 102 }, Club, Blok("M1"), Blok("M2", "11:00"));
        eerste.Toegevoegd.Should().Be(2);
        (await TelAsync(Club)).Should().Be(2);

        var herhaling = await Sync(store, new(), Club, Blok("M1"), Blok("M2", "11:00"));
        (herhaling.Ongewijzigd, herhaling.Toegevoegd, herhaling.Gewijzigd).Should().Be((2, 0, 0));
        herhaling.Blokken.Select(b => b.ExternalMatchId).Should().Equal(101, 102);

        var wijziging = await Sync(store, new(), Club, Blok("M1", "10:30"));
        (wijziging.Gewijzigd, wijziging.Verwijderd).Should().Be((1, 1));
        (await TelAsync(Club)).Should().Be(1);
        (await TelAsync(AndereClub)).Should().Be(1, "een andere club blijft onaangeroerd");

        (await store.LeesDagAsync(Club, Dag))["M1"].ExternalMatchId.Should().Be(101, "het bewaarde nummer overleeft een wijziging van het blok");
        await SchoonAsync();
    }

    [SqlServerFact]
    public async Task OnbekendNummer_OverschrijftNooitEenBekendNummer()
    {
        await SchoonAsync();
        var store = new SqlServerVeldplannerBlokStore(ConnectionString);
        await Sync(store, new() { ["M1"] = 101 }, Club, Blok("M1"));

        var blok = Blok("M1", "10:45");
        await store.SchrijfAsync(Club, Dag,
            new[] { new VeldplannerBlokRij(Club, "M1", Dag, blok.Label, blok.Veld, blok.StartTijd, blok.Veldafmeting, blok.DuurMinuten, null, VeldplannerBlokHash.Bereken(blok)) },
            Array.Empty<string>(), Vandaag.AddDays(-14));

        (await store.LeesDagAsync(Club, Dag))["M1"].ExternalMatchId.Should().Be(101);
        await SchoonAsync();
    }

    [SqlServerFact]
    public async Task OudeRijen_WordenOpgeruimd_ZonderDeRestVanDeClubTeRaken()
    {
        await SchoonAsync();
        var store = new SqlServerVeldplannerBlokStore(ConnectionString);
        var oud = Vandaag.AddDays(-30);
        await store.SchrijfAsync(Club, oud,
            new[] { new VeldplannerBlokRij(Club, "OUD", oud, "A - B", "veld 1", "10:00", 1m, 75, 1, "h") },
            Array.Empty<string>(), Vandaag.AddDays(-14));
        (await TelAsync(Club)).Should().Be(0, "de schrijfactie ruimt rijen ouder dan de bewaardrempel direct op");

        await Sync(store, new(), Club, Blok("M1"));
        (await TelAsync(Club)).Should().Be(1);
        await SchoonAsync();
    }
}
