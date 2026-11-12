using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Autorisatie;
using Planner.Endpoints.Http;
using Planner.Shared.Autorisatie;
using Xunit;

namespace Planner.Endpoints.Tests.Autorisatie;

public class PlannerAanvragerEndpointCoreTests
{
    private sealed class BevestigDto : IBevestigAanvragerRequest
    {
        public string Datum { get; set; } = string.Empty;
        public string AanvangsTijd { get; set; } = string.Empty;
        public string? AangevraagdDoor { get; set; }
    }

    private sealed class HerplanDto : IHerplanAanvragerRequest
    {
        public long Wedstrijdcode { get; set; }
        public string GewensteAanvangsTijd { get; set; } = string.Empty;
        public string? AangevraagdDoor { get; set; }
    }

    private static HttpRequest Request(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context.Request;
    }

    [Fact]
    public async Task Bevestig_ReplacesBodyRequesterAndParsesDateTime()
    {
        var parsed = await PlannerAanvragerEndpointCore.ReadBevestigAsync<BevestigDto>(
            Request("""{"datum":"2026-11-12","aanvangsTijd":"10:30","aangevraagdDoor":"client"}"""),
            "server actor");

        parsed.Error.Should().BeNull();
        parsed.Value.AangevraagdDoor.Should().Be("server actor");
        parsed.Date.Should().Be(new DateOnly(2026, 11, 12));
        parsed.Time.Should().Be(new TimeOnly(10, 30));
    }

    [Fact]
    public async Task Herplan_RejectsMissingActorBeforePersistence()
    {
        var parsed = await PlannerAanvragerEndpointCore.ReadHerplanAsync<HerplanDto>(
            Request("""{"wedstrijdcode":123,"gewensteAanvangsTijd":"11:15","aangevraagdDoor":"client"}"""),
            null);

        parsed.Error.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        parsed.Value.AangevraagdDoor.Should().Be("client");
    }

    // #1811 + #1848: het aanvragerpad leest de body begrensd, net als de andere planner-endpoints.
    [Fact]
    public async Task Bevestig_TeGroteBody_Geeft413ZonderWaarde()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[BoundedRequestBodyReader.DefaultJsonMaxBytes + 1]);

        var parsed = await PlannerAanvragerEndpointCore.ReadBevestigAsync<BevestigDto>(context.Request, "server actor");

        parsed.Error.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(413);
        parsed.Value.Should().BeNull();
    }

    [Fact]
    public async Task Herplan_OngeldigeJson_Geeft400ZonderWaarde()
    {
        var parsed = await PlannerAanvragerEndpointCore.ReadHerplanAsync<HerplanDto>(
            Request("""{"wedstrijdcode":123,"""), "server actor");

        parsed.Error.Should().BeOfType<BadRequestObjectResult>();
        parsed.Value.Should().BeNull();
    }
}
