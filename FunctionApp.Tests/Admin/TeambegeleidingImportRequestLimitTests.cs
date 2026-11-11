using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Planner.Endpoints.Http;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class TeambegeleidingImportRequestLimitTests
{
    [SqlServerFact]
    public async Task CsvExactlyFiveMiB_ReachesParserAndReturnsBadRequest()
    {
        var result = await InvokeImportAsync(new string('a', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [SqlServerFact]
    public async Task CsvOneAsciiByteOverFiveMiB_ReturnsGenericPayloadTooLarge()
    {
        var csv = new string('a', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes + 1);

        var result = await InvokeImportAsync(csv);

        AssertGenericPayloadTooLarge(result);
    }

    [SqlServerFact]
    public async Task CsvUtf8BytesOverFiveMiB_ReturnsGenericPayloadTooLarge()
    {
        var csv = new string('é', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes / 2) + "a";

        Encoding.UTF8.GetByteCount(csv).Should().Be(TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes + 1);
        var result = await InvokeImportAsync(csv);

        AssertGenericPayloadTooLarge(result);
    }

    private static async Task<IActionResult> InvokeImportAsync(string csv)
    {
        var json = JsonConvert.SerializeObject(new { CsvContent = csv, Bestandsnaam = "synthetisch.csv" });
        var requestContext = new DefaultHttpContext();
        var request = requestContext.Request;
        request.ContentType = "application/json";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        request.ContentLength = request.Body.Length;
        request.Headers["X-MS-CLIENT-PRINCIPAL"] = Principal("admin");
        request.Headers["X-Club-Code"] = "ALLSTARS";

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var functionContext = new Mock<FunctionContext>();
        functionContext.Setup(context => context.InstanceServices).Returns(provider);

        return await AdminTeambegeleidingFunction.Import(request, functionContext.Object);
    }

    private static string Principal(string role)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { claims = new[] { new { typ = "roles", val = role } } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static void AssertGenericPayloadTooLarge(IActionResult result)
    {
        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        JsonConvert.SerializeObject(response.Value).Should().Be("{\"error\":\"Request body is te groot.\"}");
    }
}
