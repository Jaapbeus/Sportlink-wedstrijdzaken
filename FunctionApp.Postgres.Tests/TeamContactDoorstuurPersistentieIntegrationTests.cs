using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Email;
using Npgsql;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public class TeamContactDoorstuurPersistentieIntegrationTests
{
    [PostgresFact]
    public async Task ClaimIsAtomicAndLegacyAndWrongClubCannotSend()
    {
        var cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        var club = "ztest" + Guid.NewGuid().ToString("N")[..8];
        var messageId = "forward-test-" + Guid.NewGuid().ToString("N");
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs,
            new InkomendBericht
            {
                MessageId = messageId, Afzender = "trainer@voorbeeld.nl", Onderwerp = "test",
                OntvangstDatum = DateTime.UtcNow
            }, club);

        try
        {
            var store = new PostgresTeamContactDoorstuurPersistentie(cs, club);
            var wrongClub = new PostgresTeamContactDoorstuurPersistentie(cs, "ALLSTARS");
            (await wrongClub.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.NietBeschikbaar);
            // Production insert must explicitly set NietGestart, otherwise neither contender can claim.
            var claims = await Task.WhenAll(store.ClaimAsync(id), store.ClaimAsync(id));
            claims.Count(c => c == TeamContactDoorstuurClaim.Geclaimd).Should().Be(1);
            (await store.MarkeerTerminalAsync(id, TeamContactDoorstuurStatus.Geaccepteerd)).Should().BeTrue();
            (await store.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.AlGeaccepteerd);
            (await store.MarkeerTerminalAsync(id, TeamContactDoorstuurStatus.NietVerstuurd)).Should().BeFalse();
            (await store.MarkeerTerminalAsync(id + 999999, TeamContactDoorstuurStatus.Geaccepteerd)).Should().BeFalse();

            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var legacy = new NpgsqlCommand("""
                UPDATE planner.emailverwerking SET teamcontactdoorstuurstatus = NULL WHERE id = @id
                """, connection);
            legacy.Parameters.AddWithValue("id", id);
            await legacy.ExecuteNonQueryAsync();
            (await store.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.NietBeschikbaar);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var delete = new NpgsqlCommand("DELETE FROM planner.emailverwerking WHERE id = @id", connection);
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync();
        }
    }
}
