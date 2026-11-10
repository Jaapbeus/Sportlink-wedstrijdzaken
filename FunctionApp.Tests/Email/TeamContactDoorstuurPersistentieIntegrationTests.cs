using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.Email;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class TeamContactDoorstuurPersistentieIntegrationTests
{
    [SqlServerFact]
    public async Task ClaimIsAtomicAndLegacyAndWrongClubCannotSend()
    {
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        var club = "ztest" + Guid.NewGuid().ToString("N")[..8];
        SportlinkFunction.SystemUtilities.AppSettings.SetForTests("clubCode", club);
        var id = await new SqlEmailPersistenceRepository().InsertEmailVerwerkingAsync(
            new InkomendBericht
            {
                MessageId = "forward-test-" + Guid.NewGuid().ToString("N"),
                Afzender = "trainer@voorbeeld.nl", Onderwerp = "test",
                OntvangstDatum = DateTime.UtcNow
            });

        try
        {
            var store = new SqlTeamContactDoorstuurPersistentie(cs, club);
            var wrongClub = new SqlTeamContactDoorstuurPersistentie(cs, "ALLSTARS");
            (await wrongClub.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.NietBeschikbaar);
            // Production insert must explicitly set NietGestart, otherwise neither contender can claim.
            var claims = await Task.WhenAll(store.ClaimAsync(id), store.ClaimAsync(id));
            claims.Count(c => c == TeamContactDoorstuurClaim.Geclaimd).Should().Be(1);
            (await store.MarkeerTerminalAsync(id, TeamContactDoorstuurStatus.Geaccepteerd)).Should().BeTrue();
            (await store.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.AlGeaccepteerd);
            (await store.MarkeerTerminalAsync(id, TeamContactDoorstuurStatus.NietVerstuurd)).Should().BeFalse();
            (await store.MarkeerTerminalAsync(id + 999999, TeamContactDoorstuurStatus.Geaccepteerd)).Should().BeFalse();

            using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            using var legacy = new SqlCommand("UPDATE [planner].[EmailVerwerking] SET [TeamContactDoorstuurStatus] = NULL WHERE [Id] = @id", connection);
            legacy.Parameters.AddWithValue("@id", id);
            await legacy.ExecuteNonQueryAsync();
            (await store.ClaimAsync(id)).Should().Be(TeamContactDoorstuurClaim.NietBeschikbaar);
        }
        finally
        {
            using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            using var delete = new SqlCommand("DELETE FROM [planner].[EmailVerwerking] WHERE [Id] = @id", connection);
            delete.Parameters.AddWithValue("@id", id);
            await delete.ExecuteNonQueryAsync();
            SportlinkFunction.SystemUtilities.AppSettings.ResetForTests();
        }
    }
}
