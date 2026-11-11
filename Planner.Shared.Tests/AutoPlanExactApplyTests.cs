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

    [Fact]
    public void SportlinkDurationAndFullFieldTargetWorkWithoutAgeSettings()
    {
        var source = new AutoPlanApplySource(1, null, null, "ALLSTARS onbekend", null, 90);
        var choice = Choice(lane: null);

        var write = AutoPlanExactApply.Validate([choice], [source], Fields,
            new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, (int, int)>(), 15).Should().ContainSingle().Which;

        write.Veld.Should().Be("veld 101");
        write.Tijd.Should().Be("10:00");
    }

    [Fact]
    public void TargetLaneDeterminesFractionInsteadOfSourceLaneOrTeamSize()
    {
        var source = Source(field: "veld 101 A1", time: "09:00", team: "ALLSTARS JO13-1");
        var choice = Choice(oldField: source.Veld, oldTime: source.AanvangsTijd,
            lane: "A", targetTime: "10:00");

        var writes = Apply([choice], [source]);

        writes.Should().ContainSingle().Which.Veld.Should().Be("veld 101 A");
    }

    [Fact]
    public void UnknownUnassignedRowDoesNotBlockAssignedRows()
    {
        var unknown = Source(1, field: "veld 101 A1", time: "10:00", team: "ALLSTARS onbekend");
        var assigned = Source(2, team: "ALLSTARS JO13-1");
        var choices = new[]
        {
            Choice(1, oldField: unknown.Veld, oldTime: unknown.AanvangsTijd,
                targetField: null, lane: null, targetTime: null),
            Choice(2, lane: "B1", targetTime: "10:00")
        };
        var sources = new[] { unknown, assigned };

        Apply(choices, sources).Should().ContainSingle()
            .Which.WedstrijdCode.Should().Be(2);
    }

    [Theory]
    [InlineData("40001", null, true)]
    [InlineData(null, 1205, true)]
    [InlineData(null, 3960, true)]
    [InlineData("23505", null, false)]
    [InlineData(null, 2627, false)]
    public void OnlySerializationAndDeadlockErrorsAreApplyConflicts(
        string? postgresSqlState, int? sqlServerNumber, bool expected) =>
        AutoPlanApplyConflictException.IsTransactionConflict(postgresSqlState, sqlServerNumber)
            .Should().Be(expected);

    [Fact]
    public async Task PersistCommitsOnlyAfterEveryWriteMatchesOneSourceRow()
    {
        var writes = new[]
        {
            new AutoPlanApplyWrite(1, null, null, "veld 101 A1", "10:00"),
            new AutoPlanApplyWrite(2, null, null, "veld 101 A2", "10:00")
        };
        var persisted = new List<long>();
        var committed = false;

        var count = await AutoPlanExactApply.PersistAsync(writes, write =>
        {
            persisted.Add(write.WedstrijdCode);
            return Task.FromResult(1);
        }, () =>
        {
            committed = true;
            return Task.CompletedTask;
        });

        count.Should().Be(2);
        persisted.Should().Equal(1, 2);
        committed.Should().BeTrue();
    }

    [Fact]
    public async Task PersistDoesNotCommitWhenAnyWriteIsStale()
    {
        var writes = new[]
        {
            new AutoPlanApplyWrite(1, null, null, "veld 101 A1", "10:00"),
            new AutoPlanApplyWrite(2, null, null, "veld 101 A2", "10:00")
        };
        var persisted = new List<long>();
        var committed = false;

        var act = () => AutoPlanExactApply.PersistAsync(writes, write =>
        {
            persisted.Add(write.WedstrijdCode);
            return Task.FromResult(write.WedstrijdCode == 1 ? 1 : 0);
        }, () =>
        {
            committed = true;
            return Task.CompletedTask;
        });

        await act.Should().ThrowAsync<AutoPlanApplyConflictException>();
        persisted.Should().Equal(1, 2);
        committed.Should().BeFalse();
    }

    [Theory]
    [InlineData("10:00:30")]
    [InlineData("10:00:00")]
    [InlineData("10:00 AM")]
    [InlineData(" 10:00")]
    public void TargetMustHaveExactMinutePrecision(string time) =>
        ((Action)(() => Apply([Choice(targetTime: time)], [Source()])))
            .Should().Throw<AutoPlanApplyConflictException>();

    [Fact]
    public void UnchangedSourceRetainsSecondsAndIsNotWritten()
    {
        var source = Source(field: "veld 101 A2", time: "10:00:30");
        var choice = Choice(oldField: source.Veld, oldTime: source.AanvangsTijd,
            targetField: 101, lane: "A2", targetTime: "10:00");
        Apply([choice], [source]).Should().BeEmpty();
    }

    [Fact]
    public void UnassignedSourceRetainsSecondsAndIsNotWritten()
    {
        var source = Source(field: "veld 101 A2", time: "10:00:30");
        var choice = Choice(oldField: source.Veld, oldTime: source.AanvangsTijd,
            targetField: null, lane: null, targetTime: null);
        Apply([choice], [source]).Should().BeEmpty();
    }

    [Fact]
    public void FieldOrLaneChangeAtSameDisplayedMinuteRetainsSourceSeconds()
    {
        var source = Source(field: "veld 101 A2", time: "10:00:30");
        var choice = Choice(oldField: source.Veld, oldTime: source.AanvangsTijd,
            targetField: 101, lane: "A1", targetTime: "10:00");

        var write = Apply([choice], [source]).Should().ContainSingle().Which;
        write.Tijd.Should().Be("10:00:30");
    }

    [Fact]
    public void UnchangedSourceSecondsStillCountAgainstClosingTime()
    {
        var source = Source(field: "veld 101 A2", time: "21:00:30");
        var choice = Choice(oldField: source.Veld, oldTime: source.AanvangsTijd,
            targetField: null, lane: null, targetTime: null);
        ((Action)(() => Apply([choice], [source])))
            .Should().Throw<AutoPlanApplyConflictException>();
    }

    [Theory]
    [InlineData("X")]
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
