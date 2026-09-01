using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Admin;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #956: <c>AdminSpeeltijdenRepository.UpdateAsync</c>/<c>DeleteAsync</c> vergeleken <c>leeftijd</c>
/// met een kale <c>=</c> — case-sensitief op Postgres (in tegenstelling tot SQL Server's
/// <c>Latin1_General_CI_AS</c>-collatie). Een lookup met afwijkende hoofdlettering t.o.v. de
/// opgeslagen sleutel gaf zo stilzwijgend 0 bijgewerkte/verwijderde rijen — op de admin-endpoints
/// zichtbaar als een onterechte <c>404</c>.
/// </summary>
public class AdminSpeeltijdenRepositoryCasingIntegrationTests
{
    private const string ClubCode = "case956test";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    [PostgresFact]
    public async Task UpdateAsync_AfwijkendeHoofdlettering_VindtDeRijToch()
    {
        await using var conn = await OpstellingAsync();

        var rows = await AdminSpeeltijdenRepository.UpdateAsync(
            "jo9", // opgeslagen als "JO9"
            new SpeeltijdInput("jo9", 1.00m, 70, 30, 10, null),
            ClubCode, ConnectionString);

        rows.Should().Be(1, "de lookup moet de rij vinden ongeacht hoofdlettering");

        await using var check = new NpgsqlCommand(
            "SELECT wedstrijdtotaal FROM public.speeltijden WHERE clubcode = @cc", conn);
        check.Parameters.AddWithValue("cc", ClubCode);
        (await check.ExecuteScalarAsync()).Should().Be(70);
    }

    [PostgresFact]
    public async Task DeleteAsync_AfwijkendeHoofdlettering_VerwijdertDeRijToch()
    {
        await using var conn = await OpstellingAsync();

        var rows = await AdminSpeeltijdenRepository.DeleteAsync("Jo9", ClubCode, ConnectionString);

        rows.Should().Be(1, "de lookup moet de rij vinden ongeacht hoofdlettering");

        await using var check = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.speeltijden WHERE clubcode = @cc", conn);
        check.Parameters.AddWithValue("cc", ClubCode);
        (await check.ExecuteScalarAsync()).Should().Be(0L);
    }

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var clean = new NpgsqlCommand(
            "DELETE FROM public.speeltijden WHERE clubcode = @cc", conn))
        {
            clean.Parameters.AddWithValue("cc", ClubCode);
            await clean.ExecuteNonQueryAsync();
        }
        await using (var seed = new NpgsqlCommand(
            @"INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, clubcode)
              VALUES ('JO9', 1.00, 60, 30, 10, @cc)", conn))
        {
            seed.Parameters.AddWithValue("cc", ClubCode);
            await seed.ExecuteNonQueryAsync();
        }
        return conn;
    }
}
