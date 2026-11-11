using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Planner.Endpoints.Http;
using System.Text;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public class EmailTestEndpointCoreRequestTests
{
    [Fact]
    public async Task LeesRequest_GeldigeJson_LeestDto()
    {
        var result = await EmailTestEndpointCore.LeesRequestAsync(Request("{\"body\":\"test\"}"));

        result.Fout.Should().BeNull();
        result.Dto!.Body.Should().Be("test");
    }

    [Fact]
    public async Task LeesRequest_OngeldigeJson_GeeftVeilige400()
    {
        var result = await EmailTestEndpointCore.LeesRequestAsync(Request("{kapot"));

        result.Dto.Should().BeNull();
        result.Fout.Should().BeOfType<BadRequestObjectResult>();
        Newtonsoft.Json.JsonConvert.SerializeObject(result.Fout).Should().NotContain("kapot");
    }

    [Fact]
    public async Task LeesRequest_BovenBytegrens_GeeftVaste413()
    {
        var content = "{" + new string('x', BoundedRequestBodyReader.DefaultJsonMaxBytes) + "}";

        var result = await EmailTestEndpointCore.LeesRequestAsync(Request(content));

        result.Dto.Should().BeNull();
        result.Fout.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(413);
        Newtonsoft.Json.JsonConvert.SerializeObject(result.Fout).Should().NotContain("x");
    }

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }
}
