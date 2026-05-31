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
// Provider: OpenAI gpt-4o-mini direct — geen Azure OpenAI.
var openAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey");
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    builder.Services.AddSingleton<IChatClient>(
        new ChatClient("gpt-4o-mini", new System.ClientModel.ApiKeyCredential(openAiApiKey))
            .AsIChatClient());
}

// CORS voor lokale dev: geconfigureerd via Host.CORS in local.settings.json (Functions host-level).
// In productie (Azure SWA) is CORS niet nodig: SWA proxying houdt alles op dezelfde origin.

builder.Build().Run();