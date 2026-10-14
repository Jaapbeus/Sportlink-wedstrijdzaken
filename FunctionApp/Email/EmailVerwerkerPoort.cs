using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Monitoring;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Email;

/// <summary>
/// SQL Server-kant van één poll van de e-mailverwerker (#1716 slice 9). De fase-orkestratie staat in
/// <see cref="EmailVerwerkerFasen"/>; hier alleen databasetoegang (<see cref="IEmailPersistenceService"/>),
/// de Graph-adapter van deze tier, de AI-service, de teamlijstcontrole en de verwerking van één bericht.
/// </summary>
internal sealed class EmailVerwerkerPoort(
    ILogger log,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IEmailGraphService graph,
    ITeamResolver teamResolver,
    IEmailPersistenceService persistence,
    INoodmailThrottleStore throttle) : IEmailVerwerkerPoort<InkomendBericht>
{
    /// <summary>Adviesregels in de noodmails voor deze tier (Azure SQL Serverless, Azure OpenAI).</summary>
    internal static readonly EmailNoodmailTeksten Teksten = new(
        "Meest waarschijnlijke oorzaak: Azure SQL Serverless database was gepauzeerd (auto-pause) en kon niet op tijd opstarten.\n"
        + "De processor probeert 10× met 15 seconden tussentijd (max. 150 seconden). Als de database langer nodig heeft om te starten, verschijnt deze melding.\n\n"
        + "Controleer in Azure Portal:\n"
        + "  • Azure SQL Server → Database → Overzicht → Status (moet 'Online' zijn)\n"
        + "  • Compute + storage → Free monthly vCore amount (maandlimiet bereikt?)\n\n"
        + "Als de maandlimiet bereikt is: Azure Portal → SQL database → Compute and Storage → \"Continue using database with additional charges\"",
        "  • Controleer in Azure Portal → OpenAI resource → Overzicht → Quota\n"
        + "  • Verhoog de quota-limiet of wacht tot de quota vernieuwt (begin volgende maand)\n");

    private readonly EmailBatchFilterService _batchFilter = new();
    private readonly EmailReplyPolicyService _replyPolicy = new();
    private BerichtAiService? _ai;

    public IReplyGraph Graph => graph;
    public INoodmailThrottlePoort Throttle => throttle;
    public EmailNoodmailTeksten NoodmailTeksten => Teksten;

    public Task<List<InkomendBericht>> HaalOngelezenAsync() => graph.GetUnreadEmailsAsync();

    public Task<List<InkomendBericht>> PreFilterAsync(
        List<InkomendBericht> emails, string eigenMailbox, IReadOnlySet<string> uitgeslotenCache)
        => _batchFilter.PreFilterVoorClassificatieAsync(emails, eigenMailbox, uitgeslotenCache, graph, log);

    public List<InkomendBericht> FilterUitgesloten(List<InkomendBericht> emails, IReadOnlySet<string> uitgeslotenAdressen)
        => _batchFilter.FilterUitgeslotenAdressen(emails, uitgeslotenAdressen);

    public async Task<HashSet<string>> WekEnLaadUitsluitingslijstAsync()
    {
        await SystemUtilities.WaitForDatabaseAsync(log);
        await SystemUtilities.AppSettings.LoadSettingsAsync(log);
        return await persistence.LaadUitgeslotenAdressenAsync(log);
    }

    // AI-classificatie voor alle resterende emails — database wordt niet nogmaals gewekt. Op deze tier
    // is een ontbrekende IChatClient een configuratiefout: gooien, zoals vóór #1716 slice 9.
    public bool BereidAiVoor()
    {
        var chatClient = services.GetService<IChatClient>()
            ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
        _ai = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>(), chatClient);
        return true;
    }

    public Task<EmailClassificatieBatch<InkomendBericht>> ClassificeerAsync(List<InkomendBericht> teClassificeren)
    {
        var ai = Ai;
        return new EmailClassificationService().ClassificeerBatchAsync(
            teClassificeren,
            email => ai.ClassificeerBerichtAsync(email.Body, email.Onderwerp, email.Afzender),
            EmailFoutCategorie.IsOpenAiQuotaFout,
            log);
    }

    public Task LabelBuitenScopeAsync(List<(InkomendBericht Email, BerichtClassificatie Classificatie)> classificaties)
        => _batchFilter.LabelBuitenScopeAsync(classificaties, graph, log);

    public Task WachtOpDatabaseAsync() => SystemUtilities.WaitForDatabaseAsync(log);

    public Task LaadInstellingenAsync() => SystemUtilities.AppSettings.LoadSettingsAsync(log);

    public Task<HashSet<string>> LaadUitsluitingslijstAsync() => persistence.LaadUitgeslotenAdressenAsync(log);

    /// <summary>
    /// Teamlijst-gereedheid hoort in fase 2, niet in fase 1 (#700). Twee redenen, beide gevonden bij de
    /// adversariële review:
    ///  1. De check heeft de clubCode uit dbo.AppSettings nodig. In fase 1 is die cache op een verse
    ///     worker nog leeg — de check zou dan altijd falen en terugkeren vóór de regel die de settings
    ///     laadt. Dat zette de verwerking permanent stil, met een logregel die naar de verkeerde
    ///     oorzaak wees.
    ///  2. In fase 1 zou hij bij élke poll de database openen, ook bij een lege inbox. Azure SQL
    ///     Serverless pauzeert pas na 60 minuten inactiviteit, dus dat houdt de database 24/7 wakker
    ///     en verbruikt het gratis vCore-budget. Hier is de database toch al wakker.
    /// Retourneert <c>false</c> als de verwerking moet stoppen.
    /// </summary>
    public async Task<bool> BereidVerwerkingVoorAsync()
    {
        var gereedheid = services.GetService<TeamlijstGereedheid>();
        if (gereedheid is not null
            && !await gereedheid.ZorgVoorTeamlijstAsync(SystemUtilities.AppSettings.GetOptionalClubCode()))
        {
            log.LogError(
                "Teamherkenning niet mogelijk (geen actieve teams voor deze club) — verwerking overgeslagen "
                + "om verkeerde koppelingen te voorkomen. Controleer of de Sportlink-sync heeft gedraaid.");
            return false;
        }

        return true;
    }

    public Task RegistreerClassificatieFoutAsync(InkomendBericht email)
        => EmailProcessorFunction.RegistreerClassificatieFoutAsync(email, graph, persistence, log);

    public Task VerwerkEmailAsync(InkomendBericht email, BerichtClassificatie classificatie, IReadOnlySet<string> uitgeslotenAdressen)
        => EmailProcessorFunction.VerwerkEmailAsync(
            email, classificatie, graph, uitgeslotenAdressen, Ai, persistence, _replyPolicy, log, teamResolver);

    public Task LegVerwerkingsFoutVastAsync(InkomendBericht email, string foutMelding)
        => EmailProcessorFunction.LegVerwerkingsFoutVastAsync(email, foutMelding, persistence, log);

    private BerichtAiService Ai => _ai ?? throw new InvalidOperationException("AI-classificatie niet voorbereid");
}
