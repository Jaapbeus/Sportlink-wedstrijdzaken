using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Planner;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace FunctionApp.Postgres.Tests.Planner;

/// <summary>
/// #1915: de Postgres-opslag van de veldplanner-blokken (migratie 050) tegen een echte instantie. Bewaakt de
/// tabelvorm (sleutel, RLS), de synchronisatie op sleutel en hash, en dat een bekend wedstrijdnummer nooit met "onbekend"
/// wordt overschreven. Testdata uitsluitend synthetisch (eigen testclubs).
/// </summary>
public class PostgresVeldplannerBlokStoreIntegrationTests
{
    private const string Club = "tc-vpb-a";
    private const string AndereClub = "tc-vpb-b";
    private static readonly DateOnly Dag = new(2030, 1, 5);
    private static readonly DateOnly Vandaag = new(2030, 1, 4);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private static SportlinkVeldplannerBlok Blok(string id, string start = "10:00", string veld = "veld 1 A", string label = "A - B")
        => new(label, veld, start, 0.5m, 75, id);

    private static async Task SchoonAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM public.sportlinkveldplannerblok WHERE clubcode IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("a", Club);
        cmd.Parameters.AddWithValue("b", AndereClub);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> TelAsync(string clubCode)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM public.sportlinkveldplannerblok WHERE clubcode = @c", conn);
        cmd.Parameters.AddWithValue("c", clubCode);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task<VeldplannerBlokSynchronisatieResultaat> Sync(
        PostgresVeldplannerBlokStore store, Dictionary<string, long?> nummers, string club, params SportlinkVeldplannerBlok[] blokken)
        => VeldplannerBlokSynchronisatie.SynchroniseerAsync(store, id => Task.FromResult(nummers.TryGetValue(id, out var n) ? n : null),
            club, Dag, blokken, Vandaag);

    [PostgresFact]
    public async Task Tabel_HeeftRowLevelSecurity_EnEenSleutelPerClubEnWedstrijd()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT c.relrowsecurity,
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                      FROM pg_index i
                      CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                     WHERE i.indrelid = c.oid AND i.indisprimary)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = 'sportlinkveldplannerblok'
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue("migratie 050 maakt de tabel");
        reader.GetBoolean(0).Should().BeTrue("RLS staat aan zonder policies (#1198)");
        reader.GetString(1).Should().Be("clubcode,publicmatchid");
    }

    [PostgresFact]
    public async Task Synchronisatie_InsertWijzigVerwijder_EnLaatAndereClubEnDagStaan()
    {
        await SchoonAsync();
        var store = new PostgresVeldplannerBlokStore(ConnectionString);
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

        var stand = await store.LeesDagAsync(Club, Dag);
        stand["M1"].ExternalMatchId.Should().Be(101, "het bewaarde nummer overleeft een wijziging van het blok");
        await SchoonAsync();
    }

    [PostgresFact]
    public async Task OnbekendNummer_OverschrijftNooitEenBekendNummer()
    {
        await SchoonAsync();
        var store = new PostgresVeldplannerBlokStore(ConnectionString);
        await Sync(store, new() { ["M1"] = 101 }, Club, Blok("M1"));

        // Een directe schrijfactie zonder nummer (zoals een mislukte opvraag zou geven) mag het nummer niet wissen.
        var blok = Blok("M1", "10:45");
        await store.SchrijfAsync(Club, Dag,
            new[] { new VeldplannerBlokRij(Club, "M1", Dag, blok.Label, blok.Veld, blok.StartTijd, blok.Veldafmeting, blok.DuurMinuten, null, VeldplannerBlokHash.Bereken(blok)) },
            Array.Empty<string>(), Vandaag.AddDays(-14));

        (await store.LeesDagAsync(Club, Dag))["M1"].ExternalMatchId.Should().Be(101);
        await SchoonAsync();
    }

    [PostgresFact]
    public async Task OudeRijen_WordenOpgeruimd_ZonderDeRestVanDeClubTeRaken()
    {
        await SchoonAsync();
        var store = new PostgresVeldplannerBlokStore(ConnectionString);
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
