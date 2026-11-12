using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

public class ReasoningChatClientTests
{
    private sealed class OptiesVastleggendeClient : IChatClient
    {
        public ChatOptions? Ontvangen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Ontvangen = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Ontvangen = options;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task GetResponseAsync_ZetReasoningInspanningEnLaatTemperatuurWeg()
    {
        var inner = new OptiesVastleggendeClient();
        var client = new ReasoningChatClient(inner, ReasoningEffort.None);
        var aanroeperOpties = new ChatOptions { Temperature = 0.1f, ResponseFormat = ChatResponseFormat.Json };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], aanroeperOpties);

        inner.Ontvangen.Should().NotBeNull();
        inner.Ontvangen!.Temperature.Should().BeNull();
        inner.Ontvangen.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
        inner.Ontvangen.ResponseFormat.Should().Be(ChatResponseFormat.Json, "JSON-uitvoer blijft behouden");
    }

    [Fact]
    public async Task GetResponseAsync_WijzigtDeOptiesVanDeAanroeperNiet()
    {
        var inner = new OptiesVastleggendeClient();
        var client = new ReasoningChatClient(inner, ReasoningEffort.Low);
        var aanroeperOpties = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], aanroeperOpties);

        aanroeperOpties.Temperature.Should().Be(0.2f);
        aanroeperOpties.Reasoning.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_ZonderOpties_StuurtAlleenDeReasoningInspanning()
    {
        var inner = new OptiesVastleggendeClient();
        var client = new ReasoningChatClient(inner, ReasoningEffort.None);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        inner.Ontvangen!.Temperature.Should().BeNull();
        inner.Ontvangen.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PastDezelfdeOptiesToe()
    {
        var inner = new OptiesVastleggendeClient();
        var client = new ReasoningChatClient(inner, ReasoningEffort.None);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Temperature = 0.1f })) { }

        inner.Ontvangen!.Temperature.Should().BeNull();
        inner.Ontvangen.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("NONE", ReasoningEffort.None)]
    [InlineData(" low ", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("extra-high", ReasoningEffort.ExtraHigh)]
    public void TryParse_GeldigeWaarde_GeeftDeInspanning(string waarde, ReasoningEffort verwacht)
    {
        ReasoningChatClient.TryParse(waarde, out var inspanning).Should().BeTrue();
        inspanning.Should().Be(verwacht);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("onzin")]
    [InlineData("99")]
    public void TryParse_LeegOfOnbekend_GeeftFalse(string? waarde)
    {
        ReasoningChatClient.TryParse(waarde, out _).Should().BeFalse();
    }

    [Fact]
    public void TryRegister_MetGeldigeInspanning_RegistreertHetFilter()
    {
        var services = new ServiceCollection();

        AiClientRegistration.TryRegister(
            services, "synthetic-test-key", "synthetic-test-model", true,
            (_, _) => new OptiesVastleggendeClient(), "none").Should().BeTrue();

        services.Single(d => d.ServiceType == typeof(IChatClient)).ImplementationInstance
            .Should().BeOfType<ReasoningChatClient>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("onzin")]
    public void TryRegister_ZonderGeldigeInspanning_LaatDeClientOngemoeid(string? inspanning)
    {
        var services = new ServiceCollection();

        AiClientRegistration.TryRegister(
            services, "synthetic-test-key", "synthetic-test-model", true,
            (_, _) => new OptiesVastleggendeClient(), inspanning).Should().BeTrue();

        services.Single(d => d.ServiceType == typeof(IChatClient)).ImplementationInstance
            .Should().BeOfType<OptiesVastleggendeClient>();
    }
}
