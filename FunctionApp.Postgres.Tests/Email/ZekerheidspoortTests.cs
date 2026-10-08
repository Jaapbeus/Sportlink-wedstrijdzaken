using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp.Tests/Email/ZekerheidspoortTests.cs</c> (#1568 deel D, review M6),
/// met fakes en zonder database via de <see cref="IReplyPersistentie"/>-naad: een onzeker antwoord gaat niet naar
/// de afzender maar ter review; een zeker antwoord blijft automatisch gaan.
/// </summary>
public class ZekerheidspoortTests
{
    private const string Afzender = "afzender@voorbeeld.test";

    private static TraceBuilder OnbekendTeamTrace()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Onopgelost", 0, null, null)
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"teamOnbekend\":true}");

    private static TraceBuilder HerkendTeamTrace()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Alias", 1.0, null, "JO13-2")
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"beschikbaar\":false}");

    private static async Task<(ReplyVerwerkingUitkomst uitkomst, FakeEmailGraphService graph, RecordingReplyPersistentie db)>
        Draai(TraceBuilder? trace, bool poortActief, bool reviewMode = false, string? reviewRecipient = null,
            string plannerJson = "{\"beschikbaar\":false}")
    {
        var graph = new FakeEmailGraphService();
        var db = new RecordingReplyPersistentie();
        var uitkomst = await new EmailReplyPolicyService(_ => db).HandelReplyFlowAfAsync(
            connectionString: "niet-gebruikt-fake-persistentie",
            verwerkingId: 7,
            email: new InkomendBericht { MessageId = "m7", Afzender = Afzender, Onderwerp = "Test", ConversationId = "conv-7" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: plannerJson,
            reviewMode: reviewMode,
            reviewRecipient: reviewRecipient,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance,
            trace: trace,
            zekerheidspoortActief: poortActief);
        return (uitkomst, graph, db);
    }

    [Fact]
    public async Task Onzeker_PoortAan_StuurtNietNaarAfzender_ZetReview_EnBewaartVoorstel()
    {
        var trace = OnbekendTeamTrace();

        var (uitkomst, graph, db) = await Draai(trace, poortActief: true);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().BeEmpty();
        db.VoorgesteldeAntwoorden.Should().ContainSingle(v => v.VerwerkingId == 7 && v.AntwoordEmail == "voorgestelde-body");
        // Geen verzendintentie en geen "verstuurd"-registratie: de mail is niet beantwoord.
        db.AntwoordUpdates.Should().BeEmpty();
        db.VerzendPogingMarkeringen.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m7");
        trace.Stappen.Should().ContainSingle(s => s.Code == TraceCodes.Zekerheidspoort && s.Uitkomst.StartsWith("Tegengehouden"));
    }

    [Fact]
    public async Task Onzeker_MetReviewRecipient_KrijgtVoorstelMetKopBovenaan_AfzenderNiets()
    {
        var (_, graph, _) = await Draai(OnbekendTeamTrace(), poortActief: true, reviewRecipient: "reviewer@voorbeeld.test");

        var verstuurd = graph.SentReplies.Should().ContainSingle().Subject;
        verstuurd.To.Should().Be("reviewer@voorbeeld.test");
        verstuurd.Body.Should().StartWith("LET OP: dit antwoord is NIET naar de afzender verstuurd");
        verstuurd.Body.Should().EndWith("voorgestelde-body");
        graph.SentReplies.Should().NotContain(r => r.To == Afzender);
    }

    [Fact]
    public async Task Onzeker_PoortUit_VerstuurtZoalsVoorheen()
    {
        var (uitkomst, graph, db) = await Draai(OnbekendTeamTrace(), poortActief: false);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == Afzender);
        db.AntwoordUpdates.Should().ContainSingle(u => u.VerwerkingId == 7);
    }

    [Fact]
    public async Task Zeker_PoortAan_VerstuurtAutomatisch()
    {
        var (uitkomst, graph, db) = await Draai(HerkendTeamTrace(), poortActief: true);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == Afzender);
        db.AntwoordUpdates.Should().ContainSingle(u => u.VerwerkingId == 7);
    }

    /// <summary>Hetzelfde bericht: vóór de alias onzeker (Review), erna zeker (automatisch antwoord).</summary>
    [Fact]
    public async Task NaAliasAanmaken_WordtDezelfdeMailZeker_EnGaatAutomatischDoor()
    {
        var voor = await Draai(OnbekendTeamTrace(), poortActief: true);
        var na = await Draai(HerkendTeamTrace(), poortActief: true);

        voor.uitkomst.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        voor.graph.SentReplies.Should().BeEmpty();
        na.uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        na.graph.SentReplies.Should().ContainSingle(r => r.To == Afzender);
    }

    [Fact]
    public async Task ReviewMode_BlijftOngewijzigd_OokBijOnzeker()
    {
        var trace = OnbekendTeamTrace();

        var (uitkomst, graph, db) = await Draai(trace, poortActief: true, reviewMode: true);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().BeEmpty();
        db.VoorgesteldeAntwoorden.Should().ContainSingle();
        trace.Stappen.Should().NotContain(s => s.Code == TraceCodes.Zekerheidspoort);
    }

    [Fact]
    public async Task ZonderTrace_GedraagtZichAlsVoorheen()
    {
        var (uitkomst, graph, _) = await Draai(null, poortActief: true);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == Afzender);
    }
}
