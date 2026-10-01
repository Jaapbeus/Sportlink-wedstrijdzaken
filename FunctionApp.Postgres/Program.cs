using Azure.Identity;
using FunctionApp.Postgres;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Monitoring;
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
// FunctionApp/Program.cs' DI-registraties (AI, monitoring): die horen bij functionaliteit die nog
// niet vertaald is. Sinds issue 888 vervolg (§43) staat hier wél de uitgaande e-mailregistratie
// (AdminTeambegeleidingDoorsturen), en sinds #972 ook INoodmailThrottleStore
// (EmailProcessorFunction — de mailbox-getriggerde inkomende e-mailverwerking).
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
// Authentication and the encrypted persistent token store are registered per database tier.
// Credentials and the key setup are documented in docs/SPORTLINK-AUTOLOGIN.md.
PostgresSportlinkAuthenticationRegistration.Register(builder.Services);

// Audit-logging voor Sportlink-mutaties (#991, #998) — Postgres tier
builder.Services.AddSingleton<ISportlinkMutationAuditService, PostgresSportlinkMutationAuditService>();

// Persistente noodmail-throttle (#972, port van FunctionApp/Program.cs' gelijknamige
// registratie, #831 op de SQL Server-tier): Azure Table Storage via de bestaande
// AzureWebJobsStorage-opslagaccount — geen nieuwe Azure-resource. Onvoorwaardelijk registreren:
// AzureWebJobsStorage is sowieso vereist voor de Functions-host zelf, en anders dan
// IEmailGraphService/IChatClient gaat dit nooit naar een externe (niet-Microsoft) dienst, dus
// valt het niet onder EgressGuard (#857).
builder.Services.AddSingleton<INoodmailThrottleStore>(sp =>
{
    var storageVerbinding = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException(
            "AzureWebJobsStorage ontbreekt — vereist voor de Azure Functions-host zelf.");
    return new TableStorageNoodmailThrottleStore(
        storageVerbinding,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<TableStorageNoodmailThrottleStore>());
});

// Onafhankelijke database-uitvalmonitor (#1268, tegenhanger van #831 op de SQL Server-tier).
// Onvoorwaardelijk registreren: de reader doet pas iets bij aanroep en kiest dan zelf tussen het
// control-plane-pad (SUPABASE_PROJECT_REF + SUPABASE_ACCESS_TOKEN + EgressGuard) en een
// rechtstreekse verbindingsprobe. Zonder die instellingen valt hij vanzelf terug op de probe —
// dat is bewust, anders is de monitor dood voor elke club die geen managementtoken wil zetten.
builder.Services.AddSingleton<IDatabaseStatusReader, PostgresDatabaseStatusReader>();

builder.Build().Run();
