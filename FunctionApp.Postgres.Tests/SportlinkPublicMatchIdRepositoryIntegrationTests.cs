using Database.Postgres;
using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt <see cref="SportlinkPublicMatchIdRepository"/>'s cache- en lookup-gedrag vast (#991,
/// epic #986). Draait tegen een echte Postgres-instantie (<see cref="PostgresFactAttribute"/>) —
/// zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.
/// </summary>
public class SportlinkPublicMatchIdRepositoryIntegrationTests
{
    private const string Club = "testclub-sportlink";
    // 9600001-9600005: his.matches.wedstrijdcode is GEEN clubcode-gescoped sleutel (UQ_matches_bk is
    // globaal, businessKey=["wedstrijdcode"] — zie Database.Postgres/KnownEntities.cs), dus dit
    // getal moet uniek zijn over ALLE testklassen in deze suite, niet alleen binnen deze klasse.
    // 9100001/9200001-9200004/9300001-9300004/9400001-9400009/9500001-9500006/9999999 zijn al in
    // gebruik door andere testklassen; 9600002-9600005 gereserveerd voor de #1017-warmuptests
    // hieronder, 9600006-9600007 voor de #1111-reverse-lookuptests.
    private const long Wedstrijdcode = 9600001;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    private static async Task<NpgsqlConnection> OpstellingAsync(long? wedstrijdnummer = 3403, string? kaledatum = "2026-09-05")
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, $"DELETE FROM his.matches WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.sportlinkpublicmatchidcache WHERE clubcode = '{Club}'");

        await using (var match = new NpgsqlCommand(
            @"INSERT INTO his.matches (wedstrijdcode, wedstrijdnummer, kaledatum, clubcode, mta_inserted, mta_modified)
              VALUES (@code, @nummer, @datum, @club, NOW(), NOW())", conn))
        {
            match.Parameters.AddWithValue("code", Wedstrijdcode);
            match.Parameters.AddWithValue("nummer", (object?)wedstrijdnummer ?? DBNull.Value);
            match.Parameters.AddWithValue("datum", (object?)kaledatum ?? DBNull.Value);
            match.Parameters.AddWithValue("club", Club);
            await match.ExecuteNonQueryAsync();
        }

        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task ZoekWedstrijdAsync_VindtWedstrijdnummerEnDatumViaWedstrijdcode()
    {
        await using var conn = await OpstellingAsync();

        var result = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(conn, Wedstrijdcode, Club);

        result.Should().NotBeNull();
        result!.Wedstrijdnummer.Should().Be(3403);
        result.Datum.Should().Be(new DateOnly(2026, 9, 5));
    }

    [PostgresFact]
    public async Task ZoekWedstrijdAsync_OnbekendeWedstrijdcode_GeeftNullTerug()
    {
        await using var conn = await OpstellingAsync();

        var result = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(conn, 99999999, Club);

        result.Should().BeNull();
    }

    [PostgresFact]
    public async Task ZoekWedstrijdAsync_OntbrekendWedstrijdnummer_GeeftNullTerugInPlaatsVanTeCrashen()
    {
        await using var conn = await OpstellingAsync(wedstrijdnummer: null);

        var result = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(conn, Wedstrijdcode, Club);

        result.Should().BeNull("een onvolledig gesynchroniseerde rij mag geen crash veroorzaken");
    }

    [PostgresFact]
    public async Task CacheRoundtrip_SchrijvenEnLezenGeeftDezelfdeWaardeTerug()
    {
        await using var conn = await OpstellingAsync();

        var vooraf = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, Wedstrijdcode, Club);
        vooraf.Should().BeNull("nog niets gecachet voor deze wedstrijd");

        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, Wedstrijdcode, Club, "M392686417");
        var erna = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, Wedstrijdcode, Club);

        erna.Should().Be("M392686417");
    }

    [PostgresFact]
    public async Task CacheRoundtrip_TweedeSchrijfActieOverschrijftDeEerste()
    {
        await using var conn = await OpstellingAsync();

        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, Wedstrijdcode, Club, "M111111111");
        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, Wedstrijdcode, Club, "M222222222");

        var resultaat = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, Wedstrijdcode, Club);
        resultaat.Should().Be("M222222222", "een hernieuwde lookup moet de eerdere cache-waarde overschrijven, niet dupliceren");
    }

    // ── ZoekWedstrijdenZonderCacheAsync (#1017: warmup-timer) ──

    private async Task<NpgsqlConnection> WarmupOpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, $"DELETE FROM his.matches WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.sportlinkpublicmatchidcache WHERE clubcode = '{Club}'");

        return conn;
    }

    private static async Task InsertMatchAsync(NpgsqlConnection conn, long wedstrijdcode, long wedstrijdnummer, string kaledatum)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO his.matches (wedstrijdcode, wedstrijdnummer, kaledatum, clubcode, mta_inserted, mta_modified)
              VALUES (@code, @nummer, @datum, @club, NOW(), NOW())", conn);
        cmd.Parameters.AddWithValue("code", wedstrijdcode);
        cmd.Parameters.AddWithValue("nummer", wedstrijdnummer);
        cmd.Parameters.AddWithValue("datum", kaledatum);
        cmd.Parameters.AddWithValue("club", Club);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenZonderCacheAsync_VindtWedstrijdenBinnenBereikZonderCache()
    {
        await using var conn = await WarmupOpstellingAsync();
        await InsertMatchAsync(conn, 9600002, 5001, "2026-09-10");
        await InsertMatchAsync(conn, 9600003, 5002, "2026-09-11");

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
            conn, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), Club);

        resultaat.Should().HaveCount(2);
        resultaat.Should().Contain(w => w.Wedstrijdcode == 9600002 && w.Wedstrijdnummer == 5001);
        resultaat.Should().Contain(w => w.Wedstrijdcode == 9600003 && w.Wedstrijdnummer == 5002);
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenZonderCacheAsync_SluitWedstrijdenMetBestaandeCacheUit()
    {
        await using var conn = await WarmupOpstellingAsync();
        await InsertMatchAsync(conn, 9600004, 5003, "2026-09-10");
        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, 9600004, Club, "M300000004");

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
            conn, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), Club);

        resultaat.Should().BeEmpty("deze wedstrijd heeft al een PublicMatchId gecachet");
    }

    // ── ZoekWedstrijdenBijPublicMatchIdsAsync (#1111: wedstrijdcontext bij wijzigingsverzoeken) ──

    private static async Task InsertVolledigeMatchAsync(NpgsqlConnection conn, long wedstrijdcode, long wedstrijdnummer)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO his.matches (wedstrijdcode, wedstrijdnummer, kaledatum, aanvangstijd, thuisteam, uitteam, accommodatie,
                                       clubcode, mta_inserted, mta_modified)
              VALUES (@code, @nummer, '2026-09-05', '14:30', 'TEST1', 'TEST2', 'Sportpark Test', @club, NOW(), NOW())", conn);
        cmd.Parameters.AddWithValue("code", wedstrijdcode);
        cmd.Parameters.AddWithValue("nummer", wedstrijdnummer);
        cmd.Parameters.AddWithValue("club", Club);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenBijPublicMatchIdsAsync_VindtContextViaCache_EnLaatOnbekendeIdsWeg()
    {
        await using var conn = await WarmupOpstellingAsync();
        await InsertVolledigeMatchAsync(conn, 9600006, 6001);
        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, 9600006, Club, "M-test-6006");

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(
            conn, new[] { "M-test-6006", "M-nooit-gecachet" }, Club);

        resultaat.Should().ContainKey("M-test-6006");
        resultaat.Should().NotContainKey("M-nooit-gecachet", "zonder cache-rij is er geen koppeling — geen fout, gewoon afwezig");
        var ctx = resultaat["M-test-6006"];
        ctx.Wedstrijdcode.Should().Be(9600006);
        ctx.Wedstrijdnummer.Should().Be(6001);
        ctx.Thuisteam.Should().Be("TEST1");
        ctx.Uitteam.Should().Be("TEST2");
        ctx.Datum.Should().Be("2026-09-05");
        ctx.Tijd.Should().Be("14:30");
        ctx.Accommodatie.Should().Be("Sportpark Test");
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenBijPublicMatchIdsAsync_AndereClub_GeeftGeenContext()
    {
        await using var conn = await WarmupOpstellingAsync();
        await InsertVolledigeMatchAsync(conn, 9600007, 6002);
        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, 9600007, Club, "M-test-6007");

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(
            conn, new[] { "M-test-6007" }, "andere-club");

        resultaat.Should().BeEmpty("de ClubCode-discriminator geldt ook voor de reverse-lookup");
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenBijPublicMatchIdsAsync_LegeInvoer_DoetGeenQuery()
    {
        await using var conn = await WarmupOpstellingAsync();

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(
            conn, Array.Empty<string>(), Club);

        resultaat.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task ZoekWedstrijdenZonderCacheAsync_SluitWedstrijdenBuitenBereikUit()
    {
        await using var conn = await WarmupOpstellingAsync();
        await InsertMatchAsync(conn, 9600005, 5004, "2026-10-01"); // ver buiten het bereik hieronder

        var resultaat = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
            conn, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), Club);

        resultaat.Should().BeEmpty("deze wedstrijd valt buiten het opgevraagde datumbereik");
    }
}
