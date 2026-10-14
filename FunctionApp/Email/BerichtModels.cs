using Planner.Shared.Email;

namespace SportlinkFunction.Email;

// VerzoekType, NamensWie, BerichtClassificatie en EmailStatus staan sinds #1716 slice 5 eenmalig in
// Planner.Shared/Email/ (gelijk voor beide tiers). InkomendBericht blijft per tier: de vorm verschilt
// (ConversationId en Body zijn hier niet-nullable met standaard "", op de Postgres-tier nullable).

// Inkomend bericht (kanaal-agnostisch: email, WhatsApp, dry-run, etc.)
public class InkomendBericht : IInkomendBericht
{
    public string MessageId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Afzender { get; set; } = "";
    public string AfzenderNaam { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public DateTime OntvangstDatum { get; set; }
    public string Body { get; set; } = "";
    /// <summary>Internetheaders van het bericht (naam, waarde); alleen gevuld door de Graph-adapter, gebruikt om automatische afzenders te herkennen.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> InternetHeaders { get; set; } = [];
}
