using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public class SyncLogEndpointCoreTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static SyncJobStatus Job(string status, int minutes = 0) => new(
        Guid.NewGuid(), status, -1, 3, Now.AddMinutes(minutes),
        status == "pending" ? null : Now.AddMinutes(minutes),
        status is "failed" or "succeeded" ? Now.AddMinutes(minutes + 1) : null,
        "SyncAlreadyRunningException private runtime text") { Attempt = status == "pending" ? 0 : 1 };
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task InvalidStartBody_DoesNotRecoverCreateOrEnqueue()
    {
        var result = await SyncLogEndpointCore.StartAsync("{invalid", 2026,
            () => throw new Exception("must not read"), _ => throw new Exception("must not read"),
            () => throw new Exception("must not recover"), (_, _) => throw new Exception("must not create"),
            _ => throw new Exception("must not enqueue"), _ => throw new Exception("must not compensate"),
            _ => throw new Exception("must not read phases"));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task StartWithActiveJob_KeepsItsWindowAndCurrentAttemptPhase()
    {
        var job = Job("running", -1);
        var recovered = false;
        var phase = new SyncLogEntry(job.Id, job.Attempt, "teams", Now, null, "running", null, null, null, null, null, null, null);
        var result = (ObjectResult)await SyncLogEndpointCore.StartAsync(null, 2026,
            () => Task.FromResult(25), _ => throw new Exception("must not read season start"),
            () => { recovered = true; return Task.CompletedTask; }, (from, to) =>
            {
                recovered.Should().BeTrue();
                (from, to).Should().Be((-1, 25));
                return Task.FromResult(new SyncJobCreation(job, false));
            }, _ => throw new Exception("must not enqueue"), _ => throw new Exception("must not compensate"),
            _ => Task.FromResult<IReadOnlyList<SyncLogEntry>>(new[] { phase }));
        var json = Json(result.Value!);
        json.GetProperty("weekOffsetTo").GetInt32().Should().Be(job.WeekOffsetTo);
        json.GetProperty("phase").GetString().Should().Be("teams");
        json.GetProperty("alreadyRunning").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SecondClick_ReusesJob_WithoutEnqueue()
    {
        var job = Job("running", -1);
        var result = (ObjectResult)await SyncLogEndpointCore.TriggerAsync(new(job, false),
            _ => throw new Exception("must not enqueue"), () => throw new Exception("must not fail"),
            () => Task.FromResult<string?>("teams"));
        result.StatusCode.Should().Be(202);
        var json = Json(result.Value!);
        json.GetProperty("jobId").GetGuid().Should().Be(job.Id);
        json.GetProperty("alreadyRunning").GetBoolean().Should().BeTrue();
        json.GetProperty("phase").GetString().Should().Be("teams");
    }

    [Fact]
    public async Task NewJob_EnqueuesOnce_AndLogFailureDoesNotFailTrigger()
    {
        var calls = 0;
        var result = (ObjectResult)await SyncLogEndpointCore.TriggerAsync(new(Job("pending"), true),
            _ => { calls++; return Task.CompletedTask; }, () => throw new Exception("unexpected"),
            () => throw new InvalidOperationException("private"));
        calls.Should().Be(1);
        result.StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task EnqueueFailure_IsRecorded_WithoutExceptionDisclosure()
    {
        var recorded = false;
        var result = (ObjectResult)await SyncLogEndpointCore.TriggerAsync(new(Job("pending"), true),
            _ => throw new InvalidOperationException("private"),
            () => { recorded = true; return Task.CompletedTask; }, () => Task.FromResult<string?>(null));
        recorded.Should().BeTrue();
        result.StatusCode.Should().Be(503);
        Json(result.Value!).ToString().Should().NotContain("private").And.NotContain("Exception");
    }

    [Fact]
    public async Task EnqueueAndCompensationFailure_ReturnsSafeUnavailableResponse()
    {
        var result = (ObjectResult)await SyncLogEndpointCore.TriggerAsync(new(Job("pending"), true),
            _ => throw new InvalidOperationException("private queue"),
            () => throw new InvalidOperationException("private database"), () => Task.FromResult<string?>(null));
        result.StatusCode.Should().Be(503);
        Json(result.Value!).ToString().Should().Contain("tijdelijk onzeker").And.NotContain("private");
    }

    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("succeeded", "partial")]
    public void PartialPhase_DoesNotHideFailedJob(string terminalStatus, string expected)
    {
        var job = Job(terminalStatus, -10);
        var entry = new SyncLogEntry(job.Id, 1, "teams", Now.AddMinutes(-10), Now.AddMinutes(-9),
            "partial", 3, 1, 0, 1, null, null, "partial_failure");
        var json = Json(SyncLogEndpointCore.Status(Now, "schedule", new[] { job }, new[] { entry }, null, Now, true));
        json.GetProperty("lastCompletedJob").GetProperty("status").GetString().Should().Be(expected);
    }

    [Fact]
    public void Status_SeparatesActiveAndCompleted_DoesNotExposeStoredError()
    {
        var active = Job("running", -1);
        var done = Job("succeeded", -10);
        var json = Json(SyncLogEndpointCore.Status(Now, "schedule", new[] { active, done }, [], null, Now, true));
        json.GetProperty("activeJob").GetProperty("id").GetGuid().Should().Be(active.Id);
        json.GetProperty("lastCompletedJob").GetProperty("id").GetGuid().Should().Be(done.Id);
        json.ToString().Should().NotContain("Exception").And.NotContain("private");
    }

    [Fact]
    public void HistoricalUnclaimedPending_DoesNotBlockActiveStatus_OrDisappearFromHistory()
    {
        var stale = Job("pending", -31);
        var json = Json(SyncLogEndpointCore.Status(null, "schedule", new[] { stale }, [], null, Now, true));
        json.GetProperty("activeJob").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("jobs").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void UnknownRequestedJob_DoesNotFallBackToDifferentJob()
    {
        var json = Json(SyncLogEndpointCore.Status(null, "schedule", new[] { Job("running") }, [], Guid.NewGuid(), Now, false));
        json.GetProperty("job").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("logAvailable").GetBoolean().Should().BeFalse();
    }
}
