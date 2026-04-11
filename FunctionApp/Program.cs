using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Application Insights wordt automatisch geconfigureerd via host.json
// en de APPINSIGHTS_INSTRUMENTATIONKEY app setting in Azure

// Graph client met client credentials (application permissions)
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var clientSecret = Environment.GetEnvironmentVariable("GraphClientSecret");

if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
{
    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    builder.Services.AddSingleton(new GraphServiceClient(credential));
}

builder.Build().Run();