using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace SportlinkFunction.Email;

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
/// Opslagkant van de gedeelde afhandeling (#1650) voor één bericht, op de SQL Server-persistentie
/// (<see cref="IEmailPersistenceService"/>).
/// </summary>
internal sealed class EmailVerwerkingOpslagAdapter(InkomendBericht email, IEmailPersistenceService persistence)
    : IEmailVerwerkingOpslag
{
    public async Task<EmailVerwerkingStandInfo?> HaalStandOpAsync()
        => EmailIdempotentie.NaarStandInfo(await persistence.HaalVerwerkingStandOpAsync(email.MessageId));

    public Task<int> RegistreerNieuweVerwerkingAsync() => persistence.InsertEmailVerwerkingAsync(email);

    public bool IsGelijktijdigeRegistratie(Exception ex) => ex is DubbeleMessageIdException;

    public Task VerhoogPogingenAsync(int verwerkingId) => persistence.VerhoogPogingenAsync(verwerkingId);

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding) => persistence.UpdateFoutAsync(verwerkingId, foutMelding);

    public Task ZetOpReviewAsync(int verwerkingId) => persistence.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
}

/// <summary>
/// Enige koppelpunt van <see cref="EmailProcessorFunction"/> met de gedeelde afhandeling (#1650):
/// bouwt de adapters en roept <see cref="EmailVerwerkingsAfhandeling"/> aan. Eén type in plaats van
/// vijf houdt de klassekoppeling van de timerfunctie onder de CA1506-grens.
/// </summary>
internal static class EmailVerwerkingKoppeling
{
    internal static Task<int?> BepaalVerwerkingIdAsync(
        InkomendBericht email, IEmailGraphService graph, IEmailPersistenceService persistence, ILogger log)
        => EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(
            email.MessageId, new EmailVerwerkingOpslagAdapter(email, persistence), new EmailMailboxAdapter(graph), log);

    internal static Task RegistreerClassificatieFoutAsync(
        InkomendBericht email, IEmailGraphService graph, IEmailPersistenceService persistence, ILogger log)
        => EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(
            email.MessageId, new EmailVerwerkingOpslagAdapter(email, persistence), new EmailMailboxAdapter(graph), log);

    internal static Task LegVerwerkingsFoutVastAsync(
        InkomendBericht email, string foutMelding, IEmailPersistenceService persistence, ILogger log)
        => EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync(
            email.MessageId, foutMelding, new EmailVerwerkingOpslagAdapter(email, persistence), log);

    internal static Task<bool> HandelBuitenScopeAsync(
        int verwerkingId, string messageId, bool isBuitenScope, string classificatieJson,
        IEmailGraphService graph, IEmailPersistenceService persistence, ILogger log)
        => EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(
            verwerkingId, messageId, isBuitenScope,
            () => persistence.UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, classificatieJson),
            new EmailMailboxAdapter(graph), log);

    /// <summary>
    /// Antwoordlusbeveiliging (<see cref="AntwoordLimiet"/>): <c>true</c> als het bericht zonder antwoord is
    /// afgehandeld; status, label en korte trace staan dan vast.
    /// </summary>
    internal static Task<bool> HandelAntwoordLimietAsync(
        int verwerkingId, InkomendBericht email, BerichtClassificatie classificatie, string classificatieJson,
        IEmailGraphService graph, IEmailPersistenceService persistence, ILogger log)
        => EmailVerwerkingsAfhandeling.HandelAntwoordLimietAsync(
            new(verwerkingId, email.MessageId, email.ConversationId, classificatie.Type.ToString(), classificatie.TeamNaam,
                classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd),
            classificatie.Type == VerzoekType.Bevestiging,
            () => persistence.TelVerzondenAntwoordenInConversatieAsync(email.ConversationId!),
            () => persistence.UpdateStatusAsync(verwerkingId, EmailStatus.GeenAntwoordNodig, classificatieJson),
            trace => persistence.UpsertTraceAsync(
                verwerkingId, classificatie.Type.ToString(), trace, EmailTraceRecord.VersieVan(typeof(EmailVerwerkingKoppeling).Assembly)),
            new EmailMailboxAdapter(graph), log);
}
