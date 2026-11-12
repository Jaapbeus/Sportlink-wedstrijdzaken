using Microsoft.Extensions.AI;

namespace Planner.Shared.Infrastructure;

/// <summary>
/// Past elke chataanroep aan voor modellen met reasoning (#1756): zet de gewenste reasoning-inspanning en laat
/// de vaste <c>Temperature</c> van de aanroeper weg. Zo'n model weigert sampling-parameters zodra de
/// reasoning-inspanning niet <c>None</c> is, en de aanroepers (classificatie, feedback) sturen altijd een
/// temperatuur mee. De aanroepers blijven ongewijzigd; alleen de registratie bepaalt of dit filter actief is.
/// </summary>
public sealed class ReasoningChatClient : DelegatingChatClient
{
    private readonly ReasoningEffort _inspanning;

    public ReasoningChatClient(IChatClient innerClient, ReasoningEffort inspanning) : base(innerClient)
    {
        _inspanning = inspanning;
    }

    /// <summary>
    /// Leest de app setting <c>AiReasoningEffort</c> (none, low, medium, high, extra-high). Leeg of onbekend geeft
    /// <c>false</c>: dan blijft het gedrag zoals bij een model zonder reasoning.
    /// </summary>
    public static bool TryParse(string? waarde, out ReasoningEffort inspanning)
    {
        inspanning = default;
        if (string.IsNullOrWhiteSpace(waarde)) return false;
        return Enum.TryParse(waarde.Trim().Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out inspanning)
            && Enum.IsDefined(inspanning);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, Pas(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, Pas(options), cancellationToken);

    private ChatOptions Pas(ChatOptions? options)
    {
        var kopie = options?.Clone() ?? new ChatOptions();
        kopie.Temperature = null;
        kopie.Reasoning = new ReasoningOptions { Effort = _inspanning };
        return kopie;
    }
}
