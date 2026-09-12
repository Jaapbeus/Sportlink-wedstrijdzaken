using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using OpenAI.Chat;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Email;
using SportlinkFunction.Infrastructure;
using SportlinkFunction.Monitoring;
using SportlinkFunction.Sportlink;
using SportlinkFunction.TeamResolution;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Graph client met client credentials (application permissions)
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var graphAppCredential = Environment.GetEnvironmentVariable("GraphClientSecret");

// EgressGuard (#857): buiten productie blijft IEmailGraphService onvoorwaardelijk ongeregistreerd,
// ook als Graph-secrets toevallig wél lokaal geconfigureerd zijn — hetzelfde "niet geregistreerd →
// resolutie faalt expliciet"-gedrag als de bestaande "niet geconfigureerd"-tak hieronder.
if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(graphAppCredential)
    && EgressGuard.ExternalIntegrationsAllowed())
{
    var credential = new ClientSecretCredential(tenantId, clientId, graphAppCredential);
    builder.Services.AddSingleton(new GraphServiceClient(credential));

    // IEmailGraphService alleen registreren als Graph zelf geconfigureerd is (#827) — anders zou een
    // resolutiepoging een GraphServiceClient-afhankelijkheid missen die er per ontwerp niet is.
    builder.Services.AddSingleton<IEmailGraphService>(sp =>
        new EmailGraphService(
            sp.GetRequiredService<GraphServiceClient>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<EmailGraphService>()));
}

// IChatClient: provider-agnostische AI-abstractie (CLAUDE.md architectuurregel).
// Provider: OpenAI direct — geen Azure OpenAI.
// Modelnaam komt uit de app setting `AiModelName` zodat een model-upgrade geen code-wijziging
// vereist (zie docs/ARCHITECTUUR-AI-SERVICES.md). Niet uit dbo.AppSettings: de DI-registratie
// loopt bij host-start, vóór de eerste databaseverbinding. (#604)
// EgressGuard (#857): buiten productie blijft IChatClient onvoorwaardelijk ongeregistreerd, ook als
// OpenAiApiKey toevallig wél lokaal geconfigureerd is.
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

    // Forced-choice teamdisambiguatie (#697). Alleen geregistreerd als er een AI-provider is:
    // zonder OpenAiApiKey blijft TeamResolver puur deterministisch en geeft bij ambiguïteit
    // gewoon de kandidatenlijst terug in plaats van te kiezen.
    builder.Services.AddSingleton<ITeamDisambiguator, TeamDisambiguationAiService>();
}

// Sportlink Club API client (#991, #998): read-only Match API + token-refresh per functionele rol.
// EgressGuard (#857): eigen if-blok, losgekoppeld van de OpenAiApiKey-check hierboven — dit is een
// onafhankelijke uitgaande integratie en hoort niet toevallig aan AI-configuratie vast te zitten.
if (EgressGuard.ExternalIntegrationsAllowed())
{
    builder.Services.AddSingleton<ISportlinkClubTokenStore, SportlinkClubAppSettingsTokenStore>();
    builder.Services.AddHttpClient<ISportlinkClubClient, SportlinkClubClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}

builder.Services.AddSingleton<ITeamCandidateRepository, TeamCandidateRepository>();
builder.Services.AddSingleton<ITeamResolver, TeamResolver>();
builder.Services.AddSingleton<TeamAliasLearningService>();
builder.Services.AddSingleton<TeamlijstGereedheid>();

// Repository-boundary voor de e-mailverwerking (#827): vóór deze registratie omzeilden
// EmailProcessorFunction, EmailPersistenceService en AdminTeambegeleidingFunction de DI-container
// met eigen `new`-instantiaties. SqlEmailPersistenceRepository is stateless (elke methode
// opent/sluit een eigen SqlConnection) — Singleton is concurrency-veilig.
// Factory-registraties (i.p.v. AddSingleton<TService, TImplementation>()): beide implementatietypen
// zijn bewust `internal` — de generieke registratievorm vereist een publieke constructor, wat hier
// de encapsulatie van dit interne subsysteem zou doorbreken.
builder.Services.AddSingleton<IEmailPersistenceRepository>(_ => new SqlEmailPersistenceRepository());
builder.Services.AddSingleton<IEmailPersistenceService>(sp =>
    new EmailPersistenceService(sp.GetRequiredService<IEmailPersistenceRepository>()));

// Persistente noodmail-throttle (#831): Azure Table Storage via de bestaande AzureWebJobsStorage-
// opslagaccount — geen nieuwe Azure-resource, wél cold-start-bestendig (i.t.t. de vorige static
// bool/DateTime-velden op EmailProcessorFunction). AzureWebJobsStorage is sowieso vereist voor de
// Functions-host zelf, dus onvoorwaardelijk registreren.
builder.Services.AddSingleton<INoodmailThrottleStore>(sp =>
{
    var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException(
            "AzureWebJobsStorage ontbreekt — vereist voor de Azure Functions-host zelf.");
    return new TableStorageNoodmailThrottleStore(
        storageConnectionString,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<TableStorageNoodmailThrottleStore>());
});

// Onafhankelijke database-uitvalmonitor (#831) — leest de SQL-databasestatus via de Azure Management
// API. Alleen actief als AzureSubscriptionId/AzureResourceGroupName/AzureSqlServerName/
// AzureSqlDatabaseName zijn geconfigureerd (gecheckt in DatabaseUitvalMonitorFunction zelf); de reader
// is onvoorwaardelijk registreerbaar omdat hij pas bij aanroep iets doet.
builder.Services.AddSingleton<IDatabaseStatusReader, ArmDatabaseStatusReader>();

// Audit-logging voor Sportlink-mutaties (#991, #998) — SQL Server tier
builder.Services.AddSingleton<ISportlinkMutationAuditService, SqlSportlinkMutationAuditService>();

// CORS voor lokale dev: geconfigureerd via Host.CORS in local.settings.json (Functions host-level).
// In productie (Azure SWA) is CORS niet nodig: SWA proxying houdt alles op dezelfde origin.

builder.Build().Run();