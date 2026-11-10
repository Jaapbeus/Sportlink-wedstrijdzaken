using AwesomeAssertions;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class AdminSettingsFieldContractTests
{
    [Fact]
    public void SqlServer_AllowsSportlinkDryRunForSettingsUpdates()
        => AdminSettingsFunction.AllowedFields.Should().Contain("SportlinkDryRun");
}
