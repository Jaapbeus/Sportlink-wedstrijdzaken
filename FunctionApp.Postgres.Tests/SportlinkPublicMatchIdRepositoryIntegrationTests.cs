using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
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
    private const long Wedstrijdcode = 9200001;

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

        var result = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(conn, 999999999, Club);

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
}
