using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using System.Net;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Beperkt logcontract van de veldplanner-keten (Codex-review #1917, ronde 1): de timer en de Planning loggen alleen het
/// uitzonderingstype, nooit het uitzonderingsobject. Een <c>HttpRequestException</c> of <c>TaskCanceledException</c> kan in
/// zijn bericht of binnenste uitzondering een host of querystring met de publieke <c>clientId</c> dragen (AGENTS.md: log nooit
/// een request-URL). Alle invoer is synthetisch; de sentinel staat voor zo'n waarde.
/// </summary>
public class SportlinkClubLogContractTests
{
    private const string Rol = "test-planner";
    private const string Sentinel = "sentinel-clientid-123";

    public enum Fout { TokenEndpoint, Netwerk, Timeout, Onverwacht, KapotteJson, OnverwachteVorm }

    /// <summary>De locatielijst (accommodatie → facility) hoort bij het timerpad (Codex-review #1917, ronde 2).</summary>
    [Theory]
    [InlineData(Fout.TokenEndpoint)]
    [InlineData(Fout.Netwerk)]
    [InlineData(Fout.Timeout)]
    [InlineData(Fout.Onverwacht)]
    [InlineData(Fout.KapotteJson)]
    [InlineData(Fout.OnverwachteVorm)]
    public async Task GetClubMatchPickListsAsync_LogtGeenUitzonderingsobject_EnGeenAntwoordinhoud(Fout fout)
    {
        var logger = new VastleggendeLogger();
        var client = MaakClient(fout, logger);

        await client.GetClubMatchPickListsAsync(Rol);

        Controleer(logger, fout);
    }

    [Theory]
    [InlineData(Fout.TokenEndpoint)]
    [InlineData(Fout.Netwerk)]
    [InlineData(Fout.Timeout)]
    [InlineData(Fout.KapotteJson)]
    public async Task GetVeldplannerAsync_LogtGeenUitzonderingsobject_EnGeenSentinel(Fout fout)
    {
        var logger = new VastleggendeLogger();
        var client = MaakClient(fout, logger);

        await client.GetVeldplannerAsync(Rol, "facility-1", new DateOnly(2030, 1, 5));

        Controleer(logger, fout);
    }

    [Theory]
    [InlineData(Fout.TokenEndpoint)]
    [InlineData(Fout.Netwerk)]
    [InlineData(Fout.Timeout)]
    [InlineData(Fout.Onverwacht)]
    [InlineData(Fout.KapotteJson)]
    public async Task GetMatchAsync_LogtGeenUitzonderingsobject_EnGeenSentinel(Fout fout)
    {
        var logger = new VastleggendeLogger();
        var client = MaakClient(fout, logger);

        await client.GetMatchAsync(Rol, "M000000001");

        Controleer(logger, fout);
    }

    private static void Controleer(VastleggendeLogger logger, Fout fout)
    {
        logger.Regels.Should().NotBeEmpty($"de fout ({fout}) moet gelogd worden zodat een storing zichtbaar blijft");
        foreach (var regel in logger.Regels)
        {
            regel.Uitzondering.Should().BeNull("alleen het uitzonderingstype hoort in het log, niet het object met zijn binnenste uitzonderingen");
            regel.Bericht.Should().NotContain(Sentinel);
        }
    }

    private static SportlinkClubClient MaakClient(Fout fout, ILogger<SportlinkClubClient> logger)
        => new(new HttpClient(new FoutHandler(fout)), new LosseTokenStore(), logger, isDryRun: () => false);

    /// <summary>Token-endpoint geeft een geldig token, tenzij het scenario daar zelf de fout legt; elke andere aanroep faalt.</summary>
    private sealed class FoutHandler(Fout fout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isToken = request.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com", StringComparison.Ordinal) == true;
            if (isToken && fout != Fout.TokenEndpoint)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"fictief","expires_in":3600,"refresh_token":"fictief-nieuw","token_type":"Bearer"}""")
                });

            return fout switch
            {
                Fout.KapotteJson => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{ \"{Sentinel}\": [") }),
                Fout.OnverwachteVorm => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{ \"{Sentinel}\": 1 }}") }),
                Fout.Timeout => throw new TaskCanceledException($"timeout naar host {Sentinel}", new TimeoutException(Sentinel)),
                Fout.Onverwacht => throw new InvalidOperationException($"onverwacht {Sentinel}", new InvalidOperationException(Sentinel)),
                _ => throw new HttpRequestException($"netwerkfout naar https://host/?clientId={Sentinel}", new IOException(Sentinel)),
            };
        }
    }

    private sealed class LosseTokenStore : ISportlinkClubTokenStore
    {
        public string? LeesRefreshToken(string functioneleRol) => "fictief-refresh";
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed record LogRegel(string Bericht, Exception? Uitzondering);

    /// <summary>Legt bericht én uitzonderingsobject vast; het gedeelde <c>TestLogger</c> bewaart alleen het bericht.</summary>
    internal sealed class VastleggendeLogger : ILogger<SportlinkClubClient>
    {
        public List<LogRegel> Regels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Regels.Add(new LogRegel(formatter(state, null), exception));
    }
}
