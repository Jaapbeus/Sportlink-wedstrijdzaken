using Planner.Shared.Email;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace SportlinkFunction.Email;

// VerzoekType, NamensWie, BerichtClassificatie, EmailStatus en de zes gemeenschappelijke
// berichtvelden staan eenmalig in Planner.Shared/Email/. Dit concrete model blijft per tier omdat
// ConversationId en Body hier niet-nullable zijn met standaard "" (Postgres houdt ze nullable).

// Inkomend bericht (kanaal-agnostisch: email, WhatsApp, dry-run, etc.)
public class InkomendBericht : InkomendBerichtBasis
{
    [JsonProperty(Order = 1)]
    [JsonPropertyOrder(1)]
    public string ConversationId { get; set; } = "";

    [JsonProperty(Order = 6)]
    [JsonPropertyOrder(6)]
    public string Body { get; set; } = "";
}
