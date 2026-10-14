using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.TeamResolution;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-kant van één poll van de e-mailverwerker (#1716 slice 9). De fase-orkestratie staat in
/// <see cref="EmailVerwerkerFasen"/>; hier alleen databasetoegang (verbinding en clubcode per aanroep),
/// de Graph-adapter van deze tier, de AI-service en de verwerking van één bericht.
/// </summary>
internal sealed class EmailVerwerkerPoort(
    ILogger log,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IEmailGraphService graph,
    INoodmailThrottleStore throttle,
    string cs) : IEmailVerwerkerPoort<InkomendBericht>
{
    /// <summary>Adviesregels in de noodmails voor deze tier (Postgres-hosting, OpenAI-dashboard).</summary>
    internal static readonly EmailNoodmailTeksten Teksten = new(
        "Controleer de Postgres-hosting-omgeving (bijv. het Supabase-dashboard) op status en resource-limieten.",
        "  • Controleer de OpenAI-dashboard-quota\n"
        + "  • Verhoog de quota-limiet of wacht tot de quota vernieuwt\n");

    private readonly EmailBatchFilterService _batchFilter = new();
    private readonly EmailReplyPolicyService _replyPolicy = new();
    private BerichtAiService? _ai;
    private ITeamResolver? _teamResolver;
    private string? _clubCode;

    public IReplyGraph Graph => graph;
    public INoodmailThrottlePoort Throttle => throttle;
    public EmailNoodmailTeksten NoodmailTeksten => Teksten;

    private string ClubCode => _clubCode ?? throw new InvalidOperationException("Instellingen niet geladen");

    public Task<List<InkomendBericht>> HaalOngelezenAsync() => graph.GetUnreadEmailsAsync();

    public Task<List<InkomendBericht>> PreFilterAsync(
        List<InkomendBericht> emails, string eigenMailbox, IReadOnlySet<string> uitgeslotenCache)
        => _batchFilter.PreFilterVoorClassificatieAsync(emails, eigenMailbox, uitgeslotenCache, graph, log);

    public List<InkomendBericht> FilterUitgesloten(List<InkomendBericht> emails, IReadOnlySet<string> uitgeslotenAdressen)
        => _batchFilter.FilterUitgeslotenAdressen(emails, uitgeslotenAdressen);

    public async Task<HashSet<string>> WekEnLaadUitsluitingslijstAsync()
    {
        await PostgresSystemUtilities.WaitForDatabaseAsync(log);
        await PostgresAppSettings.LoadSettingsAsync(log);
        return await LaadUitgeslotenAdressenAsync(PostgresClubScope.Primary);
    }

    // IChatClient is alleen geregistreerd als OpenAiApiKey geconfigureerd is én EgressGuard dat
    // toestaat (#857) — buiten productie onvoorwaardelijk null. Nooit gooien: loggen en stoppen,
    // exact hetzelfde patroon als graphService in Run.
    public bool BereidAiVoor()
    {
        var chatClient = services.GetService<IChatClient>();
        if (chatClient is null)
        {
            log.LogError("IChatClient niet beschikbaar — controleer OpenAiApiKey/EgressGuard-configuratie; verwerking overgeslagen");
            return false;
        }

        _ai = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>(), chatClient);
        return true;
    }

    public Task<EmailClassificatieBatch<InkomendBericht>> ClassificeerAsync(List<InkomendBericht> teClassificeren)
    {
        var ai = Ai;
        return new EmailClassificationService().ClassificeerBatchAsync(
            teClassificeren,
            email => ai.ClassificeerBerichtAsync(email.Body ?? "", email.Onderwerp, email.Afzender),
            EmailFoutCategorie.IsOpenAiQuotaFout,
            log);
    }

    public Task LabelBuitenScopeAsync(List<(InkomendBericht Email, BerichtClassificatie Classificatie)> classificaties)
        => _batchFilter.LabelBuitenScopeAsync(classificaties, graph, log);

    public Task WachtOpDatabaseAsync() => PostgresSystemUtilities.WaitForDatabaseAsync(log);

    // De clubcode hoort bij het wekken: een ontbrekende instelling telt als verbindingsfout (noodmail).
    public async Task LaadInstellingenAsync()
    {
        await PostgresAppSettings.LoadSettingsAsync(log);
        _clubCode = PostgresClubScope.Primary;
    }

    public Task<HashSet<string>> LaadUitsluitingslijstAsync() => LaadUitgeslotenAdressenAsync(ClubCode);

    // Teamnaam→canonieke-teamlijst-verversing hoort hier, niet in fase 1: idempotent, kan
    // zonder bezwaar herhaald worden — zelfde pad als EmailTestFunction/AdminTeamsHerstelFunction.
    public async Task<bool> BereidVerwerkingVoorAsync()
    {
        await TeamCanonicalisatieService.RefreshAsync(cs, ClubCode, log);
        _teamResolver = services.GetRequiredService<ITeamResolver>();
        return true;
    }

    public Task RegistreerClassificatieFoutAsync(InkomendBericht email)
        => EmailProcessorFunction.RegistreerClassificatieFoutAsync(cs, ClubCode, email, graph, log);

    public Task VerwerkEmailAsync(InkomendBericht email, BerichtClassificatie classificatie, IReadOnlySet<string> uitgeslotenAdressen)
        => EmailProcessorFunction.VerwerkEmailAsync(
            cs, ClubCode, email, classificatie, graph, uitgeslotenAdressen, Ai, _replyPolicy, log,
            _teamResolver ?? throw new InvalidOperationException("Teamresolutie niet voorbereid"));

    public Task LegVerwerkingsFoutVastAsync(InkomendBericht email, string foutMelding)
        => EmailProcessorFunction.LegVerwerkingsFoutVastAsync(cs, ClubCode, email, foutMelding, log);

    private BerichtAiService Ai => _ai ?? throw new InvalidOperationException("AI-classificatie niet voorbereid");

    private async Task<HashSet<string>> LaadUitgeslotenAdressenAsync(string clubCode)
    {
        var adressen = await SqlEmailPersistenceRepository.GetExcludedEmailAddressesAsync(cs, clubCode);
        log.LogInformation("Uitsluitingslijst geladen: {Aantal} adressen", adressen.Count);
        return adressen;
    }
}
