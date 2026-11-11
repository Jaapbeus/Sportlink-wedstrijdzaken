using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.AutoPlan;
using Planner.Shared;
using Xunit;

namespace Planner.Endpoints.Tests.AutoPlan;

public sealed class AutoPlanToepassenEndpointCoreTests
{
    [Fact]
    public async Task ApplyResultIsReturnedAsOk()
    {
        var result = await AutoPlanToepassenEndpointCore.VerwerkAsync(
            () => Task.FromResult(new { bijgewerkt = 2 }), NullLogger.Instance);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task ConflictAndForbiddenExceptionsMapToTheirHttpStatuses()
    {
        var conflict = await AutoPlanToepassenEndpointCore.VerwerkAsync<object>(
            () => throw new AutoPlanApplyConflictException(), NullLogger.Instance);
        var forbidden = await AutoPlanToepassenEndpointCore.VerwerkAsync<object>(
            () => throw new AutoPlanApplyForbiddenException(), NullLogger.Instance);

        conflict.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        forbidden.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TierForbiddenPathsReturnTheSame403Contract()
    {
        var postgresPath = await AutoPlanToepassenEndpointCore.VerwerkAsync<object>(
            () => throw new AutoPlanApplyForbiddenException(), NullLogger.Instance);
        var sqlServerPath = AutoPlanToepassenEndpointCore.Verboden();

        var postgresResult = postgresPath.Should().BeOfType<ObjectResult>().Subject;
        postgresResult.StatusCode.Should().Be(403);
        sqlServerPath.StatusCode.Should().Be(403);
        var contract = new
        {
            error = new AutoPlanApplyForbiddenException().Message
        };
        postgresResult.Value.Should().BeEquivalentTo(contract);
        sqlServerPath.Value.Should().BeEquivalentTo(contract);
    }

    [Fact]
    public async Task InvalidOperationIsBadRequestOnlyWhenRequestedByTheTier()
    {
        var postgres = await AutoPlanToepassenEndpointCore.VerwerkAsync<object>(
            () => throw new InvalidOperationException("invalid request"), NullLogger.Instance,
            invalidOperationIsBadRequest: true);

        postgres.Should().BeOfType<BadRequestObjectResult>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AutoPlanToepassenEndpointCore.VerwerkAsync<object>(
                () => throw new InvalidOperationException("unexpected"), NullLogger.Instance));
    }
}
