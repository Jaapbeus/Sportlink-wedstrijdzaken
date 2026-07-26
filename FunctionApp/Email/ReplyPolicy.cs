using Newtonsoft.Json.Linq;

namespace SportlinkFunction.Email;

/// <summary>
/// Wat er met een verwerkt bericht moet gebeuren: automatisch antwoorden of niet.
/// </summary>
public enum ReplyActie
{
    /// <summary>Automatisch antwoord versturen.</summary>
    Versturen,

    /// <summary>Geen antwoord versturen — de coördinator plant handmatig en koppelt zelf terug.</summary>
    Onderdrukken
}

public sealed record ReplyBesluit(ReplyActie Actie, string Reden)
{
    public bool MoetVersturen => Actie == ReplyActie.Versturen;
}

/// <summary>
/// Beslist of een beschikbaarheidsantwoord automatisch verstuurd wordt (#572).
///
/// Functionele eis (eigenaar, 2026-07-25):
///   • Enkele datum, planning mogelijk      → geen automatisch antwoord (handmatige opvolging).
///   • Enkele datum, planning niet mogelijk → wel antwoord, met duidelijke reden.
///   • Meerdere datums, gemengde uitkomst   → wel antwoord; per datum staat er in het antwoord
///                                            al wat wel en niet kan.
///   • Meerdere datums, alles mogelijk      → geen antwoord (zelfde redenering als enkele datum).
///   • Meerdere datums, niets mogelijk      → wel antwoord.
///
/// Alleen <see cref="VerzoekType.BeschikbaarheidCheck"/> valt onder deze policy. Andere types
/// (herplanverzoek, teamcontact, bevestiging) hebben geen "wel/niet planbaar"-uitkomst maar een
/// inhoudelijk antwoord dat de afzender nodig heeft — die blijven altijd antwoorden.
///
/// Deze klasse is puur: geen DB, geen Graph, geen configuratie. Daarom volledig te testen
/// op de exacte JSON die de plannerpipeline oplevert.
/// </summary>
public static class ReplyPolicy
{
    public static ReplyBesluit Bepaal(BerichtClassificatie classificatie, string plannerResponseJson)
    {
        if (classificatie.Type != VerzoekType.BeschikbaarheidCheck)
            return new ReplyBesluit(ReplyActie.Versturen,
                $"Type {classificatie.Type} heeft altijd een inhoudelijk antwoord nodig");

        JObject planner;
        try
        {
            planner = JObject.Parse(plannerResponseJson);
        }
        catch
        {
            // Onleesbare plannerrespons: fail-open richting antwoorden. Zwijgen zou de
            // afzender zonder reactie laten zonder dat iemand het merkt.
            return new ReplyBesluit(ReplyActie.Versturen, "Plannerrespons niet leesbaar");
        }

        // Wedstrijd staat al in Sportlink: informatief antwoord, geen planbaarheidsuitkomst.
        if (IsTrue(planner, "wedstrijdAlIngepland"))
            return new ReplyBesluit(ReplyActie.Versturen, "Wedstrijd is al ingepland — informatief antwoord");

        // Team/tegenstander niet te herleiden → niet planbaar, afzender moet dat weten.
        if (IsTrue(planner, "teamOnbekend"))
            return new ReplyBesluit(ReplyActie.Versturen, "Team onbekend — niet planbaar");

        if (IsTrue(planner, "multiDatum"))
            return BepaalMultiDatum(planner);

        return IsTrue(planner, "beschikbaar")
            ? new ReplyBesluit(ReplyActie.Onderdrukken, "Planning mogelijk op de gevraagde datum")
            : new ReplyBesluit(ReplyActie.Versturen, "Planning niet mogelijk op de gevraagde datum");
    }

    private static ReplyBesluit BepaalMultiDatum(JObject planner)
    {
        var resultaten = Prop(planner, "resultaten") as JArray;
        if (resultaten == null || resultaten.Count == 0)
            return new ReplyBesluit(ReplyActie.Versturen, "Multidatum zonder resultaten — niet planbaar");

        int mogelijk = 0, nietMogelijk = 0;
        foreach (var item in resultaten)
        {
            if (Prop(item as JObject, "response") is JObject resp && IsTrue(resp, "beschikbaar")) mogelijk++;
            else nietMogelijk++;
        }

        if (nietMogelijk == 0)
            return new ReplyBesluit(ReplyActie.Onderdrukken,
                $"Planning mogelijk op alle {mogelijk} gevraagde datums");

        return new ReplyBesluit(ReplyActie.Versturen, mogelijk == 0
            ? $"Planning niet mogelijk op alle {nietMogelijk} gevraagde datums"
            : $"Gemengde uitkomst: {mogelijk} datum(s) mogelijk, {nietMogelijk} niet");
    }

    // De plannerpipeline mengt anonieme objecten (camelCase: multiDatum, resultaten) met
    // geserialiseerde modellen (PascalCase: Beschikbaar). Case-insensitief lezen houdt de
    // policy los van serialisatie-instellingen die elders kunnen wijzigen.
    private static JToken? Prop(JObject? obj, string naam)
        => obj?.GetValue(naam, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(JObject obj, string naam)
        => Prop(obj, naam)?.Type == JTokenType.Boolean && Prop(obj, naam)!.Value<bool>();
}
