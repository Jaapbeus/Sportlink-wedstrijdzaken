using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Stateloze parser van Sportlinks <c>ConfirmationNeeded</c>-respons (#995, #1631).</summary>
internal static class SportlinkMatchChangeValidatieLezer
{
    /// <summary>
    /// Parseert Sportlinks <c>ConfirmationNeeded</c>-veld uit een <c>UpdateMatchDetails</c>-respons
    /// (#995) naar <see cref="SportlinkMatchChangeValidatie"/>. <b>ONBEVESTIGD:</b> de exacte vorm is
    /// nooit met een netwerktrace gezien — <c>ValidationResultMessages</c>-elementen kunnen kale
    /// strings zijn óf objecten met een <c>Message</c>- of <c>Description</c>-veld (issue #995:
    /// "elementvorm onbekend"), en <c>HasBlockingMessages</c> kan zowel genest onder
    /// <c>ConfirmationNeeded</c> als op het toplevel staan — deze parser probeert beide, zelfde
    /// defensieve stijl als <see cref="SportlinkMutatieBodies.VerrijkOfficialsResultaat"/>/<see cref="SportlinkJsonHulp.UnwrapArrayEnvelope"/>.
    /// Geeft <c>null</c> terug bij een onherkenbare JSON-vorm — nooit een gok naar de aanroeper.
    /// </summary>
    internal static SportlinkMatchChangeValidatie? ParseMatchChangeValidatie(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // #1320: vier toplevel booleans, gezien bij de oefenwedstrijd-trace van 2026-09-26 —
            // GEEN bewijs voor de verplichte-wijzigingsverzoek-vorm, zie de doc-comment op
            // SportlinkMatchChangeValidatie. Ontbrekend of geen boolean -> null, nooit gokken.
            bool? LeesToplevelBool(string naam) =>
                doc.RootElement.TryGetProperty(naam, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? el.GetBoolean()
                    : null;

            var isSuccess = LeesToplevelBool("IsSuccess");
            var isMatchChangeRequestMandatory = LeesToplevelBool("IsMatchChangeRequestMandatory");
            var isOwnFacility = LeesToplevelBool("IsOwnFacility");
            var isForceUpdate = LeesToplevelBool("IsForceUpdate");

            if (!doc.RootElement.TryGetProperty("ConfirmationNeeded", out var confirmationElement) ||
                confirmationElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new SportlinkMatchChangeValidatie(
                    false, Array.Empty<string>(), false,
                    isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);

            var meldingen = LeesValidatieMeldingen(confirmationElement);

            // Positie van HasBlockingMessages niet bevestigd — genest onder ConfirmationNeeded en
            // toplevel allebei proberen, genest weegt zwaarder (issue-tekst noemt ze als één geheel).
            var hasBlocking =
                (confirmationElement.ValueKind == JsonValueKind.Object &&
                 confirmationElement.TryGetProperty("HasBlockingMessages", out var nestedBlocking) &&
                 nestedBlocking.ValueKind == JsonValueKind.True)
                || (doc.RootElement.TryGetProperty("HasBlockingMessages", out var rootBlocking) &&
                    rootBlocking.ValueKind == JsonValueKind.True);

            return new SportlinkMatchChangeValidatie(
                true, meldingen, hasBlocking,
                isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Leest <c>ValidationResultMessages</c>: kale strings of objecten met <c>Message</c>/<c>Description</c>.</summary>
    private static List<string> LeesValidatieMeldingen(JsonElement confirmationElement)
    {
        var meldingen = new List<string>();
        if (confirmationElement.ValueKind != JsonValueKind.Object ||
            !confirmationElement.TryGetProperty("ValidationResultMessages", out var messagesElement) ||
            messagesElement.ValueKind != JsonValueKind.Array)
            return meldingen;

        foreach (var item in messagesElement.EnumerateArray())
        {
            string? tekst = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                JsonValueKind.Object when item.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String => d.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(tekst))
                meldingen.Add(tekst);
        }
        return meldingen;
    }
}
