using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

internal interface IEmailPersistenceRepository
{
    Task<HashSet<string>> GetExcludedEmailAddressesAsync(string clubCode);
    Task<bool> BestaatMessageIdAsync(string messageId);
    Task<int> InsertEmailVerwerkingAsync(InkomendBericht email);
    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData);
    Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson);
    Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail);
    Task UpdateFoutAsync(string messageId, string foutMelding);
    Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, string clubCode, ILogger log);
    Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId);
    Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting,
        string clubCode);
    Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(string clubCode, ILogger log);
}

internal sealed class SqlEmailPersistenceRepository : IEmailPersistenceRepository
{
    public async Task<HashSet<string>> GetExcludedEmailAddressesAsync(string clubCode)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "SELECT [EmailAdres] FROM [dbo].[UitgeslotenEmailAdressen] WHERE [Actief] = 1 AND [ClubCode] = @ClubCode",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        var adressen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            adressen.Add(reader.GetString(0));
        return adressen;
    }

    public Task<bool> BestaatMessageIdAsync(string messageId)
        => EmailProcessingRepository.BestaatMessageIdAsync(messageId);

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
        => EmailProcessingRepository.InsertEmailVerwerkingAsync(email);

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
        => EmailProcessingRepository.UpdateStatusAsync(verwerkingId, status, geextraheerdeData);

    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => EmailProcessingRepository.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
        => EmailProcessingRepository.UpdateAntwoordVerstuurdAsync(verwerkingId, verstuurdNaar, antwoordEmail);

    public Task UpdateFoutAsync(string messageId, string foutMelding)
        => EmailProcessingRepository.UpdateFoutAsync(messageId, foutMelding);

    public Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, string clubCode, ILogger log)
        => EmailProcessingRepository.DetecteerReplyOpOnsAntwoordAsync(conversationId, clubCode, log);

    public Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
        => EmailProcessingRepository.UpdateReplyStatusAsync(verwerkingId, isReply, replyOpVerwerkingId);

    public Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting,
        string clubCode)
        => LearningMomentRepository.InsertClassificatieCorrectieAsync(
            origineleVerwerkingId,
            correctionVerwerkingId,
            origineelType,
            afgeleidType,
            originaleSamenvatting,
            correctieSamenvatting,
            clubCode);

    public Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(string clubCode, ILogger log)
        => LearningMomentRepository.HaalVoorbeeldenOpAsync(clubCode, log);
}
