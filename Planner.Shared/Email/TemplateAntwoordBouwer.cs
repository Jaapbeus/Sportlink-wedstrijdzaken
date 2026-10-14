using Newtonsoft.Json.Linq;
using Planner.Shared.Email.Trace;

namespace Planner.Shared.Email;

/// <summary>
/// Kiest het antwoord op een binnenkomend bericht uit het plannerresponse (#1627, was
/// <c>BerichtPipeline.BouwTemplateAntwoord</c> op elke databasetier). Pure JSON-naar-takkeuze:
/// geen databasetoegang en geen tier-typen; die zitten achter <see cref="ITemplateAntwoordBron"/>.
/// <para>
/// De volgorde van de takken is gedrag: de eerste vlag die geldt wint. Per verzoektype is de
/// volgorde een geordende lijst, zodat een nieuwe tak één regel is en de volgorde zichtbaar blijft.
/// </para>
/// </summary>
public static class TemplateAntwoordBouwer
{
    private sealed record Tak(
        Func<JObject, bool> Geldt,
        Func<JObject, ITemplateAntwoordBron, (string onderwerp, string body)> Bouw);

    // Volgorde = voorrang. Dezelfde volgorde als voor #1627 in BerichtPipeline.BouwTemplateAntwoord.
    // #1568: wedstrijdAlIngepland wordt door VerwerkMetPlannerAsync teruggegeven bij het opponent-pad (#1139);
    // zonder deze tak viel het antwoord door naar het standaard-beschikbaarheidsantwoord ("niet planbaar").
    private static readonly Tak[] BeschikbaarheidTakken =
    {
        new(Vlag("wedstrijdAlIngepland"), (j, bron) => bron.WedstrijdAlIngepland(j["wedstrijd"])),
        new(Vlag("teamOnbekend"), (j, bron) => bron.TeamOnbekend(j["tegenstander"]?.ToString())),
        new(Vlag("datumOnbekend"), (_, bron) => bron.DatumOnbekend()),
        new(Vlag("multiDatum"), (j, bron) => bron.MultiDatum(LeesMultiDatumResultaten(j))),
    };

    private static readonly Tak[] HerplanTakken =
    {
        new(Vlag("herplanTeLaat"), (j, bron) => bron.HerplanTeLaat(
            j["wedstrijd"],
            j["deadlineDagen"]?.ToObject<int>() ?? 8,
            j["dagenTotWedstrijd"]?.ToObject<int>() ?? 0)),
        new(Vlag("verzetZonderDatum"), (j, bron) => bron.VerzetZonderDatum(
            j["wedstrijd"],
            j["vrijeZaterdagen"]?.ToObject<List<string>>() ?? new List<string>())),
        new(j => j["gewensteDatum"] != null && j["beschikbaarheid"] != null, (j, bron) => bron.HerplanGewensteDatum(
            j["wedstrijd"], j["gewensteDatum"]?.ToString(), j["beschikbaarheid"])),
    };

    /// <param name="verzoekType">Naam van de <see cref="VerzoekType"/>-waarde (zoals <see cref="SjabloonSleutel.Bepaal"/>).</param>
    public static async Task<(string onderwerp, string body)> BouwAsync(
        string verzoekType, string plannerResponseJson, ITemplateAntwoordBron bron, TraceBuilder? trace)
    {
        trace?.Antwoordkeuze(verzoekType, plannerResponseJson);
        return verzoekType switch
        {
            "BeschikbaarheidCheck" => await BouwBeschikbaarheidAsync(plannerResponseJson, bron),
            "HerplanVerzoek" => await BouwHerplanAsync(plannerResponseJson, bron),
            "TeamContactOpvragen" => await MetSjabloonOfStandaardAsync(bron, "team_contact_opvragen", bron.TeamContactStandaard),
            "Bevestiging" => await MetSjabloonOfStandaardAsync(bron, "bevestiging", bron.BevestigingStandaard),
            _ => await MetSjabloonOfStandaardAsync(bron, "buiten_scope", bron.BuitenScopeStandaard),
        };
    }

    private static async Task<(string onderwerp, string body)> BouwBeschikbaarheidAsync(
        string plannerResponseJson, ITemplateAntwoordBron bron)
    {
        var json = JObject.Parse(plannerResponseJson);
        var tak = BeschikbaarheidTakken.FirstOrDefault(t => t.Geldt(json));
        return tak != null
            ? tak.Bouw(json, bron)
            : await bron.BeschikbaarheidStandaardAsync(plannerResponseJson);
    }

    private static async Task<(string onderwerp, string body)> BouwHerplanAsync(
        string plannerResponseJson, ITemplateAntwoordBron bron)
    {
        var json = JObject.Parse(plannerResponseJson);
        // Validatie vóór takkeuze en sjabloonophaling, zoals de oude BouwTemplateAntwoord (Codex #1644, P2).
        bron.ValideerWedstrijd(json["wedstrijd"]);
        var tak = HerplanTakken.FirstOrDefault(t => t.Geldt(json));
        if (tak != null) return tak.Bouw(json, bron);

        return await MetSjabloonOfStandaardAsync(bron, "herplan_verzoek",
            () => bron.HerplanStandaard(json["wedstrijd"], json["herplanOpties"]));
    }

    /// <summary>Databasesjabloon als dat bestaat (override), anders het ingebouwde standaardantwoord.</summary>
    private static async Task<(string onderwerp, string body)> MetSjabloonOfStandaardAsync(
        ITemplateAntwoordBron bron, string sleutel, Func<(string onderwerp, string body)> standaard)
        => await bron.ProbeerSjabloonAsync(sleutel) ?? standaard();

    private static Func<JObject, bool> Vlag(string naam) => json => json[naam]?.ToObject<bool>() == true;

    private static List<(string datum, JToken? response)> LeesMultiDatumResultaten(JObject json)
    {
        var resultaten = new List<(string datum, JToken? response)>();
        foreach (var item in json["resultaten"]!)
            resultaten.Add((item["datum"]?.ToString() ?? "", item["response"]));
        return resultaten;
    }
}
