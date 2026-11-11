using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.AutoPlan;
using Planner.Shared;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1888: deterministic coverage of the SQL Server deadlock mapping. T-SQL cannot raise error 1205
/// itself, so the real-deadlock integration test depends on the database engine; these tests inject
/// the <see cref="SqlException"/> directly into the exact-apply conflict seam.
/// </summary>
public class AutoPlanExactApplyConflictMappingTests
{
    [Theory]
    [InlineData(1205)]
    [InlineData(3960)]
    public async Task TransactionConflictBecomesSafeConflict(int number)
    {
        var sqlError = CreateSqlException(number, "deadlock detail with server internals");

        var act = () => AutoPlanService.MapTransactionConflictAsync<AutoPlanToepassenResponse>(
            () => throw sqlError);

        var thrown = await act.Should().ThrowAsync<AutoPlanApplyConflictException>();
        thrown.Which.Message.Should().Be(AutoPlanApplyConflictException.SafeMessage);
        thrown.Which.InnerException.Should().BeNull("SQL Server details must not reach the caller");
    }

    [Fact]
    public async Task DeadlockReachesTheCallerAsSafe409()
    {
        var sqlError = CreateSqlException(1205, "deadlock detail with server internals");

        var result = await AutoPlanToepassenEndpointCore.VerwerkAsync(
            () => AutoPlanService.MapTransactionConflictAsync<AutoPlanToepassenResponse>(() => throw sqlError),
            NullLogger.Instance);

        var conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        conflict.Value.Should().BeEquivalentTo(new { error = AutoPlanApplyConflictException.SafeMessage });
    }

    [Fact]
    public async Task OtherSqlErrorsAreNotMappedToConflict()
    {
        var sqlError = CreateSqlException(2627, "unique key violation");

        var act = () => AutoPlanService.MapTransactionConflictAsync<AutoPlanToepassenResponse>(
            () => throw sqlError);

        (await act.Should().ThrowAsync<SqlException>()).Which.Should().BeSameAs(sqlError);
    }

    [Fact]
    public async Task SuccessfulApplyPassesThroughUnchanged()
    {
        var response = new AutoPlanToepassenResponse { Bijgewerkt = 2 };

        (await AutoPlanService.MapTransactionConflictAsync(() => Task.FromResult(response)))
            .Should().BeSameAs(response);
    }

    [Fact]
    public void InjectedSqlExceptionCarriesTheRequestedNumber() =>
        CreateSqlException(1205, "deadlock").Number.Should().Be(1205,
            "the reflection helper must build a real SqlException, otherwise the mapping tests prove nothing");

    /// <summary>
    /// SqlException has no public constructor; build one the way SqlClient does internally. A
    /// SqlClient upgrade that changes these internals fails loudly here instead of passing vacuously.
    /// </summary>
    private static SqlException CreateSqlException(int number, string message)
    {
        const BindingFlags nonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        var errorCtor = typeof(SqlError).GetConstructors(nonPublic)
            .OrderByDescending(c => c.GetParameters().Length).First();
        var errorArgs = errorCtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => number,
            "errorMessage" => message,
            _ => p.ParameterType == typeof(string) ? "test"
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
        }).ToArray();
        var error = (SqlError)errorCtor.Invoke(errorArgs);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", nonPublic)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethod("CreateException",
            BindingFlags.NonPublic | BindingFlags.Static, [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [errors, "16.0"])!;
    }
}
