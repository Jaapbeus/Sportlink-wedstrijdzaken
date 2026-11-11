using AwesomeAssertions;
using FunctionApp.Postgres.Admin;
using Planner.Shared.Infrastructure;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public class ManagementApiHttpClientTests
{
    [Fact]
    public void PostgresPassesTheExistingOutboundTimeoutToAzureManagementClient()
    {
        using var client = AzureManagementHttpClientFactory.Create(AdminEndpoint.OutboundHttpTimeoutSeconds);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }
}
