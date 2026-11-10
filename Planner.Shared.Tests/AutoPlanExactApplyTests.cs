using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

public class AutoPlanExactApplyTests
{
    private static readonly List<VeldInfo> Fields = [new() { VeldNummer = 101, VeldNaam = "veld 101" }];
    private static readonly Dictionary<string, Speeltijd> Speeltijden = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JO13"] = new() { Leeftijd = "JO13", Veldafmeting = .25m, WedstrijdTotaal = 60 },
        ["JO15"] = new() { Leeftijd = "JO15", Veldafmeting = .5m, WedstrijdTotaal = 60 }
    };

    private static AutoPlanApplySource Source(long id = 1, string? field = null, string? time = null,
        string team = "ALLSTARS JO13-1") => new(id, field, time, team, null, null);

    private static AutoPlanApplyChoice Choice(long id = 1, string? oldField = null, string? oldTime = null,
        int? targetField = 101, string? lane = "A2", string? targetTime = "10:00") =>
        new(id, oldField, oldTime, targetField, lane, targetTime);

    private static List<AutoPlanApplyWrite> Apply(
        IReadOnlyList<AutoPlanApplyChoice> choices, IReadOnlyList<AutoPlanApplySource> sources) =>
        AutoPlanExactApply.Validate(choices, sources, Fields, Speeltijden,
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase), 15);

    [Fact]
    public void ManualQuarterLaneIsPersistedExactly()
    {
        var writes = Apply([Choice()], [Source()]);
        writes.Should().ContainSingle().Which.Veld.Should().Be("veld 101 A2");
        writes[0].Tijd.Should().Be("10:00");
    }

    [Theory]
    [InlineData("X")]
    [InlineData("A")]
    [InlineData("")]
    public void InvalidQuarterLaneIsRejected(string lane) =>
        ((Action)(() => Apply([Choice(lane: lane)], [Source()])))
            .Should().Throw<AutoPlanApplyConflictException>();

    [Fact]
    public void HalfLaneConflictsWithQuarterInSamePhysicalLane()
    {
        var sources = new[] { Source(), Source(2, team: "ALLSTARS JO15-1") };
        var choices = new[] { Choice(lane: "A1"), Choice(2, lane: "A") };
        ((Action)(() => Apply(choices, sources))).Should().Throw<AutoPlanApplyConflictException>();
    }

    [Fact]
    public void StaleSourceLaneIsRejected() =>
        ((Action)(() => Apply([Choice(oldField: "veld 101 A1", oldTime: "10:00")],
            [Source(field: "veld 101 A2", time: "10:00")])))
            .Should().Throw<AutoPlanApplyConflictException>();

    [Fact]
    public void ChangedSourceSetAndDuplicateIdsAreRejected()
    {
        ((Action)(() => Apply([Choice()], [Source(), Source(2)])))
            .Should().Throw<AutoPlanApplyConflictException>();
        ((Action)(() => Apply([Choice(), Choice()], [Source(), Source(2)])))
            .Should().Throw<AutoPlanApplyConflictException>();
    }

    [Fact]
    public void UnassignedRowIsIncludedButNotWritten()
    {
        var choices = new[] { Choice(), Choice(2, targetField: null, lane: null, targetTime: null) };
        var sources = new[] { Source(), Source(2, team: "ALLSTARS JO15-1") };
        Apply(choices, sources).Should().ContainSingle();
    }

    [Fact]
    public void HalfNullTargetIsRejected() =>
        ((Action)(() => Apply([Choice(targetTime: null)], [Source()])))
            .Should().Throw<AutoPlanApplyConflictException>();

    [Fact]
    public void InvalidAssignedFieldIsRejectedRatherThanIgnored() =>
        ((Action)(() => Apply([Choice(targetField: 0)], [Source()])))
            .Should().Throw<AutoPlanApplyConflictException>();
}
