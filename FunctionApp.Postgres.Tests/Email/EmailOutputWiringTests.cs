using AwesomeAssertions;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Planner.Shared;
using Planner.Shared.Email;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using FunctionApp.Postgres.Planner.Repositories;

namespace FunctionApp.Postgres.Tests.Email;

public class EmailOutputWiringTests
{
    [Fact]
    public void TeamControle_WaarschuwingEnConflictRedenGebruikenVeiligeTeamtekst()
    {
        var response = new CheckAvailabilityResponse();
        var request = new CheckAvailabilityRequest { TeamNaam = "<b>JO13-2</b>" };
        var date = new DateOnly(2031, 1, 4);
        AvailabilityService.PasTeamControleToe(TeamWedstrijdenOpDatum.NietHerkend(), request, date, response).Should().BeFalse();
        response.Waarschuwingen.Should().ContainSingle().Which.Should().Contain(AiVeldSanering.GeneriekTeam).And.NotContain("<");
        AvailabilityService.PasTeamControleToe(TeamWedstrijdenOpDatum.Herkend([new()]), request, date, response).Should().BeTrue();
        response.Reden.Should().StartWith(AiVeldSanering.GeneriekTeam).And.NotContain("<");
    }

    [Fact]
    public async Task AvailabilityHttpResult_SaneertDeWerkelijkeWaarschuwingUitDePlanner()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"datum\":\"2031-01-04\"}"));
        var resultaat = await PlannerFunction.CheckAvailabilityInhoudAsync(
            context.Request, NullLogger.Instance, "ALLSTARS", request =>
            {
                request.Datum.Should().Be("2031-01-04");
                return Task.FromResult(new CheckAvailabilityResponse
                {
                    Waarschuwingen = ["Controleer https://allstars-fc.test <b>nu</b>\r\n" + new string('x', 400), "trainer@voorbeeld.nl"]
                });
            });
        var response = resultaat.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CheckAvailabilityResponse>().Which;
        response.Waarschuwingen.Should().ContainSingle();
        response.Waarschuwingen[0].Should().NotContain("://").And.NotContain("<").And.NotContain("\n");
        response.Waarschuwingen[0].Length.Should().BeLessThanOrEqualTo(AiVeldSanering.MaxWaarschuwingLengte + 1);
    }

    [Fact]
    public async Task Forwarding_ActualGraphOutputGebruiktVeiligeBuilder()
    {
        var graph = new FakeEmailGraphService();
        var verzoek = new TeamContactDoorstuurVerzoek("<b>JO13-2</b>", "ongeldig", "Jan\r\nBcc: test", new string('x', 9000));
        var doorstuur = TeamContactDoorstuurAfhandeling.MaakStandaardDoorstuur(
            () => new ClaimbarePersistentie(),
            _ => Task.FromResult<string?>("trainer@voorbeeld.nl"),
            () => "trainer@voorbeeld.nl",
            graph.StuurTeamContactDoorAsync,
            NullLogger.Instance);
        (await doorstuur(1, verzoek)).Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        var mail = graph.TeamForwardings.Should().ContainSingle().Which;
        mail.Subject.Should().Contain(AiVeldSanering.GeneriekTeam).And.NotContain("<").And.NotContain("Bcc:");
        mail.Body.Should().NotContain("Bcc:").And.Contain("kies handmatig het juiste contactkanaal");
        mail.Body.Length.Should().BeLessThan(9000);
        mail.AanvragerEmail.Should().BeNull();
        mail.CoordinatorEmail.Should().Be("trainer@voorbeeld.nl");
    }

    [Fact]
    public async Task TeamleiderNotificatie_ActualGraphOutputSaneertTeamEnDatum()
    {
        var graph = new FakeEmailGraphService();
        await EmailProcessorFunction.StuurTeamleiderNotificatieAsync(
            "unused", "ALLSTARS", graph, "<b>JO13-2</b>", "<b>datum</b>", NullLogger.Instance,
            _ => Task.FromResult<TeamleiderContact?>(new() { Naam = "Jan de Vries", Emailadres = "trainer@voorbeeld.nl" }),
            _ => "Planner");
        var mail = graph.SentReplies.Should().ContainSingle().Which;
        mail.Subject.Should().Contain(AiVeldSanering.GeneriekTeam).And.Contain("de opgegeven datum").And.NotContain("<");
        mail.Body.Should().Contain(AiVeldSanering.GeneriekTeam).And.NotContain("<");
    }

    /// <summary>One explicit NietGestart row that the forwarding attempt may claim exactly once.</summary>
    private sealed class ClaimbarePersistentie : ITeamContactDoorstuurPersistentie
    {
        private bool _geclaimd;

        public Task<TeamContactDoorstuurClaim> ClaimAsync(int verwerkingId)
        {
            var claim = _geclaimd ? TeamContactDoorstuurClaim.NietBeschikbaar : TeamContactDoorstuurClaim.Geclaimd;
            _geclaimd = true;
            return Task.FromResult(claim);
        }

        public Task<bool> MarkeerTerminalAsync(int verwerkingId, TeamContactDoorstuurStatus status)
            => Task.FromResult(true);
    }
}
