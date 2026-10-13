using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Graph-kant van de gedeelde afhandeling in <see cref="EmailVerwerkingsAfhandeling"/> (#1650).
/// </summary>
internal sealed class EmailMailboxAdapter(IEmailGraphService graph) : IEmailMailboxActies
{
    public Task MarkeerGelezenAsync(string messageId) => graph.MarkAsReadAsync(messageId);

    public async Task LabelGeenAiAntwoordAsync(string messageId)
    {
        await graph.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
        await graph.SetCategoriesAsync(messageId, EmailCategorieLabels.GeenAiAntwoord);
    }
}

/// <summary>
/// Opslagkant van de gedeelde afhandeling (#1650) voor één bericht, op de Postgres-persistentie
/// (<see cref="SqlEmailPersistenceRepository"/>, verbinding en clubcode per aanroep).
/// </summary>
internal sealed class EmailVerwerkingOpslagAdapter(string cs, string clubCode, InkomendBericht email)
    : IEmailVerwerkingOpslag
{
    public async Task<EmailVerwerkingStandInfo?> HaalStandOpAsync()
        => EmailIdempotentie.NaarStandInfo(await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId));

    public Task<int> RegistreerNieuweVerwerkingAsync() => SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs, email, clubCode);

    public bool IsGelijktijdigeRegistratie(Exception ex) => ex is DubbeleMessageIdException;

    public Task VerhoogPogingenAsync(int verwerkingId) => SqlEmailPersistenceRepository.VerhoogPogingenAsync(cs, verwerkingId);

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding) => SqlEmailPersistenceRepository.UpdateFoutAsync(cs, verwerkingId, foutMelding);

    public Task ZetOpReviewAsync(int verwerkingId) => SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Review, null);
}

/// <summary>
/// Enige koppelpunt van <see cref="EmailProcessorFunction"/> met de gedeelde afhandeling (#1650):
/// bouwt de adapters en roept <see cref="EmailVerwerkingsAfhandeling"/> aan. Eén type in plaats van
/// vijf houdt de klassekoppeling van de timerfunctie onder de CA1506-grens.
/// </summary>
internal static class EmailVerwerkingKoppeling
{
    internal static Task<int?> BepaalVerwerkingIdAsync(
        string cs, string clubCode, InkomendBericht email, IEmailGraphService graph, ILogger log)
        => EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(
            email.MessageId, new EmailVerwerkingOpslagAdapter(cs, clubCode, email), new EmailMailboxAdapter(graph), log);

    internal static Task RegistreerClassificatieFoutAsync(
        string cs, string clubCode, InkomendBericht email, IEmailGraphService graph, ILogger log)
        => EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(
            email.MessageId, new EmailVerwerkingOpslagAdapter(cs, clubCode, email), new EmailMailboxAdapter(graph), log);

    internal static Task LegVerwerkingsFoutVastAsync(
        string cs, string clubCode, InkomendBericht email, string foutMelding, ILogger log)
        => EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync(
            email.MessageId, foutMelding, new EmailVerwerkingOpslagAdapter(cs, clubCode, email), log);

    internal static Task<bool> HandelBuitenScopeAsync(
        string cs, int verwerkingId, string messageId, bool isBuitenScope, string classificatieJson,
        IEmailGraphService graph, ILogger log)
        => EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(
            verwerkingId, messageId, isBuitenScope,
            () => SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.BuitenScope, classificatieJson),
            new EmailMailboxAdapter(graph), log);

    /// <summary>
    /// Antwoordlusbeveiliging (<see cref="AntwoordLimiet"/>): <c>true</c> als het bericht zonder antwoord is
    /// afgehandeld; status, label en korte trace staan dan vast.
    /// </summary>
    internal static Task<bool> HandelAntwoordLimietAsync(
        string cs, string clubCode, int verwerkingId, InkomendBericht email, BerichtClassificatie classificatie,
        string classificatieJson, IEmailGraphService graph, ILogger log)
        => EmailVerwerkingsAfhandeling.HandelAntwoordLimietAsync(
            new(verwerkingId, email.MessageId, email.ConversationId, classificatie.Type.ToString(), classificatie.TeamNaam,
                classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd),
            classificatie.Type == VerzoekType.Bevestiging,
            () => SqlEmailPersistenceRepository.TelVerzondenAntwoordenInConversatieAsync(cs, email.ConversationId!, clubCode),
            () => SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.GeenAntwoordNodig, classificatieJson),
            trace => EmailTraceRepository.UpsertAsync(cs, EmailTraceRecord.Van(
                verwerkingId, clubCode, classificatie.Type.ToString(), trace, EmailTraceRecord.VersieVan(typeof(EmailVerwerkingKoppeling).Assembly))),
            new EmailMailboxAdapter(graph), log);
}
