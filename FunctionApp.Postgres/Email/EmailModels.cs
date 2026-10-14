namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-lokale kopieën van de modeltypes uit <c>FunctionApp/Email/BerichtModels.cs</c> en
/// <c>EmailPersistenceService.cs</c> (#889) — pure databagger zonder SQL-afhankelijkheid, dus geen
/// architectuurbezwaar tegen een tweede definitie (in tegenstelling tot <c>TeamNaamNormalisatie</c>,
/// dat wél gedrag/logica bevat en daarom naar <c>Planner.Shared</c> is verhuisd).
/// <c>EmailStatus</c>, <c>VerzoekType</c>, <c>NamensWie</c> en <c>BerichtClassificatie</c> staan sinds #1716
/// slice 5 eenmalig in <c>Planner.Shared/Email/</c>. <c>InkomendBericht</c> blijft hier: <c>ConversationId</c>
/// en <c>Body</c> zijn op deze tier nullable, op de SQL Server-tier niet-nullable met standaard <c>""</c>;
/// samenvoegen zou een standaardwaarde en daarmee opgeslagen gegevens op één tier veranderen.
/// </summary>
public sealed class InkomendBericht
{
    public string MessageId { get; set; } = "";
    public string? ConversationId { get; set; }
    public string Afzender { get; set; } = "";
    // #889 vervolg (BerichtPipeline/EmailTestFunction): de SQL Server-tier se InkomendBericht
    // heeft dit veld al sinds het begin — hier toegevoegd toen de eerste consument die het
    // daadwerkelijk gebruikt (EmailTestFunction) werd vertaald.
    public string AfzenderNaam { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public DateTime OntvangstDatum { get; set; }
    public string? Body { get; set; }
    /// <summary>Internetheaders van het bericht (naam, waarde); alleen gevuld door de Graph-adapter, gebruikt om automatische afzenders te herkennen.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> InternetHeaders { get; set; } = [];
}

public sealed record ClassificatieCorrectieVoorbeeld(
    string OrigineelType, string JuistType, string OrigineleSamenvatting, string CorrectieSamenvatting);

public sealed record EmailVerwerkingStand(
    int VerwerkingId, string Status, int Pogingen, bool AntwoordVerstuurd, bool VerzendPogingOnbeslist = false);

internal sealed class DubbeleMessageIdException(string messageId, Exception inner)
    : Exception("MessageId is al geregistreerd in planner.emailverwerking", inner)
{
    internal string MessageId { get; } = messageId;
}
