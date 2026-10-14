using AwesomeAssertions;
using Planner.Shared.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;
using Planner.Shared.Email;

namespace Planner.Shared.Tests.Email;

public class EmailReplyAfhandelingTests
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        var buildCalled = false;

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 42,
            email: new ReplyBericht("m1", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () =>
            {
                buildCalled = true;
                return Task.FromResult(("subj", "voorgestelde-body"));
            },
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        buildCalled.Should().BeTrue();
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 42 && v.AntwoordEmail == "voorgestelde-body");

        // Review mode blokkeert post naar de originele afzender — dat blijft ongewijzigd. Zonder
        // reviewRecipient wordt er dus helemaal niets verstuurd.
        graph.SentReplies.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();

        // Er ís een AI-antwoord — het wacht alleen op beoordeling. Het label "Geen AI antwoord"
        // hoort hier dus niet (#1244); als gelezen markeren blijft wel.
        graph.CategoryUpdates.Should().BeEmpty();
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 44,
            email: new ReplyBericht("m1c", "afzender@voorbeeld.test", "conv-44"),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: "reviewer@voorbeeld.test",
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 44 && v.AntwoordEmail == "voorgestelde-body");

        // De testmail gaat naar reviewRecipient, nooit naar de originele afzender.
        graph.SentReplies.Should().ContainSingle(r =>
            r.To == "reviewer@voorbeeld.test" && r.Subject == "subj" && r.ConversationId == "conv-44");
        persistence.AntwoordUpdates.Should().BeEmpty();
        graph.CategoryUpdates.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1c");
    }

    /// <summary>
    /// Een mislukte reviewmail mag de opslag niet blokkeren — het voorstel blijft dan alsnog in de
    /// database te vinden (#801). Er is een voorstel opgebouwd, dus er wordt niet gelabeld (#1244).
    /// </summary>
    [Fact]
    public async Task ReviewMode_ReviewmailMislukt_SlaatVoorstelTochOp()
    {
        var graph = new FakeReplyGraph { ThrowOnSendReply = true };
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 45,
            email: new ReplyBericht("m1d", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: "reviewer@voorbeeld.test",
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v =>
            v.VerwerkingId == 45 && v.AntwoordEmail == "voorgestelde-body");
        graph.CategoryUpdates.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1d");
    }

    /// <summary>
    /// #1608: planning is mogelijk op de gevraagde datum → de policy onderdrukt het antwoord (#572). Dat is
    /// een beleidsbesluit, geen AI-uitval: ook in review mode status GeenAntwoordNodig en label
    /// "Handmatige planning", precies als <see cref="GeenReplyNodig_ZetStatusEnHandmatigePlanningLabel"/>.
    /// Vóór #1608 werd dit "Review" + "Geen AI antwoord" — een leeg item in de reviewwachtrij.
    /// </summary>
    [Fact]
    public async Task ReviewMode_ZonderAntwoordNodig_ZetGeenAntwoordNodig_EnHandmatigePlanningLabel()
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        var buildCalled = false;

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 43,
            email: new ReplyBericht("m1b", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: JsonConvert.SerializeObject(new { Beschikbaar = true }),
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () =>
            {
                buildCalled = true;
                return Task.FromResult(("subj", "body"));
            },
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        buildCalled.Should().BeFalse();
        persistence.VoorgesteldeAntwoorden.Should().BeEmpty();
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 43 && u.Status == EmailStatus.GeenAntwoordNodig && u.GeextraheerdeData == null);
        graph.SentReplies.Should().BeEmpty();

        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Handmatige planning"));
        graph.CategoryUpdates.Should().NotContain(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1b");
    }

    [Fact]
    public async Task GeenReplyNodig_ZetStatusEnHandmatigePlanningLabel()
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 100,
            email: new ReplyBericht("m2", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: JsonConvert.SerializeObject(new { Beschikbaar = true }),
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 200,
            email: new ReplyBericht("m3", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
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
        var odataError = new GraphStatusFout(400);
        var graph = new FakeReplyGraph { ExceptionToThrowOnSendReply = odataError };
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 300,
            email: new ReplyBericht("m4", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            haken: TestHaken.Standaard,
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
        var graph = new FakeReplyGraph { ExceptionToThrowOnSendReply = new TaskCanceledException() };
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 301,
            email: new ReplyBericht("m4b", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            haken: TestHaken.Standaard,
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
        var odataError = new GraphStatusFout(503);
        var graph = new FakeReplyGraph { ExceptionToThrowOnSendReply = odataError };
        var persistence = new RecordingReplyPersistentie();

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 302,
            email: new ReplyBericht("m4c", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst);
        persistence.VerzendPogingWissingen.Should().BeEmpty();
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 302 && u.Status == EmailStatus.Review);
    }

    /// <summary>
    /// Geen dubbel antwoord: als het antwoord verstuurd is maar het vastleggen in de database faalt,
    /// moet het bericht alsnog als gelezen worden gemarkeerd. Bleef het ongelezen, dan zou de
    /// volgende poll de afzender een tweede antwoord sturen. (#712)
    /// </summary>
    [Fact]
    public async Task VastleggenAntwoordMislukt_MarkeertBerichtAlsnogGelezen()
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie { ThrowOnUpdateAntwoordVerstuurd = true };

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 400,
            email: new ReplyBericht("m5", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        var intentieBijVersturen = -1;

        // De fake legt vast hoeveel intenties er stonden op het moment van versturen. Nul zou betekenen
        // dat de intentie ná het versturen wordt geschreven — precies de bug.
        graph.OnSendReply = () => intentieBijVersturen = persistence.VerzendPogingMarkeringen.Count;

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 500,
            email: new ReplyBericht("m6", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie { ThrowOnMarkeerVerzendPoging = true };

        var actie = () => EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 600,
            email: new ReplyBericht("m7", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
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
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();

        await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 700,
            email: new ReplyBericht("m8", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            persistentie: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        persistence.VerzendPogingMarkeringen.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
    }
}
