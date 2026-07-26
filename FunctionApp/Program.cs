using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;
using OpenAI.Chat;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Graph client met client credentials (application permissions)
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var graphAppCredential = Environment.GetEnvironmentVariable("GraphClientSecret");

if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(graphAppCredential))
{
    var credential = new ClientSecretCredential(tenantId, clientId, graphAppCredential);
    builder.Services.AddSingleton(new GraphServiceClient(credential));
}

// IChatClient: provider-agnostische AI-abstractie (CLAUDE.md architectuurregel).
// Provider: OpenAI direct — geen Azure OpenAI.
// Modelnaam komt uit de app setting `AiModelName` zodat een model-upgrade geen code-wijziging
// vereist (zie docs/ARCHITECTUUR-AI-SERVICES.md). Niet uit dbo.AppSettings: de DI-registratie
// loopt bij host-start, vóór de eerste databaseverbinding. (#604)
var openAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey");
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    // Fallback is puur een provider-model-identifier — geen club-specifieke waarde, dus toegestaan.
    const string defaultAiModelName = "gpt-4o-mini";
    var aiModelName = Environment.GetEnvironmentVariable("AiModelName");
    if (string.IsNullOrWhiteSpace(aiModelName)) aiModelName = defaultAiModelName;

    builder.Services.AddSingleton<IChatClient>(
        new ChatClient(aiModelName, new System.ClientModel.ApiKeyCredential(openAiApiKey))
            .AsIChatClient());
}

// CORS voor lokale dev: geconfigureerd via Host.CORS in local.settings.json (Functions host-level).
// In productie (Azure SWA) is CORS niet nodig: SWA proxying houdt alles op dezelfde origin.

builder.Build().Run();