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

    /// <summary>
    /// Herkent modelfamilies met reasoning (#1958) aan de modelnaam: <c>gpt-5*</c>, <c>gpt-6*</c> en de o-serie
    /// (<c>o1</c>, <c>o3</c>, <c>o4</c>). Zulke modellen weigeren een vaste temperatuur, ook als de beheerder
    /// <c>AiReasoningEffort</c> vergat te zetten. Alleen een vangnet: een geldige expliciete waarde blijft leidend.
    /// </summary>
    public static bool IsReasoningModel(string? modelnaam)
    {
        if (string.IsNullOrWhiteSpace(modelnaam)) return false;
        var naam = modelnaam.Trim();
        return naam.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || naam.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            || new[] { "o1", "o3", "o4" }.Any(o =>
                naam.StartsWith(o, StringComparison.OrdinalIgnoreCase)
                && (naam.Length == o.Length || naam[o.Length] == '-'));
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
