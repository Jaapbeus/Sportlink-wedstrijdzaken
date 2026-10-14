using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.Tests.Email.TestDoubles;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// De tierhaken van de gedeelde reply-afhandeling (#1760): begeleiding-BCC en KNVB-kalenderbijlage bij
/// "verzet zonder datum" (#561/#1141) zijn fail-safe en worden alleen opgehaald als de classificatie dat vraagt.
/// </summary>
public class ReplyTierHakenTests
{
    private static BerichtClassificatie VerzetZonderDatum(string? regio = "Zuid") => new()
    {
        Type = VerzoekType.HerplanVerzoek, TeamNaam = "JO13-1", VoegKnvbPdfBijlageToe = true, KnvbBijlageRegio = regio
    };

    private static Task<ReplyVerwerkingUitkomst> Draai(
        BerichtClassificatie classificatie, FakeReplyGraph graph, ReplyTierHaken haken)
        => EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 9, email: new ReplyBericht("m9", "afzender@voorbeeld.test", "conv-9"), classificatie: classificatie,
            plannerResponseJson: "{}", reviewMode: false, reviewRecipient: null, graphService: graph,
            persistentie: new RecordingReplyPersistentie(), bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s, haken: haken, log: NullLogger.Instance);

    [Fact]
    public async Task VerzetZonderDatum_ZetBegeleidingInBcc_EnVoegKnvbBijlageToe()
    {
        var pdf = new EmailBijlage("kalender.pdf", [1, 2, 3], "application/pdf");
        string? gezochtTeam = null, gevraagdeRegio = null;
        var haken = TestHaken.Maak(
            begeleiding: t => { gezochtTeam = t; return Task.FromResult<string?>("trainer@voorbeeld.nl"); },
            bijlage: r => { gevraagdeRegio = r; return Task.FromResult<EmailBijlage?>(pdf); });
        var graph = new FakeReplyGraph();

        (await Draai(VerzetZonderDatum(), graph, haken)).Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);

        var verstuurd = graph.SentReplies.Should().ContainSingle().Subject;
        verstuurd.Bcc.Should().Equal("trainer@voorbeeld.nl");
        verstuurd.Bijlage.Should().BeSameAs(pdf);
        gezochtTeam.Should().Be("JO13-1");
        gevraagdeRegio.Should().Be("Zuid");
    }

    [Fact]
    public async Task ZonderVerzetZonderDatum_WordenDeHakenNietAangeroepen()
    {
        var aangeroepen = false;
        var haken = TestHaken.Maak(
            begeleiding: _ => { aangeroepen = true; return Task.FromResult<string?>("x@voorbeeld.nl"); },
            bijlage: _ => { aangeroepen = true; return Task.FromResult<EmailBijlage?>(null); });
        var graph = new FakeReplyGraph();

        await Draai(new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek }, graph, haken);

        aangeroepen.Should().BeFalse();
        graph.SentReplies.Should().ContainSingle().Which.Should().Match<(string To, string Subject, string Body, string? ConversationId, IReadOnlyList<string>? Bcc, EmailBijlage? Bijlage)>(
            r => r.Bcc == null && r.Bijlage == null);
    }

    [Fact]
    public async Task BegeleidingZonderAdres_VerstuurtZonderBcc()
    {
        var graph = new FakeReplyGraph();

        await Draai(VerzetZonderDatum(regio: null), graph, TestHaken.Maak(begeleiding: _ => Task.FromResult<string?>("  ")));

        graph.SentReplies.Should().ContainSingle().Which.Bcc.Should().BeNull();
    }

    [Fact]
    public async Task BegeleidingOphalenMislukt_VerstuurtGewoonZonderBcc_MaarMetBijlage()
    {
        var pdf = new EmailBijlage("kalender.pdf", [1], "application/pdf");
        var graph = new FakeReplyGraph();

        var uitkomst = await Draai(VerzetZonderDatum(), graph, TestHaken.Maak(
            begeleiding: _ => throw new InvalidOperationException("database weg"),
            bijlage: _ => Task.FromResult<EmailBijlage?>(pdf)));

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        var verstuurd = graph.SentReplies.Should().ContainSingle().Subject;
        verstuurd.Bcc.Should().BeNull();
        verstuurd.Bijlage.Should().BeSameAs(pdf);
    }

    [Fact]
    public async Task ZonderRegio_WordtGeenBijlageOpgehaald()
    {
        var bijlageOpgehaald = false;
        var graph = new FakeReplyGraph();

        await Draai(VerzetZonderDatum(regio: " "), graph, TestHaken.Maak(
            bijlage: _ => { bijlageOpgehaald = true; return Task.FromResult<EmailBijlage?>(null); }));

        bijlageOpgehaald.Should().BeFalse();
    }
}
