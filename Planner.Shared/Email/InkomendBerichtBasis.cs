using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Email;

/// <summary>De zes tier-onafhankelijke velden van een inkomend bericht (#1716 slice 14).</summary>
public abstract class InkomendBerichtBasis : IInkomendBericht
{
    [JsonProperty(Order = 0)]
    [JsonPropertyOrder(0)]
    public string MessageId { get; set; } = "";

    [JsonProperty(Order = 2)]
    [JsonPropertyOrder(2)]
    public string Afzender { get; set; } = "";

    [JsonProperty(Order = 3)]
    [JsonPropertyOrder(3)]
    public string AfzenderNaam { get; set; } = "";

    [JsonProperty(Order = 4)]
    [JsonPropertyOrder(4)]
    public string Onderwerp { get; set; } = "";

    [JsonProperty(Order = 5)]
    [JsonPropertyOrder(5)]
    public DateTime OntvangstDatum { get; set; }

    /// <summary>Internetheaders (naam, waarde), gevuld door de Graph-adapter voor herkenning van automatische afzenders.</summary>
    [JsonProperty(Order = 7)]
    [JsonPropertyOrder(7)]
    public IReadOnlyList<KeyValuePair<string, string>> InternetHeaders { get; set; } = [];
}
