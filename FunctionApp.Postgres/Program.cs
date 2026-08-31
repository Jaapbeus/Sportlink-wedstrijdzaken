using Azure.Identity;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

// #891: minimale host-bootstrap voor de Postgres-tier — bewust géén 1-op-1-kopie van
// FunctionApp/Program.cs' DI-registraties (AI, noodmail-throttle, monitoring): die horen bij
// functionaliteit die nog niet vertaald is. Sinds issue 888 vervolg (§43) staat hier wél de
// uitgaande e-mailregistratie, want AdminTeambegeleidingDoorsturen heeft die nodig.
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Graph-client met client credentials (application permissions).
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var graphAppCredential = Environment.GetEnvironmentVariable("GraphClientSecret");

// EgressGuard (#857): buiten productie blijft IEmailGraphService onvoorwaardelijk ongeregistreerd,
// ook als de Graph-secrets toevallig wél lokaal geconfigureerd zijn. Dezelfde "niet geregistreerd →
// endpoint meldt 503"-lijn als de SQL Server-tier: één centrale poort, geen tweede impliciete
// is-dit-geconfigureerd-check ergens in een handler.
if (!string.IsNullOrWhiteSpace(tenantId)
    && !string.IsNullOrWhiteSpace(clientId)
    && !string.IsNullOrWhiteSpace(graphAppCredential)
    && EgressGuard.ExternalIntegrationsAllowed())
{
    var credential = new ClientSecretCredential(tenantId, clientId, graphAppCredential);
    builder.Services.AddSingleton(new GraphServiceClient(credential));

    // IEmailGraphService alleen registreren als Graph zelf geconfigureerd is (#827) — anders zou
    // een resolutiepoging een GraphServiceClient-afhankelijkheid missen die er per ontwerp niet is.
    builder.Services.AddSingleton<IEmailGraphService>(sp =>
        new EmailGraphService(
            sp.GetRequiredService<GraphServiceClient>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<EmailGraphService>()));
}

builder.Build().Run();
