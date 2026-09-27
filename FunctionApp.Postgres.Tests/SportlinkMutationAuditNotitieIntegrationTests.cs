using AwesomeAssertions;
using Database.Postgres.Tests;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt de <c>notitie</c>-kolom vast die migratie 028 toevoegt aan <c>public.sportlinkmutationaudit</c>
/// (#1320) — en het club-scopingsgedrag van de UPDATE die
/// <see cref="global::FunctionApp.Postgres.Sportlink.PostgresSportlinkMutationAuditService.ZetNotitieAsync"/>
/// uitvoert. Bewust dezelfde raw-SQL-aanpak als <see cref="SportlinkMutationAuditCleanupIntegrationTests"/>
/// in plaats van de service-klasse zelf aan te roepen: die leest <c>PostgresDatabaseConfig.ConnectionString</c>
/// (een static, ooit-per-proces geïnitialiseerde omgevingsvariabele) — een test die daarvan afhangt
/// zou stilzwijgend crashen zodra <c>POSTGRES_TEST_CONNECTION_STRING</c> wél maar
/// <c>POSTGRES_CONNECTION_STRING</c> niet gezet is, in plaats van netjes over te slaan. In CI
/// (<c>fresh-db-postgres</c>) wijzen beide naar dezelfde database, maar dat is geen garantie voor
/// een losse lokale testrun.
/// </summary>
public class SportlinkMutationAuditNotitieIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task Notitie_JuisteClub_WordtBijgewerkt()
    {
        await using var conn = await OpenAsync();
        var id = await SeedAuditAsync(conn, "club-a");

        var affected = await ZetNotitieAsync(conn, id, "club-a", "Wedstrijd is niet verplaatst, tegenstander kreeg geen melding.");

        affected.Should().Be(1);
        (await LeesNotitieAsync(conn, id)).Should().Be("Wedstrijd is niet verplaatst, tegenstander kreeg geen melding.");
    }

    [PostgresFact]
    public async Task Notitie_AndereClub_WordtNietBijgewerkt()
    {
        // Scoping-bewijs: een audit-rij van "club-a" mag niet wijzigen via een aanroep die zichzelf
        // als "club-b" identificeert — dat zou een club-grensoverschrijding zijn.
        await using var conn = await OpenAsync();
        var id = await SeedAuditAsync(conn, "club-a");

        var affected = await ZetNotitieAsync(conn, id, "club-b", "Dit hoort niet te lukken.");

        affected.Should().Be(0);
        (await LeesNotitieAsync(conn, id)).Should().BeNull();
    }

    [PostgresFact]
    public async Task Notitie_OnbekendId_GeeftNulRijenBijgewerkt()
    {
        await using var conn = await OpenAsync();
        await SeedAuditAsync(conn, "club-a");

        var affected = await ZetNotitieAsync(conn, long.MaxValue, "club-a", "Bestaat niet.");

        affected.Should().Be(0);
    }

    [PostgresFact]
    public async Task Notitie_LegeStringOpEenBestaandeNotitie_WistDeNotitie()
    {
        // Acceptatiecriterium: een lege string is een toegestane manier om te wissen, niet fout.
        await using var conn = await OpenAsync();
        var id = await SeedAuditAsync(conn, "club-a");
        await ZetNotitieAsync(conn, id, "club-a", "Eerste notitie.");

        var affected = await ZetNotitieAsync(conn, id, "club-a", "");

        affected.Should().Be(1);
        (await LeesNotitieAsync(conn, id)).Should().BeEmpty();
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Zelfde UPDATE-vorm als PostgresSportlinkMutationAuditService.ZetNotitieAsync —
    /// zie de klasse-doc-comment voor waarom dit hier gedupliceerd staat in plaats van de
    /// servicemethode zelf aan te roepen.</summary>
    private static async Task<int> ZetNotitieAsync(NpgsqlConnection conn, long id, string clubCode, string notitie)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = @"
            UPDATE public.sportlinkmutationaudit
            SET notitie = @notitie
            WHERE id = @id AND clubcode = @clubcode";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clubcode", clubCode);
        command.Parameters.AddWithValue("@notitie", notitie);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> SeedAuditAsync(NpgsqlConnection conn, string clubCode)
    {
        await using var deleteCmd = new NpgsqlCommand("DELETE FROM public.sportlinkmutationaudit", conn);
        await deleteCmd.ExecuteNonQueryAsync();

        await using var insertCmd = new NpgsqlCommand(
            @"INSERT INTO public.sportlinkmutationaudit
                  (clubcode, functionelerol, triggerddoor, publicmatchid, actie, resultaat, tijdstip)
              VALUES (@clubcode, 'Wedstrijdzaken', 'tester', 'PM-TEST', 'UpdateMatchDetails:ChangeRequest', 'DryRunLocked', NOW())
              RETURNING id", conn);
        insertCmd.Parameters.AddWithValue("clubcode", clubCode);
        var id = await insertCmd.ExecuteScalarAsync();
        return (long)id!;
    }

    private static async Task<string?> LeesNotitieAsync(NpgsqlConnection conn, long id)
    {
        await using var cmd = new NpgsqlCommand("SELECT notitie FROM public.sportlinkmutationaudit WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }
}
