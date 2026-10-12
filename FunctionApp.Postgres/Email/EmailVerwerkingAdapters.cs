using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

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
}
