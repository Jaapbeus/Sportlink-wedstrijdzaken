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
    /// <summary>
    /// Review mode moet een te beoordelen antwoord opleveren (#712). De vorige versie van deze test
    /// zette het foutieve gedrag vast — <c>buildCalled == false</c> — waardoor er niets te reviewen
    /// was: geen antwoord opgebouwd, <c>AntwoordEmail</c> leeg, en status 'Verwerkt', dezelfde
    /// waarde als een mislukte verzending.
    /// </summary>
    [Fact]
    public async Task ReviewMode_SlaatVoorgesteldAntwoordOp_EnVerstuurtNiet()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();
        var buildCalled = false;

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 42,
            email: new InkomendBericht { MessageId = "m1", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () =>
            {
                buildCalled = true;
                return Task.FromResult(("subj", "voorgestelde-body"));
            },
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        buildCalled.Should().BeTrue();
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 42 && v.AntwoordEmail == "voorgestelde-body");

        // Review mode blokkeert alle uitgaande post — dat blijft ongewijzigd.
        graph.SentReplies.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
    }

    [Fact]
    public async Task ReviewMode_ZonderAntwoordNodig_ZetStatusReview_EnBouwtGeenAntwoord()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();
        var buildCalled = false;

        // Planning is mogelijk op de gevraagde datum → de policy onderdrukt het antwoord. Er is dan
        // ook in review mode niets voor te stellen, maar de status moet wél Review zijn.
        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 43,
            email: new InkomendBericht { MessageId = "m1b", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
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
        buildCalled.Should().BeFalse();
        persistence.VoorgesteldeAntwoorden.Should().BeEmpty();
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 43 && u.Status == EmailStatus.Review && u.GeextraheerdeData == null);
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task GeenReplyNodig_ZetStatusEnHandmatigePlanningLabel()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 100,
            email: new InkomendBericht { MessageId = "m2", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
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
            email: new InkomendBericht { MessageId = "m3", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == "afzender@voorbeeld.test" && r.Subject == "antwoord-subject");
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m3");
        persistence.AntwoordUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 200 && u.VerstuurdNaar == "afzender@voorbeeld.test" && u.AntwoordEmail == "antwoord-body");
    }

    [Fact]
    public async Task SendFout_UpdateFout_EnGeeftVerzendFoutTerug()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 300,
            email: new InkomendBericht { MessageId = "m4", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
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

        // Het bericht blijft ongelezen zodat de volgende poll het opnieuw probeert. Dat werkt alleen
        // omdat de idempotentie-guard naar de eindstatus kijkt — zie EmailIdempotentieTests.
        graph.MarkedAsReadIds.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();
    }

    /// <summary>
    /// Geen dubbel antwoord: als het antwoord verstuurd is maar het vastleggen in de database faalt,
    /// moet het bericht alsnog als gelezen worden gemarkeerd. Bleef het ongelezen, dan zou de
    /// volgende poll de afzender een tweede antwoord sturen. (#712)
    /// </summary>
    [Fact]
    public async Task VastleggenAntwoordMislukt_MarkeertBerichtAlsnogGelezen()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService { ThrowOnUpdateAntwoordVerstuurd = true };

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 400,
            email: new InkomendBericht { MessageId = "m5", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle();
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m5");
    }
}
