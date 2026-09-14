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
    /// waarde als een mislukte verzending. Zonder <c>reviewRecipient</c> gaat er nog steeds geen mail
    /// de deur uit — dat pad wordt hieronder in een aparte test met een recipient afgedekt (#801).
    /// </summary>
    [Fact]
    public async Task ReviewMode_ZonderReviewRecipient_SlaatVoorgesteldAntwoordOp_EnVerstuurtNiet()
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
            reviewRecipient: null,
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

        // Review mode blokkeert post naar de originele afzender — dat blijft ongewijzigd. Zonder
        // reviewRecipient wordt er dus helemaal niets verstuurd.
        graph.SentReplies.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
    }

    /// <summary>
    /// Herstel van de regressie uit #543 (2026-06-20): review mode stuurde ooit een testantwoord
    /// naar een apart reviewadres, tot dat bewust werd verwijderd. #801 herstelt dit — met behoud
    /// van de #712-opslag in <c>AntwoordEmail</c> — omdat het voorstel anders alleen via directe
    /// databasetoegang te lezen is (de Admin GUI geeft AntwoordEmail nooit terug, AVG).
    /// </summary>
    [Fact]
    public async Task ReviewMode_MetReviewRecipient_VerstuurtTestantwoordNaarRecipient()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 44,
            email: new InkomendBericht
            {
                MessageId = "m1c", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test",
                ConversationId = "conv-44",
            },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: "reviewer@voorbeeld.test",
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 44 && v.AntwoordEmail == "voorgestelde-body");

        // De testmail gaat naar reviewRecipient, nooit naar de originele afzender.
        graph.SentReplies.Should().ContainSingle(r =>
            r.To == "reviewer@voorbeeld.test" && r.Subject == "subj" && r.ConversationId == "conv-44");
        persistence.AntwoordUpdates.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1c");
    }

    /// <summary>
    /// Een mislukte reviewmail mag de opslag en labeling niet blokkeren — het voorstel blijft dan
    /// alsnog in de database te vinden (#801).
    /// </summary>
    [Fact]
    public async Task ReviewMode_ReviewmailMislukt_SlaatTochOpEnLabelt()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 45,
            email: new InkomendBericht { MessageId = "m1d", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: "reviewer@voorbeeld.test",
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 45 && v.AntwoordEmail == "voorgestelde-body");
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1d");
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
            reviewRecipient: null,
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
            reviewRecipient: null,
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
            reviewRecipient: null,
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

    /// <summary>
    /// Expliciete afwijzing (#1133): een Graph-<c>ODataError</c> met een 4xx-statuscode bewijst dat er
    /// niets verstuurd is. Dat is het enige geval waarin de verzendintentie gewist mag worden zodat de
    /// volgende poll opnieuw probeert.
    /// </summary>
    [Fact]
    public async Task SendFout_ExpliciteteAfwijzing_WistIntentie_EnGeeftVerzendFoutTerug()
    {
        var service = new EmailReplyPolicyService();
        var odataError = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 400 };
        var graph = new FakeEmailGraphService { ExceptionToThrowOnSendReply = odataError };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 300,
            email: new InkomendBericht { MessageId = "m4", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.VerzendFout);
        persistence.FoutUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 300 && u.FoutMelding == "sanitized");

        // De verzendintentie is gezet vóór de poging en weer gewist omdat het versturen aantoonbaar
        // mislukte (een 4xx bewijst dat Graph niets verstuurd heeft) — anders zou de volgende poll
        // dit als "uitkomst onbekend" zien en niet opnieuw proberen, terwijl dat hier juist de
        // bedoeling is. (#716, #1133)
        persistence.VerzendPogingMarkeringen.Should().ContainSingle(id => id == 300);
        persistence.VerzendPogingWissingen.Should().ContainSingle(id => id == 300);

        // Het bericht blijft ongelezen zodat de volgende poll het opnieuw probeert. Dat werkt alleen
        // omdat de idempotentie-guard naar de eindstatus kijkt — zie EmailIdempotentieTests.
        graph.MarkedAsReadIds.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();
        persistence.StatusUpdates.Should().BeEmpty();
    }

    /// <summary>
    /// Kern van #1133: Graph accepteert het bericht (het staat al in <c>SentReplies</c>) maar de
    /// respons gaat verloren door een time-out. De verzendintentie MOET blijven staan — wissen zou
    /// een volgende poll een tweede antwoord laten sturen. Het bericht gaat direct op Review in
    /// plaats van te wachten op de volgende poll.
    /// </summary>
    [Fact]
    public async Task SendFout_OnbekendeUitkomstDoorTimeOut_LaatIntentieStaan_EnZetReview()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService { ExceptionToThrowOnSendReply = new TaskCanceledException() };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 301,
            email: new InkomendBericht { MessageId = "m4b", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst);

        // Graph "accepteerde" het bericht — het staat in SentReplies — vóórdat de time-out optrad.
        graph.SentReplies.Should().ContainSingle(r => r.To == "afzender@voorbeeld.test");

        // De harde eis: NIET wissen. Dit is de bug die #1133 dichtte — vóór de fix werd hier
        // onvoorwaardelijk WisVerzendPogingAsync aangeroepen.
        persistence.VerzendPogingMarkeringen.Should().ContainSingle(id => id == 301);
        persistence.VerzendPogingWissingen.Should().BeEmpty();

        // Status direct op Review — niet pas bij de volgende poll.
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 301 && u.Status == EmailStatus.Review && u.GeextraheerdeData == null);
        persistence.FoutUpdates.Should().BeEmpty();

        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m4b");
    }

    /// <summary>
    /// Een 5xx bewijst niets: Graph zelf had een probleem, mogelijk ná het (deels) verwerken van het
    /// verzoek. Moet dus ook als onbekende uitkomst gelden, niet als expliciete afwijzing.
    /// </summary>
    [Fact]
    public async Task SendFout_OnbekendeUitkomstDoor5xx_LaatIntentieStaan()
    {
        var service = new EmailReplyPolicyService();
        var odataError = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 503 };
        var graph = new FakeEmailGraphService { ExceptionToThrowOnSendReply = odataError };
        var persistence = new RecordingEmailPersistenceService();

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 302,
            email: new InkomendBericht { MessageId = "m4c", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst);
        persistence.VerzendPogingWissingen.Should().BeEmpty();
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 302 && u.Status == EmailStatus.Review);
    }

    /// <summary>
    /// Een tweede poll ná een onbekende uitkomst mag géén tweede antwoord versturen. Dit bewijst de
    /// end-to-end-garantie van #1133 door de precieze database-stand na te bootsen die
    /// <see cref="SendFout_OnbekendeUitkomstDoorTimeOut_LaatIntentieStaan_EnZetReview"/> achterlaat en
    /// die door <c>EmailIdempotentie.Bepaal</c> (aangeroepen door <c>EmailProcessorFunction</c> vóór
    /// elke verwerking) te laten beoordelen — zie EmailIdempotentieTests voor de volledige matrix.
    /// </summary>
    [Fact]
    public void NaOnbekendeUitkomst_ZietDeVolgendePollEenOnbesliteVerzendPoging_EnStuurtNietOpnieuw()
    {
        var standNaOnbekendeUitkomst = new EmailVerwerkingStand(
            VerwerkingId: 301,
            Status: nameof(EmailStatus.Review),
            Pogingen: 1,
            AntwoordVerstuurd: false,
            VerzendPogingOnbeslist: true);

        EmailIdempotentie.Bepaal(standNaOnbekendeUitkomst)
            .Should().Be(VerwerkingsBesluit.OnbeslistNaVerzendPoging);
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
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle();
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m5");
    }

    // ── Verzendintentie vóór het versturen (#716) ──

    /// <summary>
    /// De kern van #716: de grens die een tweede antwoord moet voorkomen werd pas geschreven nádat de
    /// mail de deur uit was. Wordt de invocatie daartussen hard afgebroken, dan is er geen enkel spoor
    /// en stuurt de volgende poll een tweede antwoord. De intentie moet dus vóór het versturen staan.
    /// </summary>
    [Fact]
    public async Task VerzendIntentie_WordtVastgelegdVoordatErVerstuurdWordt()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();
        var intentieBijVersturen = -1;

        // De fake legt vast hoeveel intenties er stonden op het moment van versturen. Nul zou betekenen
        // dat de intentie ná het versturen wordt geschreven — precies de bug.
        graph.OnSendReply = () => intentieBijVersturen = persistence.VerzendPogingMarkeringen.Count;

        var result = await service.HandelReplyFlowAfAsync(
            verwerkingId: 500,
            email: new InkomendBericht { MessageId = "m6", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        intentieBijVersturen.Should().Be(1, "de verzendintentie moet vastliggen vóórdat de mail de deur uit gaat");

        // Bij een geslaagde verzending wordt de intentie niet gewist: het antwoord is vastgelegd, dus
        // IsBeantwoord is de grens en er is niets onbeslist meer.
        persistence.VerzendPogingWissingen.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().ContainSingle(u => u.VerwerkingId == 500);
    }

    /// <summary>
    /// Kan de intentie niet worden vastgelegd, dan mag er niet verstuurd worden: zonder die grens is
    /// een dubbel antwoord mogelijk. Een poging uitstellen naar de volgende poll is het lichtere kwaad.
    /// </summary>
    [Fact]
    public async Task VerzendIntentieMislukt_ErWordtNietVerstuurd()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService { ThrowOnMarkeerVerzendPoging = true };

        var actie = () => service.HandelReplyFlowAfAsync(
            verwerkingId: 600,
            email: new InkomendBericht { MessageId = "m7", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        await actie.Should().ThrowAsync<InvalidOperationException>();
        graph.SentReplies.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    /// <summary>
    /// In review mode gaat er niets de deur uit, dus hoort er ook geen verzendintentie te staan —
    /// anders zou een volgende poll dat als "uitkomst onbekend" lezen en het bericht onnodig blijven
    /// markeren als iets waar de coördinator naar moet kijken.
    /// </summary>
    [Fact]
    public async Task ReviewMode_LegtGeenVerzendIntentieVast()
    {
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        await service.HandelReplyFlowAfAsync(
            verwerkingId: 700,
            email: new InkomendBericht { MessageId = "m8", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        persistence.VerzendPogingMarkeringen.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
    }
}
