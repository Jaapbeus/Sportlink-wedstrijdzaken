using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using FunctionApp.Tests.Feedback;
using FunctionApp.Tests.Planner;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Planning;
using Planner.Shared.TeamResolution;
using Planner.Shared.Email.Trace;
using SportlinkFunction;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>Concrete mailboxcaller en foutcallback, echte bezetting zonder duur; geen AI/Graph-netwerk.</summary>
public class SqlServerPlannerViewIntegrationTests_PlanningDuurEmailCallerTests
{
    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Sunday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Sunday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Monday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Monday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Tuesday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Tuesday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Wednesday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Wednesday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Thursday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Thursday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Friday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Friday);

    [SqlServerFact]
    public Task OnbekendeBezettingsduur_Saturday_BewaartTraceEnFout_ZonderAntwoordOfVerzendintentie()
        => ControleerOnbekendeBezettingsduurAsync(DayOfWeek.Saturday);

    private static async Task ControleerOnbekendeBezettingsduurAsync(DayOfWeek dag)
    {
        await using var db = await SqlServerPlannerViewIntegrationTests.Fixture.OpenAsync();
        await db.MatchAsync();
        var datum = DateOnly.FromDateTime(DateTime.Today).AddDays(21);
        while (datum.DayOfWeek != dag) datum = datum.AddDays(1);
        await db.CommitEmailFixtureAsync(datum);
        SystemUtilities.AppSettings.SetForTests("clubCode", SqlServerPlannerViewIntegrationTests.Club);
        SystemUtilities.AppSettings.SetForTests("useRealtimeApi", "false");
        try
        {
            var graph = new FakeEmailGraphService();
            var persistence = new RecordingEmailPersistenceService();
            var ai = new Mock<IChatClient>(MockBehavior.Strict);
            var resolver = new Mock<ITeamResolver>(MockBehavior.Strict);
            using var services = new ServiceCollection().AddSingleton(ai.Object).BuildServiceProvider();
            var poort = new EmailVerwerkerPoort(NullLogger.Instance, services, NullLoggerFactory.Instance,
                graph, resolver.Object, persistence, new FakeNoodmailThrottleStore());
            poort.BereidAiVoor().Should().BeTrue();
            var email = new InkomendBericht { MessageId = "duur1814", Afzender = "trainer@voorbeeld.nl", Onderwerp = "Beschikbaarheid", Body = "" };

            // Twee echte pogingen: de tweede hergebruikt de verwerking, zonder terminale status.
            for (var poging = 1; poging <= 2; poging++)
            {
                var classificatie = new BerichtClassificatie
                {
                    Type = VerzoekType.BeschikbaarheidCheck, Datum = datum.ToString("yyyy-MM-dd"),
                    LeeftijdsCategorie = "JO13", AanvangsTijd = "12:00"
                };
                var werk = () => poort.VerwerkEmailAsync(email, classificatie, new HashSet<string>());
                var fout = await werk.Should().ThrowAsync<PlanningDuurOnbekendException>();
                persistence.StandToReturn = new EmailVerwerkingStand(1, "Fout", poging, false);
                await poort.LegVerwerkingsFoutVastAsync(email, EmailSanitizer.SanitizeFoutMelding(fout.Which.Message));
            }

            persistence.Traces.Should().HaveCount(2);
            persistence.Traces.Should().OnlyContain(x => x.Trace.Stappen.Any(s =>
                s.Code == TraceCodes.VerwerkingFout && s.Zekerheid == ZekerheidsNiveau.Mislukt));
            persistence.FoutUpdates.Should().HaveCount(2).And.OnlyContain(x => x.FoutMelding == PlanningDuurOnbekendException.PubliekeMelding);
            persistence.PogingVerhogingen.Should().Equal(1);
            persistence.StatusUpdates.Should().OnlyContain(x => x.Status == EmailStatus.Geclassificeerd);
            persistence.PlannerResponses.Should().BeEmpty();
            persistence.VerzendPogingMarkeringen.Should().BeEmpty();
            persistence.AntwoordUpdates.Should().BeEmpty();
            persistence.VoorgesteldeAntwoorden.Should().BeEmpty();
            graph.SentReplies.Should().BeEmpty();
            graph.TeamForwardings.Should().BeEmpty();
            graph.MarkedAsReadIds.Should().BeEmpty();
            ai.VerifyNoOtherCalls();
            resolver.VerifyNoOtherCalls();
        }
        finally { SystemUtilities.AppSettings.ResetForTests(); }
    }
}
