using AwesomeAssertions;
using FunctionApp.Postgres.Admin;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public class AdminSettingsFieldContractTests
{
    [Fact]
    public void Postgres_AllowsSportlinkDryRunForSettingsUpdates()
        => AdminSettingsFunction.AllowedFields.Should().Contain("SportlinkDryRun");
}
