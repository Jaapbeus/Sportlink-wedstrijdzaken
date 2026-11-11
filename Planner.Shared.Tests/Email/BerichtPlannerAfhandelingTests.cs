using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class BerichtPlannerAfhandelingTests
{
    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck, "availability")]
    [InlineData(VerzoekType.HerplanVerzoek, "reschedule")]
    [InlineData(VerzoekType.TeamContactOpvragen, "contact")]
    public async Task ResolvedDecision_InvokesOnlySelectedBranch(VerzoekType type, string selected)
    {
        var calls = new List<string>();
        var response = await BerichtPlannerAfhandeling.VerwerkAsync(new() { Type = type },
            () => Record("availability"), () => Record("reschedule"), () => Record("contact"));

        Assert.Equal(selected, response);
        Assert.Equal([selected], calls);

        Task<string> Record(string branch)
        {
            calls.Add(branch);
            return Task.FromResult(branch);
        }
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    [InlineData(VerzoekType.Bevestiging)]
    [InlineData(VerzoekType.BuitenScope)]
    public async Task ManualDecision_IsTerminalWithoutInvokingAnyBranch(VerzoekType type)
    {
        var classification = new BerichtClassificatie { Type = type, TeamHandmatigBeoordelen = true };

        var response = await BerichtPlannerAfhandeling.VerwerkAsync(
            classification, Forbidden, Forbidden, Forbidden);

        Assert.True(classification.TeamNaamOnbevestigd);
        Assert.Contains("handmatigeTeamBeoordeling", response);
    }

    [Theory]
    [InlineData(VerzoekType.Bevestiging, "{\"status\":\"Bevestiging ontvangen\",\"opmerking\":\"Bevestigingen vereisen handmatige afhandeling door de coördinator\"}")]
    [InlineData(VerzoekType.BuitenScope, "{\"status\":\"Niet verwerkt\"}")]
    public async Task NonPlanningDecision_ReturnsExistingContractWithoutInvokingAnyBranch(VerzoekType type, string expected)
        => Assert.Equal(expected, await BerichtPlannerAfhandeling.VerwerkAsync(
            new() { Type = type }, Forbidden, Forbidden, Forbidden));

    private static Task<string> Forbidden() => throw new InvalidOperationException("No branch may be invoked");
}
