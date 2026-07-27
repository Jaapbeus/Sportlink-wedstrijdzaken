using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public class TeamDisambiguationAiServiceTests
{
    private static readonly TeamCandidate[] TweeKandidaten =
    [
        new(9, "TESTCLUB O13-1", "JO13"),
        new(10, "TESTCLUB MO13-1", "MO13"),
    ];

    private static TeamDisambiguationAiService Service(string antwoord)
        => new(new FakeChatClient(antwoord), NullLogger<TeamDisambiguationAiService>.Instance);

    [Fact]
    public async Task KiesAsync_GeldigeKeuze_GeeftBijbehorendTeamId()
    {
        var resultaat = await Service("""{"keuze": 2, "reden": "meisjesteam"}""")
            .KiesAsync("13-1", TweeKandidaten);

        resultaat.Should().Be(10);
    }

    [Fact]
    public async Task KiesAsync_ModelKiestNull_GeeftNull()
    {
        var resultaat = await Service("""{"keuze": null, "reden": "te onzeker"}""")
            .KiesAsync("13-1", TweeKandidaten);

        resultaat.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"keuze": 0}""")]
    [InlineData("""{"keuze": 3}""")]
    [InlineData("""{"keuze": 99}""")]
    public async Task KiesAsync_KeuzeBuitenDeLijst_WordtGenegeerd(string antwoord)
    {
        // Het model kan een nummer buiten de aangeboden lijst teruggeven; dat mag nooit
        // tot een willekeurig TeamId leiden.
        var resultaat = await Service(antwoord).KiesAsync("13-1", TweeKandidaten);

        resultaat.Should().BeNull();
    }

    [Theory]
    [InlineData("geen json")]
    [InlineData("")]
    [InlineData("""{"iets": "anders"}""")]
    public async Task KiesAsync_OnbruikbaarAntwoord_GeeftNullZonderExceptie(string antwoord)
    {
        var resultaat = await Service(antwoord).KiesAsync("13-1", TweeKandidaten);

        resultaat.Should().BeNull();
    }

    [Fact]
    public async Task KiesAsync_EenKandidaat_KiestDirectZonderAiCall()
    {
        var fake = new FakeChatClient("""{"keuze": 1}""");
        var service = new TeamDisambiguationAiService(fake, NullLogger<TeamDisambiguationAiService>.Instance);

        var resultaat = await service.KiesAsync("JO13-1", [TweeKandidaten[0]]);

        resultaat.Should().Be(9);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task KiesAsync_GeenKandidaten_GeeftNullZonderAiCall()
    {
        var fake = new FakeChatClient("""{"keuze": 1}""");
        var service = new TeamDisambiguationAiService(fake, NullLogger<TeamDisambiguationAiService>.Instance);

        var resultaat = await service.KiesAsync("JO13-1", []);

        resultaat.Should().BeNull();
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task KiesAsync_TeVeelKandidaten_SlaatDisambiguatieOver()
    {
        // Bij "JO13" zonder teamnummer bij een grote club is de tekst te vaag; dan is
        // terugvragen aan de afzender correcter dan een AI-gok.
        var veel = Enumerable.Range(1, 9).Select(i => new TeamCandidate(i, $"TESTCLUB O13-{i}", "JO13")).ToArray();
        var fake = new FakeChatClient("""{"keuze": 1}""");
        var service = new TeamDisambiguationAiService(fake, NullLogger<TeamDisambiguationAiService>.Instance);

        var resultaat = await service.KiesAsync("JO13", veel);

        resultaat.Should().BeNull();
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task KiesAsync_PromptBevatAlleKandidatenGenummerd()
    {
        var fake = new FakeChatClient("""{"keuze": 1}""");
        var service = new TeamDisambiguationAiService(fake, NullLogger<TeamDisambiguationAiService>.Instance);

        await service.KiesAsync("13-1", TweeKandidaten);

        fake.LaatstePrompt.Should().Contain("1. TESTCLUB O13-1");
        fake.LaatstePrompt.Should().Contain("2. TESTCLUB MO13-1");
        fake.LaatstePrompt.Should().Contain("13-1");
    }

    private sealed class FakeChatClient(string antwoord) : IChatClient
    {
        public int AantalAanroepen { get; private set; }
        public string LaatstePrompt { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            AantalAanroepen++;
            LaatstePrompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, antwoord)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
