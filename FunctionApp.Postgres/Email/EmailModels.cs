using Planner.Shared.Email;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-lokaal inkomend-berichtmodel en de unieke-sleutelexceptie (#889).
/// <c>EmailStatus</c>, <c>VerzoekType</c>, <c>NamensWie</c> en <c>BerichtClassificatie</c> staan sinds #1716
/// slice 5 eenmalig in <c>Planner.Shared/Email/</c>. <c>ClubAppSettingsSnapshot</c>, <c>EmailTemplate</c>
/// en <c>EmailVerwerkingStand</c> staan sinds slice 13 daar; de zes gemeenschappelijke berichtvelden
/// staan sinds slice 14 in <c>InkomendBerichtBasis</c>. <c>InkomendBericht</c> blijft hier:
/// <c>ConversationId</c> en <c>Body</c> zijn op deze tier nullable, op de SQL Server-tier niet-nullable
/// met standaard <c>""</c>. Het concrete type en de twee velden blijven tierlokaal om die defaults
/// en nullability te behouden.
/// </summary>
public sealed class InkomendBericht : InkomendBerichtBasis
{
    [JsonProperty(Order = 1)]
    [JsonPropertyOrder(1)]
    public string? ConversationId { get; set; }

    [JsonProperty(Order = 6)]
    [JsonPropertyOrder(6)]
    public string? Body { get; set; }
}

internal sealed class DubbeleMessageIdException(string messageId, Exception inner)
    : Exception("MessageId is al geregistreerd in planner.emailverwerking", inner)
{
    internal string MessageId { get; } = messageId;
}
