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
        Func<string, string, IChatClient> createClient)
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

        services.AddSingleton(createClient(modelName, apiKey));
        return true;
    }
}
