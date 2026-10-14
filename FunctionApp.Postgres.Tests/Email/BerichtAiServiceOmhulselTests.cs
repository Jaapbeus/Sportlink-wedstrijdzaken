using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Omhulseltest (#1759): <see cref="BerichtAiService"/> op de Postgres-tier geeft zijn aanroepen door aan de
/// gedeelde <c>BerichtAiKern</c>. De promptlogica zelf wordt getest in <c>Planner.Shared.Tests</c>.
/// </summary>
public class BerichtAiServiceOmhulselTests
{
    private sealed class VasteClient(string antwoord) : IChatClient
    {
        public int Aanroepen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Aanroepen++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, antwoord)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task DetecteerCorrectie_GeeftDeGedeeldeKernUitkomstTerug()
    {
        var client = new VasteClient("""{"isCorrectie":true,"afgeleidType":"herplan_verzoek","samenvatting":"Bedoelde verplaatsen"}""");
        var service = new BerichtAiService(NullLogger<BerichtAiService>.Instance, client);

        var (isCorrectie, _, samenvatting) = await service.DetecteerCorrectieAsync("body", "onderwerp", "buiten_scope", null);

        isCorrectie.Should().BeTrue();
        samenvatting.Should().Be("Bedoelde verplaatsen");
        client.Aanroepen.Should().Be(1);
    }
}
