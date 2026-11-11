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
    public void RuntimeOnlyFlag_DoesNotAlterEitherSerializedContract()
    {
        var classification = new BerichtClassificatie { TeamHandmatigBeoordelen = true };
        Assert.DoesNotContain(nameof(BerichtClassificatie.TeamHandmatigBeoordelen), JsonConvert.SerializeObject(classification));
        Assert.DoesNotContain(nameof(BerichtClassificatie.TeamHandmatigBeoordelen), System.Text.Json.JsonSerializer.Serialize(classification));
    }
}
