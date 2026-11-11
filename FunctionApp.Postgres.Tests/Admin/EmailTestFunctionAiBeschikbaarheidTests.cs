using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FunctionApp.Postgres.Tests.Admin;

public class EmailTestFunctionAiBeschikbaarheidTests
{
    [Fact]
    public void ZonderChatClient_GeeftVeilige503ZonderConfiguratiewaarden()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.Setup(c => c.InstanceServices).Returns(services);

        var result = FunctionApp.Postgres.Admin.EmailTestFunction.ControleerAiBeschikbaar(
            context.Object, NullLogger.Instance);

        var response = result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        response.StatusCode.Should().Be(503);
        var json = JsonSerializer.SerializeToElement(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("error").GetString().Should().Be(global::Planner.Endpoints.Admin.EmailTestEndpointCore.AiNietBeschikbaarMelding);
        json.GetProperty("aiBeschikbaar").GetBoolean().Should().BeFalse();
        json.ToString().Should().NotContain("AiModelName").And.NotContain("OpenAiApiKey");
    }
}
