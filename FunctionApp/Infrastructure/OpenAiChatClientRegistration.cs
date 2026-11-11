using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Chat;
using Planner.Shared.Infrastructure;

namespace SportlinkFunction.AiComposition;

/// <summary>Shared host composition for the provider-backed chat client.</summary>
public static class OpenAiChatClientRegistration
{
    public static void Register(IServiceCollection services, string? apiKey, string? modelName, bool externalAllowed)
    {
        AiClientRegistration.TryRegister(
            services,
            apiKey,
            modelName,
            externalAllowed,
            static (configuredModel, configuredKey) =>
                new ChatClient(configuredModel, new System.ClientModel.ApiKeyCredential(configuredKey)).AsIChatClient());
    }
}
