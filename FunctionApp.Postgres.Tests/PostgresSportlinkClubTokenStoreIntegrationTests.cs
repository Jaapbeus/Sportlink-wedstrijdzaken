using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Sportlink;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt <see cref="PostgresSportlinkClubTokenStore"/>'s gedrag vast (#991, epic #986) — de
/// Postgres-tier-implementatie van <c>ISportlinkClubTokenStore</c> die het rotarende refresh_token
/// in een eigen DB-tabel bewaart i.p.v. via de Azure Management API (#998). Draait tegen een echte
/// Postgres-instantie.
/// </summary>
public class PostgresSportlinkClubTokenStoreIntegrationTests : IDisposable
{
    private const string Club = "testclub-tokenstore";
    private const string Rol = "Wedstrijdzaken";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public PostgresSportlinkClubTokenStoreIntegrationTests()
    {
        PostgresAppSettings.SetForTests("clubCode", Club);
    }

    public void Dispose() => PostgresAppSettings.ResetForTests();

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.sportlinkservicetokens WHERE clubcode = @club", conn);
        cmd.Parameters.AddWithValue("club", Club);
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    [PostgresFact]
    public async Task LeesRefreshToken_GeenRijAanwezig_GeeftNullTerug()
    {
        await using var conn = await OpstellingAsync();
        var sut = new PostgresSportlinkClubTokenStore(ConnectionString, NullLogger<PostgresSportlinkClubTokenStore>.Instance);

        var result = sut.LeesRefreshToken(Rol);

        result.Should().BeNull();
    }

    [PostgresFact]
    public async Task SchrijfEnLeesRefreshToken_Roundtrip_GeeftDeOpgeslagenWaardeTerug()
    {
        await using var conn = await OpstellingAsync();
        var sut = new PostgresSportlinkClubTokenStore(ConnectionString, NullLogger<PostgresSportlinkClubTokenStore>.Instance);

        await sut.SchrijfRefreshTokenAsync(Rol, "fictief-refresh-token-1");
        var result = sut.LeesRefreshToken(Rol);

        result.Should().Be("fictief-refresh-token-1");
    }

    [PostgresFact]
    public async Task SchrijfRefreshTokenAsync_TweedeSchrijfActie_RoteertNaarDeNieuweWaarde()
    {
        await using var conn = await OpstellingAsync();
        var sut = new PostgresSportlinkClubTokenStore(ConnectionString, NullLogger<PostgresSportlinkClubTokenStore>.Instance);

        await sut.SchrijfRefreshTokenAsync(Rol, "eerste-token");
        await sut.SchrijfRefreshTokenAsync(Rol, "geroteerd-token");
        var result = sut.LeesRefreshToken(Rol);

        result.Should().Be("geroteerd-token", "elke refresh geeft een nieuw token dat het vorige vervangt, niet aanvult (#990-onderzoek)");
    }

    [PostgresFact]
    public async Task SchrijfRefreshTokenAsync_ZetEenVervaltopdatumInDeToekomst()
    {
        await using var conn = await OpstellingAsync();
        var sut = new PostgresSportlinkClubTokenStore(ConnectionString, NullLogger<PostgresSportlinkClubTokenStore>.Instance);

        await sut.SchrijfRefreshTokenAsync(Rol, "fictief-token");

        await using var cmd = new NpgsqlCommand(
            "SELECT refreshtokenvervaltop FROM public.sportlinkservicetokens WHERE rolnaam = @rol AND clubcode = @club", conn);
        cmd.Parameters.AddWithValue("rol", Rol);
        cmd.Parameters.AddWithValue("club", Club);
        var vervaltop = (DateTime)(await cmd.ExecuteScalarAsync())!;

        vervaltop.Should().BeAfter(DateTime.UtcNow, "een net geschreven token mag niet meteen als verlopen gelden");
    }
}
