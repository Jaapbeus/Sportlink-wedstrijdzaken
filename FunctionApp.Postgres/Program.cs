using Azure.Identity;
using FunctionApp.Postgres;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Sportlink;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using OpenAI.Chat;
using Planner.Shared.Integrations.SportlinkClub;

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

// IChatClient: provider-agnostische AI-abstractie (CLAUDE.md architectuurregel), uitsluitend nodig
// voor FeedbackFunction (#966) op deze tier — BerichtAiService/teamdisambiguatie zijn hier nog niet
// vertaald (#889). Zelfde patroon als FunctionApp/Program.cs: EgressGuard (#857) houdt dit
// onvoorwaardelijk ongeregistreerd buiten productie, ook als OpenAiApiKey lokaal geconfigureerd is.
var openAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey");
if (!string.IsNullOrWhiteSpace(openAiApiKey) && EgressGuard.ExternalIntegrationsAllowed())
{
    // Fallback is puur een provider-model-identifier — geen club-specifieke waarde, dus toegestaan.
    const string defaultAiModelName = "gpt-4o-mini";
    var aiModelName = Environment.GetEnvironmentVariable("AiModelName");
    if (string.IsNullOrWhiteSpace(aiModelName)) aiModelName = defaultAiModelName;

    builder.Services.AddSingleton<IChatClient>(
        new ChatClient(aiModelName, new System.ClientModel.ApiKeyCredential(openAiApiKey))
            .AsIChatClient());
}

// Sportlink Club API client (#991, #998): read-only Match API + token-refresh per functionele rol.
// EgressGuard (#857): eigen if-blok, losgekoppeld van de OpenAiApiKey-check hierboven — dit is een
// onafhankelijke uitgaande integratie en hoort niet toevallig aan AI-configuratie vast te zitten.
// Tokenopslag: PostgresSportlinkClubTokenStore (eigen DB-tabel) i.p.v. SportlinkClubAppSettingsTokenStore
// (Function App-instelling via de Azure Management API, #998) — besloten voor de Postgres-tier
// (enige live tier) omdat dat geen nieuwe Azure-resource of Managed Identity vereist. Zie
// docs/SPORTLINK-WEB-EXTENSION.md §4.3.
if (EgressGuard.ExternalIntegrationsAllowed())
{
    builder.Services.AddSingleton<ISportlinkClubTokenStore>(sp =>
        new PostgresSportlinkClubTokenStore(
            PostgresDatabaseConfig.ConnectionString,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresSportlinkClubTokenStore>()));
    builder.Services.AddHttpClient<ISportlinkClubClient, SportlinkClubClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}

// Audit-logging voor Sportlink-mutaties (#991, #998) — Postgres tier
builder.Services.AddSingleton<ISportlinkMutationAuditService, PostgresSportlinkMutationAuditService>();

builder.Build().Run();
