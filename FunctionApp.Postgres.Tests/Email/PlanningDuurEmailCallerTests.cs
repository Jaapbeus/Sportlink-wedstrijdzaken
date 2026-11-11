using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Planning;
using Planner.Shared.TeamResolution;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>Concrete mailboxcaller en foutcallback met echte onbekende bezettingsduur en retry.</summary>
public class PlanningDuurEmailCallerTests
{
    [PostgresFact]
    public async Task OnbekendeBezettingsduur_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
    {
        await using var conn = await AvailabilityServiceIntegrationTests.OpstellingAsync();
        var datum = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);
        datum = datum.AddDays(((int)DayOfWeek.Saturday - (int)datum.DayOfWeek + 7) % 7);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (941814, @datum, '10:00', 'Veld 1', 'ALLSTARS JO99-1', 'ALLSTARS JO99-1 - Gastteam',
                    'Sportpark Testclub', 'Vastgesteld', 'testclub-availsvc', NOW(), NOW())
            """, conn))
        {
            seed.Parameters.AddWithValue("datum", datum.ToString("yyyy-MM-dd"));
            await seed.ExecuteNonQueryAsync();
        }
        try
        {
            await ControleerCallerAsync(conn, datum);
        }
        finally { PostgresAppSettings.ResetForTests(); }
    }

    private static async Task ControleerCallerAsync(NpgsqlConnection conn, DateOnly datum)
    {
        const string club = "testclub-availsvc";
        var cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        var graph = new FakeEmailGraphService();
        var ai = new Mock<IChatClient>(MockBehavior.Strict);
        var resolver = new Mock<ITeamResolver>(MockBehavior.Strict);
        var berichtAi = new BerichtAiService(NullLogger<BerichtAiService>.Instance, ai.Object);
        var replyOpslag = new RecordingReplyPersistentie();
        var reply = new EmailReplyPolicyService(_ => replyOpslag);
        var email = new InkomendBericht { MessageId = $"duur1814-{Guid.NewGuid():N}", Afzender = "trainer@voorbeeld.nl", Onderwerp = "Beschikbaarheid", Body = "", OntvangstDatum = DateTime.UtcNow };
        for (var poging = 1; poging <= 2; poging++)
        {
            var classificatie = new BerichtClassificatie
            {
                Type = VerzoekType.BeschikbaarheidCheck, Datum = datum.ToString("yyyy-MM-dd"),
                LeeftijdsCategorie = "JO13", AanvangsTijd = "12:00"
            };
            var werk = () => EmailProcessorFunction.VerwerkEmailAsync(cs, club, email, classificatie,
                graph, new HashSet<string>(), berichtAi, reply, NullLogger.Instance, resolver.Object);
            var fout = await werk.Should().ThrowAsync<PlanningDuurOnbekendException>();
            await EmailProcessorFunction.LegVerwerkingsFoutVastAsync(cs, club, email,
                EmailSanitizer.SanitizeFoutMelding(fout.Which.Message), NullLogger.Instance);
            var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);
            stand!.Status.Should().Be("Fout");
            stand.Pogingen.Should().Be(poging);
            stand.AntwoordVerstuurd.Should().BeFalse();
            var trace = await EmailTraceRepository.HaalOpAsync(cs, club, stand.VerwerkingId);
            trace.Should().NotBeNull();
            System.Text.Json.JsonSerializer.Serialize(trace).Should().Contain(TraceCodes.VerwerkingFout);
        }
        await using var cmd = new NpgsqlCommand("""
            SELECT plannerresponse, verzendpogingoputc, antwoordemail, foutmelding
            FROM planner.emailverwerking WHERE messageid = @id
            """, conn);
        cmd.Parameters.AddWithValue("id", email.MessageId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        for (var i = 0; i < 3; i++) reader.IsDBNull(i).Should().BeTrue();
        reader.GetString(3).Should().Be(PlanningDuurOnbekendException.PubliekeMelding);
        replyOpslag.VerzendPogingMarkeringen.Should().BeEmpty();
        replyOpslag.AntwoordUpdates.Should().BeEmpty();
        replyOpslag.VoorgesteldeAntwoorden.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
        graph.TeamForwardings.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
        ai.VerifyNoOtherCalls();
        resolver.VerifyNoOtherCalls();
    }
}
