using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Leest een JSON-waarde die zowel als getal als als string kan terugkomen naar een C#
/// <see cref="long"/> — defensieve tegenhanger van <see cref="FlexibleStringJsonConverter"/>.
/// Sportlinks <c>ExternalMatchId</c> is al vastgesteld (#1036) als een veld dat wisselt tussen
/// beide vormen afhankelijk van het endpoint; deze converter voorkomt dezelfde crash op elke
/// plek waar dit veld als getal wordt uitgelezen (bijv. <c>UpdateMatchDetails</c>-snapshot, #1047).
/// </summary>
public sealed class FlexibleLongJsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String => long.TryParse(reader.GetString(), out var l) ? l : null,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Onverwacht JSON-tokentype '{reader.TokenType}' voor een flexibel long-veld.")
        };

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
