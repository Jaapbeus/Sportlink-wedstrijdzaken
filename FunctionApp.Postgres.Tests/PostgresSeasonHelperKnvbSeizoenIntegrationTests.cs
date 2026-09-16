using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag vast van <see cref="PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync"/> (#1141) —
/// de Postgres-vertaling van <c>SystemUtilities.SeasonHelper.GetCurrentKnvbSeizoenAsync</c>, nodig
/// voor het "verzet zonder datum"-pad (#561).
///
/// <para>
/// <b>Isolatie zonder de tabel te wipen:</b> <c>public.season</c> heeft geen ClubCode-discriminator
/// en wordt door meerdere testklassen in dit project gedeeld (o.a. via
/// <c>PostgresSeasonProceduresIntegrationTests</c> in <c>Database.Postgres.Tests</c>, dat wél een
/// eigen database heeft — dit project niet). In plaats van de tabel leeg te maken (zou andere,
/// gelijktijdig draaiende testklassen kunnen breken), zet <see cref="ZaaiAsync"/> een rij met een
/// <c>datefrom</c> ver in het verleden (jaar 1900) en een <c>dateuntil</c> ver in de toekomst
/// (jaar 9999): <c>ORDER BY datefrom ASC LIMIT 1</c> kiest die rij dan altijd als eerste,
/// onafhankelijk van welke echte seizoensrijen (migratie 008-seed, huidig jaar) toevallig aanwezig
/// zijn.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PostgresSeasonHelperKnvbSeizoenIntegrationTests : IAsyncLifetime
{
    private const string DominantSeizoenNaam = "1900-9999";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.season WHERE name = @naam", conn);
        cmd.Parameters.AddWithValue("naam", DominantSeizoenNaam);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ZaaiAsync(int datefromJaar, int datefromMaand, int dateuntilJaar, int dateuntilMaand, string naam)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO public.season (name, datefrom, dateuntil)
            VALUES (@naam, @datefrom, @dateuntil)
            ON CONFLICT (name) DO UPDATE SET datefrom = EXCLUDED.datefrom, dateuntil = EXCLUDED.dateuntil
            """, conn);
        cmd.Parameters.AddWithValue("naam", naam);
        cmd.Parameters.AddWithValue("datefrom", new DateTime(datefromJaar, datefromMaand, 1));
        cmd.Parameters.AddWithValue("dateuntil", new DateTime(dateuntilJaar, dateuntilMaand, 1));
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task GeeftHetVroegsteSeizoenMetTotNogNietVerstrekenDateUntil()
    {
        // Ver-verleden-datefrom + ver-toekomst-dateuntil domineert ORDER BY datefrom ASC LIMIT 1,
        // ongeacht welke echte (migratie-008-geseede) seizoensrijen toevallig aanwezig zijn.
        await ZaaiAsync(1900, 1, 9999, 1, DominantSeizoenNaam);

        var seizoen = await PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync(NullLogger.Instance);

        seizoen.Should().Be("1900/9999", "de methode formatteert als '{datefrom.Jaar}/{dateuntil.Jaar}'");
    }

    [PostgresFact]
    public async Task KiestVroegsteDatefrom_NietDeLaatsteDateuntil()
    {
        await ZaaiAsync(1900, 1, 9999, 1, DominantSeizoenNaam);
        // Een tweede, nog dominantere rij met een nog vroegere datefrom en nog verdere dateuntil —
        // bewijst dat de methode niet zomaar "de eerste rij" pakt maar echt op datefrom sorteert.
        await ZaaiAsync(1800, 1, 9998, 1, "1800-9998");
        try
        {
            var seizoen = await PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync(NullLogger.Instance);

            seizoen.Should().Be("1800/9998");
        }
        finally
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM public.season WHERE name = @naam", conn);
            cmd.Parameters.AddWithValue("naam", "1800-9998");
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
