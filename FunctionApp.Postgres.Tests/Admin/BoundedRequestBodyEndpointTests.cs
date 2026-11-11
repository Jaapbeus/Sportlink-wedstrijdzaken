using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Http;
using System.Text;
using Xunit;

namespace FunctionApp.Postgres.Tests.Admin;

public class BoundedRequestBodyEndpointTests
{
    [Theory]
    [InlineData(0, StatusCodes.Status400BadRequest)]
    [InlineData(1, StatusCodes.Status400BadRequest)]
    [InlineData(2, StatusCodes.Status413PayloadTooLarge)]
    public async Task OngeldigeBody_BereiktGeenEndpointCallback(int soort, int verwachteStatus)
    {
        var bytes = soort switch
        {
            0 => Encoding.UTF8.GetBytes("{broken"),
            1 => new byte[] { 0xC3, 0x28 },
            _ => new byte[BoundedRequestBodyReader.DefaultJsonMaxBytes + 1]
        };
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        var endpointCallbacks = 0;

        var result = await BoundedRequestBodyReader.ExecuteJsonAsync<Dictionary<string, string>>(
            context.Request, _ =>
            {
                endpointCallbacks++;
                return Task.FromResult<IActionResult>(new OkResult());
            });

        (result as ObjectResult)!.StatusCode.Should().Be(verwachteStatus);
        endpointCallbacks.Should().Be(0);
    }
}
