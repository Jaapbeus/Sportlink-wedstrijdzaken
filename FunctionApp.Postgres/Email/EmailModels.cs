namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-lokale kopieën van de modeltypes uit <c>FunctionApp/Email/BerichtModels.cs</c> en
/// <c>EmailPersistenceService.cs</c> (#889) — pure databagger zonder SQL-afhankelijkheid, dus geen
/// architectuurbezwaar tegen een tweede definitie (in tegenstelling tot <c>TeamNaamNormalisatie</c>,
/// dat wél gedrag/logica bevat en daarom naar <c>Planner.Shared</c> is verhuisd).
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
}

public enum EmailStatus
{
    Ontvangen, Geclassificeerd, Verwerkt, AntwoordVerstuurd, Review, Fout, BuitenScope, GeenAntwoordNodig
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
