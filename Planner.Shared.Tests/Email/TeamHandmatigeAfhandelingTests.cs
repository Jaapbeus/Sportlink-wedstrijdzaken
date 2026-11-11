using Newtonsoft.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.Tests.Email.TestDoubles;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class TeamHandmatigeAfhandelingTests
{
    [Fact]
    public async Task ManualOutcome_ReviewsAndLabelsWithoutTemplateOrSendIntent()
    {
        var graph = new FakeReplyGraph { ThrowOnSendReply = true };
        var persistence = new RecordingReplyPersistentie { ThrowOnMarkeerVerzendPoging = true };
        var outcome = await TeamHandmatigeAfhandeling.HandelOfVervolgAsync(
            new BerichtClassificatie { TeamHandmatigBeoordelen = true }, 1, "message", persistence, graph,
            () => throw new InvalidOperationException("Template, reply policy, BCC and sending must remain unreachable"), NullLogger.Instance);
        Assert.Equal(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord, outcome);
        Assert.Equal(EmailStatus.Review, Assert.Single(persistence.StatusUpdates).Status);
        Assert.Empty(persistence.VerzendPogingMarkeringen);
        Assert.Empty(persistence.VoorgesteldeAntwoorden);
        Assert.Empty(graph.SentReplies);
        Assert.Equal("message", Assert.Single(graph.MarkedAsReadIds));
        Assert.Contains("Handmatige planning", Assert.Single(graph.CategoryUpdates).Categories);
    }

    [Fact]
    public async Task TeamReviewAndSuppressedReply_EnsureSameManualPlanningCategory()
    {
        var teamReviewGraph = new FakeReplyGraph();
        await TeamHandmatigeAfhandeling.HandelAsync(
            1, "team-review", new RecordingReplyPersistentie(), teamReviewGraph);

        var suppressedReplyGraph = new FakeReplyGraph();
        await EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId: 2,
            email: new ReplyBericht("suppressed-reply", "afzender@voorbeeld.test", null),
            classificatie: new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            plannerResponseJson: JsonConvert.SerializeObject(new { Beschikbaar = true }),
            reviewMode: false,
            reviewRecipient: null,
            graphService: suppressedReplyGraph,
            persistentie: new RecordingReplyPersistentie(),
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            haken: TestHaken.Standaard,
            log: NullLogger.Instance);

        var teamReviewCategory = Assert.Single(teamReviewGraph.EnsuredCategories);
        var suppressedReplyCategory = Assert.Single(suppressedReplyGraph.EnsuredCategories);
        Assert.Equal(teamReviewCategory, suppressedReplyCategory);
        Assert.Equal("Handmatige planning", teamReviewCategory.Name);
        Assert.Equal("preset5", teamReviewCategory.ColorPreset);
    }

    [Fact]
    public void RuntimeOnlyFlag_DoesNotAlterEitherSerializedContract()
    {
        var classification = new BerichtClassificatie { TeamHandmatigBeoordelen = true };
        Assert.DoesNotContain(nameof(BerichtClassificatie.TeamHandmatigBeoordelen), JsonConvert.SerializeObject(classification));
        Assert.DoesNotContain(nameof(BerichtClassificatie.TeamHandmatigBeoordelen), System.Text.Json.JsonSerializer.Serialize(classification));
    }
}
