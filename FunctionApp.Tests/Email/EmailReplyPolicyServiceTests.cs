using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Email;

public class EmailReplyPolicyServiceTests
{
    [Fact]
    public async Task ReviewMode_LabeltBericht_EnVerstuurtNiet()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();
        var buildCalled = false;

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 42,
            email: new InkomendBericht { MessageId = "m1", Afzender = "x@y.nl", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: JsonConvert.SerializeObject(new CheckAvailabilityResponse { Beschikbaar = true }),
            reviewMode: true,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () =>
            {
                buildCalled = true;
                return Task.FromResult(("subj", "body"));
            },
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
        buildCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GeenReplyNodig_ZetStatusEnHandmatigePlanningLabel()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 100,
            email: new InkomendBericht { MessageId = "m2", Afzender = "x@y.nl", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: JsonConvert.SerializeObject(new CheckAvailabilityResponse { Beschikbaar = true }),
            reviewMode: false,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Handmatige planning"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m2");
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 100 && u.Status == EmailStatus.GeenAntwoordNodig && u.GeextraheerdeData == null);
    }

    [Fact]
    public async Task ReplyVersturen_SlaatAntwoordOp_EnMarkeertGelezen()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 200,
            email: new InkomendBericht { MessageId = "m3", Afzender = "afzender@club.nl", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == "afzender@club.nl" && r.Subject == "antwoord-subject");
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m3");
        persistence.AntwoordUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 200 && u.VerstuurdNaar == "afzender@club.nl" && u.AntwoordEmail == "antwoord-body");
    }

    [Fact]
    public async Task SendFout_UpdateFout_EnGeeftVerzendFoutTerug()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 300,
            email: new InkomendBericht { MessageId = "m4", Afzender = "afzender@club.nl", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.VerzendFout);
        persistence.FoutUpdates.Should().ContainSingle(u =>
            u.MessageId == "m4" && u.FoutMelding == "sanitized");
    }
}
