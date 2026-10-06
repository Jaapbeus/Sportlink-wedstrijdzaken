using System.Text.Json;

namespace Planner.Shared.Email.Trace;

/// <summary>
/// Leidt uit het plannerresponse-JSON af welke tak/welk ingebouwd sjabloon bij een antwoord hoort
/// (#1568). Spiegelt de volgorde van <c>BerichtPipeline.BouwTemplateAntwoord</c> op beide tiers.
/// </summary>
public static class SjabloonSleutel
{
    private static readonly string[] BeschikbaarheidVlaggen =
        { "wedstrijdAlIngepland", "teamOnbekend", "datumOnbekend", "multiDatum" };
    private static readonly string[] HerplanVlaggen = { "herplanTeLaat", "verzetZonderDatum" };

    /// <summary>Naam van de eerste "true"-vlag in het plannerresponse, of de standaardtak.</summary>
    public static string PlannerTak(string plannerResponseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(plannerResponseJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "standaard";
            foreach (var vlag in BeschikbaarheidVlaggen.Concat(HerplanVlaggen))
                if (doc.RootElement.TryGetProperty(vlag, out var v) && v.ValueKind == JsonValueKind.True) return vlag;
            if (doc.RootElement.TryGetProperty("gewensteDatum", out _) && doc.RootElement.TryGetProperty("beschikbaarheid", out _))
                return "gewensteDatum";
        }
        catch (JsonException) { }
        return "standaard";
    }

    public static string Bepaal(string type, string plannerResponseJson)
    {
        var tak = PlannerTak(plannerResponseJson);
        return type switch
        {
            "BeschikbaarheidCheck" => tak == "standaard" ? "beschikbaarheid_check" : tak,
            "HerplanVerzoek" => tak == "standaard" ? "herplan_verzoek" : tak,
            "TeamContactOpvragen" => "team_contact_opvragen",
            "Bevestiging" => "bevestiging",
            _ => "buiten_scope"
        };
    }
}
