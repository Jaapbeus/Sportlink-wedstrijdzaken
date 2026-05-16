using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Graph client met client credentials (application permissions)
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var clientSecret = Environment.GetEnvironmentVariable("GraphClientSecret");

if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
{
    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    builder.Services.AddSingleton(new GraphServiceClient(credential));
}

// CORS voor lokale dev: geconfigureerd via Host.CORS in local.settings.json (Functions host-level).
// In productie (Azure SWA) is CORS niet nodig: SWA proxying houdt alles op dezelfde origin.

builder.Build().Run();