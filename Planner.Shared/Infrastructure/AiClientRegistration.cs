using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Planner.Shared.Infrastructure;

/// <summary>Registers AI only when all required deployment configuration is present.</summary>
public static class AiClientRegistration
{
    /// <summary>
    /// Registers the provider-agnostic client only when egress, key, and model are configured.
    /// The composition root owns provider construction; this method never logs configuration values.
    /// </summary>
    public static bool TryRegister(
        IServiceCollection services,
        string? apiKey,
        string? modelName,
        bool externalIntegrationsAllowed,
        Func<string, string, IChatClient> createClient,
        string? reasoningEffort = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createClient);

        if (string.IsNullOrWhiteSpace(apiKey) || !externalIntegrationsAllowed)
            return false;

        if (string.IsNullOrWhiteSpace(modelName))
        {
            Console.Error.WriteLine("AI niet geregistreerd: AiModelName ontbreekt; niet-AI-functies blijven beschikbaar.");
            return false;
        }

        var client = createClient(modelName, apiKey);

        // #1756: een model met reasoning weigert de vaste temperatuur van de aanroepers. Een geldige app setting
        // AiReasoningEffort activeert het filter. #1958: ontbreekt die bij een reasoning-model, dan geldt
        // ReasoningEffort.None, anders faalt elke aanroep met HTTP 400. Een model zonder reasoning blijft
        // ongewijzigd. De waarden zelf worden niet gelogd.
        if (ReasoningChatClient.TryParse(reasoningEffort, out var inspanning))
        {
            client = new ReasoningChatClient(client, inspanning);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
                Console.Error.WriteLine("AiReasoningEffort is geen geldige waarde en wordt genegeerd.");
            if (ReasoningChatClient.IsReasoningModel(modelName))
            {
                Console.Error.WriteLine("AiReasoningEffort ontbreekt voor een model met reasoning; standaard 'none' toegepast.");
                client = new ReasoningChatClient(client, ReasoningEffort.None);
            }
        }

        services.AddSingleton(client);
        return true;
    }
}
