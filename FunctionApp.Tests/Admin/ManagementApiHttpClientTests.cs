using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class ManagementApiHttpClientTests
{
    [Fact]
    public void SqlServerPassesTheExistingOutboundTimeoutToAzureManagementClient()
    {
        using var client = AzureManagementHttpClientFactory.Create(AdminEndpoint.OutboundHttpTimeoutSeconds);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }
}
