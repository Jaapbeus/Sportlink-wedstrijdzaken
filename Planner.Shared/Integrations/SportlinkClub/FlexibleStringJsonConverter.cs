using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Leest een JSON-waarde die zowel als string als als getal kan terugkomen naar een C#
/// <see cref="string"/> — live vastgesteld op <c>SportlinkMatch.ExternalMatchId</c> (2026-09-06):
/// Sportlinks eigen <c>Match</c>-endpoint levert dit veld als JSON-getal (<c>3403</c>), niet als
/// string (<c>"3403"</c>), ondanks de eerdere aanname dat
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> dit al zou opvangen — die instelling
/// werkt alleen de andere kant op (een string lezen in een numeriek C#-veld), niet andersom.
/// </summary>
public sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Onverwacht JSON-tokentype '{reader.TokenType}' voor een flexibel string-veld.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
