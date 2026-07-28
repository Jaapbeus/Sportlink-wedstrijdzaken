using Microsoft.Extensions.Logging;
using SportlinkFunction.Email;

namespace FunctionApp.Tests.Email.TestDoubles;

internal sealed class RecordingEmailPersistenceService : IEmailPersistenceService
{
    public List<(int VerwerkingId, EmailStatus Status, string? GeextraheerdeData)> StatusUpdates { get; } = new();

    public List<(int VerwerkingId, string VerstuurdNaar, string AntwoordEmail)> AntwoordUpdates { get; } = new();

    public List<(int VerwerkingId, string FoutMelding)> FoutUpdates { get; } = new();

    /// <summary>Verzendintenties die vóór een verzendpoging zijn vastgelegd (#716).</summary>
    public List<int> VerzendPogingMarkeringen { get; } = new();

    /// <summary>Verzendintenties die zijn gewist na een aantoonbaar mislukte verzending (#716).</summary>
    public List<int> VerzendPogingWissingen { get; } = new();

    /// <summary>Simuleert dat de verzendintentie niet vastgelegd kan worden (#716).</summary>
    public bool ThrowOnMarkeerVerzendPoging { get; set; }

    public List<(int VerwerkingId, string AntwoordEmail)> VoorgesteldeAntwoorden { get; } = new();

    public List<int> PogingVerhogingen { get; } = new();

    public List<InkomendBericht> Inserts { get; } = new();

    /// <summary>Stand die de guard te zien krijgt; null = bericht nog niet geregistreerd.</summary>
    public EmailVerwerkingStand? StandToReturn { get; set; }

    /// <summary>Simuleert een database die het verzonden antwoord niet kan vastleggen.</summary>
    public bool ThrowOnUpdateAntwoordVerstuurd { get; set; }

    /// <summary>
    /// Simuleert dat een gelijktijdige invocatie dezelfde MessageId al heeft geregistreerd: de INSERT
    /// botst op UQ_EmailVerwerking_MessageId. (#707)
    /// </summary>
    public bool ThrowDubbeleMessageIdOnInsert { get; set; }

    public Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log)
        => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId)
        => Task.FromResult(StandToReturn);

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
    {
        if (ThrowDubbeleMessageIdOnInsert)
            throw new DubbeleMessageIdException(email.MessageId, new InvalidOperationException("UQ-schending (gesimuleerd)"));

        Inserts.Add(email);
        return Task.FromResult(1);
    }

    public Task VerhoogPogingenAsync(int verwerkingId)
    {
        PogingVerhogingen.Add(verwerkingId);
        return Task.CompletedTask;
    }

    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
    {
        VoorgesteldeAntwoorden.Add((verwerkingId, antwoordEmail));
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        StatusUpdates.Add((verwerkingId, status, geextraheerdeData));
        return Task.CompletedTask;
    }

    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => Task.CompletedTask;

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        if (ThrowOnUpdateAntwoordVerstuurd)
            throw new InvalidOperationException("Vastleggen van het antwoord mislukt (gesimuleerd)");

        AntwoordUpdates.Add((verwerkingId, verstuurdNaar, antwoordEmail));
        return Task.CompletedTask;
    }

    public Task MarkeerVerzendPogingAsync(int verwerkingId)
    {
        if (ThrowOnMarkeerVerzendPoging)
            throw new InvalidOperationException("Vastleggen van de verzendintentie mislukt (gesimuleerd)");

        VerzendPogingMarkeringen.Add(verwerkingId);
        return Task.CompletedTask;
    }

    public Task WisVerzendPogingAsync(int verwerkingId)
    {
        VerzendPogingWissingen.Add(verwerkingId);
        return Task.CompletedTask;
    }

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding)
    {
        FoutUpdates.Add((verwerkingId, foutMelding));
        return Task.CompletedTask;
    }

    public Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, ILogger log)
        => Task.FromResult((false, (int?)null, (string?)null, (string?)null));

    public Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
        => Task.CompletedTask;

    public Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting)
        => Task.CompletedTask;

    public Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(ILogger log)
        => Task.FromResult(new List<ClassificatieCorrectieVoorbeeld>());

    // ALLSTARS is de vaste democlubcode van het project — nooit een echte clubnaam in tests.
    public string ResolveClubCode() => "ALLSTARS";
}
