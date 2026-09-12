using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Leest <c>SportlinkMatch.MatchDate</c> — live vastgesteld (2026-09-06, vervolg op #1036) dat
/// Sportlinks <c>Match</c>-endpoint dit veld niet als losse datumstring levert, maar als geneste
/// waarde: <c>{ "Date": "2026-09-27", "StartTime": "10:30:00", "DateTime": "2026-09-27T10:30:00+0200" }</c>.
/// Deze converter gebruikt het "DateTime"-subveld, dat al de volledige offset bevat.
/// </summary>
public sealed class MatchDateJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "DateTime", StringComparison.OrdinalIgnoreCase))
                    return DateTimeOffset.Parse(property.Value.GetString()!, CultureInfo.InvariantCulture);
            }

            throw new JsonException("MatchDate-object bevat geen 'DateTime'-subveld.");
        }

        throw new JsonException($"Onverwacht JSON-tokentype '{reader.TokenType}' voor MatchDate.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
