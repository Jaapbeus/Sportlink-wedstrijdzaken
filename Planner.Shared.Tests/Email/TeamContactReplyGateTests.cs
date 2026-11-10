using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.Tests.Email.TestDoubles;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class TeamContactReplyGateTests
{
    private static ReplyTierHaken Haken(
        Func<int, TeamContactDoorstuurVerzoek, Task<TeamContactDoorstuurUitkomst>>? doorstuur = null)
        => new(_ => Task.FromResult<string?>(null), _ => Task.FromResult<EmailBijlage?>(null), _ => null, doorstuur);

    [Fact]
    public async Task AcceptedForward_RunsAfterGatesBeforeReplyIntent_AndBuildsTemplateAfterAcceptance()
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        TeamContactDoorstuurVerzoek? request = null;
        var events = new List<string>();
        var haken = Haken((id, value) =>
        {
            request = value;
            events.Add("forward");
            persistence.VerzendPogingMarkeringen.Should().BeEmpty();
            return Task.FromResult(TeamContactDoorstuurUitkomst.Doorgestuurd);
        });
        graph.OnSendReply = () => events.Add("reply");

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            20,
            new ReplyBericht("m20", "vraagsteller@voorbeeld.test", "conv20", "Vraagsteller", "Mijn vraag"),
            new BerichtClassificatie { Type = VerzoekType.TeamContactOpvragen, TeamNaam = "JO13-1" },
            "{}", false, null, graph, persistence,
            () =>
            {
                events.Add("template");
                return Task.FromResult(("subject", "accepted-template"));
            }, s => s, haken, NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        events.Should().Equal("forward", "template", "reply");
        request.Should().Be(new TeamContactDoorstuurVerzoek("JO13-1", "vraagsteller@voorbeeld.test", "Vraagsteller", "Mijn vraag"));
        graph.SentReplies.Should().ContainSingle().Which.Body.Should().Be("accepted-template");
        persistence.VerzendPogingMarkeringen.Should().ContainSingle().Which.Should().Be(20);
    }

    [Theory]
    [InlineData(TeamContactDoorstuurUitkomst.NietDoorgestuurd)]
    [InlineData(TeamContactDoorstuurUitkomst.Onbekend)]
    public async Task UnconfirmedForward_GoesToReviewWithoutCustomerReplyOrFalseTemplate(TeamContactDoorstuurUitkomst outcome)
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        var templateBuilt = false;

        var result = await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            21, new ReplyBericht("m21", "vraagsteller@voorbeeld.test", null),
            new BerichtClassificatie { Type = VerzoekType.TeamContactOpvragen, TeamNaam = "JO13-1" },
            "{}", false, null, graph, persistence,
            () =>
            {
                templateBuilt = true;
                return Task.FromResult(("subject", "Uw vraag is doorgestuurd"));
            }, s => s, Haken((_, _) => Task.FromResult(outcome)), NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        templateBuilt.Should().BeFalse();
        persistence.VerzendPogingMarkeringen.Should().BeEmpty();
        persistence.AntwoordUpdates.Should().BeEmpty();
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle().Which.AntwoordEmail
            .Should().Contain("nog niet doorgestuurd").And.Contain("coördinator beoordeelt");
        graph.SentReplies.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle().Which.Categories.Should().Contain(EmailCategorieLabels.GeenAiAntwoord);
    }

    [Fact]
    public async Task ReviewMode_DoesNotRunForwardHookAndUsesTruthfulProposal()
    {
        var graph = new FakeReplyGraph();
        var persistence = new RecordingReplyPersistentie();
        var forwardCalled = false;
        var templateCalled = false;

        await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            22, new ReplyBericht("m22", "vraagsteller@voorbeeld.test", null),
            new BerichtClassificatie { Type = VerzoekType.TeamContactOpvragen, TeamNaam = "JO13-1" },
            "{}", true, null, graph, persistence,
            () =>
            {
                templateCalled = true;
                return Task.FromResult(("subject", "Uw vraag is doorgestuurd"));
            }, s => s, Haken((_, _) =>
            {
                forwardCalled = true;
                return Task.FromResult(TeamContactDoorstuurUitkomst.Doorgestuurd);
            }), NullLogger.Instance);

        forwardCalled.Should().BeFalse();
        templateCalled.Should().BeFalse();
        persistence.VoorgesteldeAntwoorden.Should().ContainSingle().Which.AntwoordEmail.Should()
            .Contain("nog niet doorgestuurd");
        graph.SentReplies.Should().BeEmpty();
    }
}
