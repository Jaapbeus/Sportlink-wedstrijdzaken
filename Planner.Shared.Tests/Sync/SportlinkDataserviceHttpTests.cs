using System.Net;
using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>Begrensde herhaalpogingen voor dataservice-GET's (#1779).</summary>
public class SportlinkDataserviceHttpTests
{
    private sealed class ScriptHandler(params Func<HttpResponseMessage>[] antwoorden) : HttpMessageHandler
    {
        public int Aanroepen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Math.Min(Aanroepen, antwoorden.Length - 1);
            Aanroepen++;
            return Task.FromResult(antwoorden[index]());
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode s) => () => new HttpResponseMessage(s);

    private static async Task<(HttpResponseMessage? response, ScriptHandler handler, List<string> meldingen)> Run(
        params Func<HttpResponseMessage>[] antwoorden)
    {
        var handler = new ScriptHandler(antwoorden);
        var meldingen = new List<string>();
        using var client = new HttpClient(handler);
        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(
            client, "http://test/x", meldingen.Add, TimeSpan.Zero);
        return (response, handler, meldingen);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task TijdelijkeStatus_WordtHerhaaldTotSucces(HttpStatusCode tijdelijk)
    {
        var (response, handler, meldingen) = await Run(Status(tijdelijk), Status(HttpStatusCode.OK));

        response!.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Aanroepen.Should().Be(2);
        meldingen.Should().HaveCount(1);
    }

    [Fact]
    public async Task BlijvendeFout_StopptNaMaxPogingenEnGeeftLaatsteResponsTerug()
    {
        var (response, handler, _) = await Run(Status(HttpStatusCode.InternalServerError));

        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
        response!.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "de aanroeper beslist met EnsureSuccessStatusCode (#1081 blijft)");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PermanenteClientfout_WordtNooitHerhaald(HttpStatusCode status)
    {
        var (_, handler, meldingen) = await Run(Status(status));

        handler.Aanroepen.Should().Be(1);
        meldingen.Should().BeEmpty();
    }

    [Fact]
    public async Task NetwerkfoutDaarnaSucces_WordtHerhaald()
    {
        var handler = new ScriptHandler(
            () => throw new HttpRequestException("onbereikbaar"),
            Status(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", null, TimeSpan.Zero);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Aanroepen.Should().Be(2);
    }

    [Fact]
    public async Task NetwerkfoutBijLaatstePoging_WordtDoorgegooid()
    {
        var handler = new ScriptHandler(() => throw new HttpRequestException("onbereikbaar"));
        using var client = new HttpClient(handler);

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", null, TimeSpan.Zero);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
    }

    [Fact]
    public async Task TimeOut_WordtHerhaald()
    {
        var handler = new ScriptHandler(
            () => throw new TaskCanceledException("time-out"),
            Status(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", null, TimeSpan.Zero);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnnuleringDoorAanroeper_WordtNietHerhaald()
    {
        var handler = new ScriptHandler(() => throw new TaskCanceledException("geannuleerd"));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", null, TimeSpan.Zero, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Aanroepen.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Melding_BevatNooitDeUrl()
    {
        var (_, _, meldingen) = await Run(Status(HttpStatusCode.InternalServerError), Status(HttpStatusCode.OK));

        meldingen.Should().NotContain(m => m.Contains("http://") || m.Contains("clientId"));
    }
}
