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
/// <para>
/// Sinds #1780 heeft elk antwoord een sleutel (<see cref="AntwoordSjabloonSleutels"/>): een actief databasesjabloon
/// van de club vervangt de ingebouwde tekst, met per antwoord eigen placeholders. Bij een antwoord dat een gevonden
/// wedstrijd nodig heeft (al ingepland, te laat, verzet zonder datum, gewenste datum) geldt het sjabloon alleen als
/// die wedstrijd er is; anders blijft de ingebouwde tekst staan die uitlegt dat er geen wedstrijd is gevonden.
/// </para>
/// </summary>
public static class TemplateAntwoordBouwer
{
    private sealed record Tak(
        Func<JObject, bool> Geldt,
        Func<JObject, ITemplateAntwoordBron, Task<(string onderwerp, string body)>> Bouw);

    // Volgorde = voorrang. Dezelfde volgorde als voor #1627 in BerichtPipeline.BouwTemplateAntwoord.
    // #1568: wedstrijdAlIngepland wordt door VerwerkMetPlannerAsync teruggegeven bij het opponent-pad (#1139);
    // zonder deze tak viel het antwoord door naar het standaard-beschikbaarheidsantwoord ("niet planbaar").
    private static readonly Tak[] BeschikbaarheidTakken =
    {
        new(Vlag("wedstrijdAlIngepland"), (j, bron) => MetWedstrijdAsync(
            bron, j["wedstrijd"], AntwoordSjabloonSleutels.WedstrijdAlIngepland,
            BerichtAntwoordGenerator.WedstrijdPlaceholders, () => bron.WedstrijdAlIngepland(j["wedstrijd"]))),
        new(Vlag("teamOnbekend"), (j, bron) => MetSjabloonAsync(
            bron, AntwoordSjabloonSleutels.TeamOnbekend,
            BerichtAntwoordGenerator.TeamOnbekendPlaceholders(j["tegenstander"]?.ToString()),
            () => bron.TeamOnbekend(j["tegenstander"]?.ToString()))),
        new(Vlag("datumOnbekend"), (_, bron) => MetSjabloonAsync(
            bron, AntwoordSjabloonSleutels.DatumOnbekend, null, bron.DatumOnbekend)),
        new(Vlag("multiDatum"), (j, bron) =>
        {
            var resultaten = LeesMultiDatumResultaten(j);
            var getypeerd = resultaten.Select(r => (r.datum, bron.LeesBeschikbaarheid(r.response))).ToList();
            return MetSjabloonAsync(
                bron, AntwoordSjabloonSleutels.MultiDatum,
                BerichtAntwoordGenerator.MultiDatumPlaceholders(getypeerd, bron.Classificatie),
                () => bron.MultiDatum(resultaten));
        }),
    };

    private static readonly Tak[] HerplanTakken =
    {
        new(Vlag("herplanTeLaat"), (j, bron) =>
        {
            var deadlineDagen = j["deadlineDagen"]?.ToObject<int>() ?? 8;
            var dagenTotWedstrijd = j["dagenTotWedstrijd"]?.ToObject<int>() ?? 0;
            return MetWedstrijdAsync(
                bron, j["wedstrijd"], AntwoordSjabloonSleutels.HerplanTeLaat,
                w => BerichtAntwoordGenerator.HerplanTeLaatPlaceholders(w, deadlineDagen, dagenTotWedstrijd),
                () => bron.HerplanTeLaat(j["wedstrijd"], deadlineDagen, dagenTotWedstrijd));
        }),
        new(Vlag("verzetZonderDatum"), (j, bron) =>
        {
            var vrijeZaterdagen = j["vrijeZaterdagen"]?.ToObject<List<string>>() ?? new List<string>();
            return MetWedstrijdAsync(
                bron, j["wedstrijd"], AntwoordSjabloonSleutels.VerzetZonderDatum,
                w => BerichtAntwoordGenerator.VerzetZonderDatumPlaceholders(w, vrijeZaterdagen, bron.Classificatie.VoegKnvbPdfBijlageToe),
                () => bron.VerzetZonderDatum(j["wedstrijd"], vrijeZaterdagen));
        }),
        new(j => j["gewensteDatum"] != null && j["beschikbaarheid"] != null, (j, bron) =>
        {
            var gewensteDatum = j["gewensteDatum"]?.ToString();
            var beschikbaarheid = bron.LeesBeschikbaarheid(j["beschikbaarheid"]);
            return MetWedstrijdAsync(
                bron, j["wedstrijd"], AntwoordSjabloonSleutels.HerplanGewensteDatum,
                w => BerichtAntwoordGenerator.HerplanGewensteDatumPlaceholders(w, gewensteDatum, beschikbaarheid),
                () => bron.HerplanGewensteDatum(j["wedstrijd"], gewensteDatum, j["beschikbaarheid"]),
                beschikbaarheid.GecontroleerdDagdeel);
        }),
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
            "TeamContactOpvragen" => await MetSjabloonAsync(bron, AntwoordSjabloonSleutels.TeamContactOpvragen, null, bron.TeamContactStandaard),
            "Bevestiging" => await MetSjabloonAsync(bron, AntwoordSjabloonSleutels.Bevestiging, null, bron.BevestigingStandaard),
            _ => await MetSjabloonAsync(bron, AntwoordSjabloonSleutels.BuitenScope, null, bron.BuitenScopeStandaard),
        };
    }

    private static async Task<(string onderwerp, string body)> BouwBeschikbaarheidAsync(
        string plannerResponseJson, ITemplateAntwoordBron bron)
    {
        var json = JObject.Parse(plannerResponseJson);
        var tak = BeschikbaarheidTakken.FirstOrDefault(t => t.Geldt(json));
        return tak != null
            ? await tak.Bouw(json, bron)
            : await bron.BeschikbaarheidStandaardAsync(plannerResponseJson);
    }

    private static async Task<(string onderwerp, string body)> BouwHerplanAsync(
        string plannerResponseJson, ITemplateAntwoordBron bron)
    {
        var json = JObject.Parse(plannerResponseJson);
        // Validatie vóór takkeuze en sjabloonophaling, zoals de oude BouwTemplateAntwoord (Codex #1644, P2).
        bron.ValideerWedstrijd(json["wedstrijd"]);
        var tak = HerplanTakken.FirstOrDefault(t => t.Geldt(json));
        if (tak != null) return await tak.Bouw(json, bron);

        return await MetSjabloonAsync(bron, AntwoordSjabloonSleutels.HerplanVerzoek,
            BerichtAntwoordGenerator.WedstrijdPlaceholders(bron.LeesWedstrijd(json["wedstrijd"])),
            () => bron.HerplanStandaard(json["wedstrijd"], json["herplanOpties"]));
    }

    /// <summary>Databasesjabloon als dat bestaat (override), anders het ingebouwde standaardantwoord.</summary>
    private static async Task<(string onderwerp, string body)> MetSjabloonAsync(
        ITemplateAntwoordBron bron, string sleutel, IDictionary<string, string>? placeholders,
        Func<(string onderwerp, string body)> standaard, string? gecontroleerdDagdeel = null)
        => await bron.ProbeerSjabloonAsync(sleutel, placeholders, gecontroleerdDagdeel) ?? standaard();

    /// <summary>
    /// Zoals <see cref="MetSjabloonAsync"/>, maar het sjabloon geldt alleen als de planner een wedstrijd vond: zonder
    /// wedstrijd zijn de wedstrijdplaceholders leeg en geeft het ingebouwde antwoord de nuttiger uitleg.
    /// </summary>
    private static async Task<(string onderwerp, string body)> MetWedstrijdAsync(
        ITemplateAntwoordBron bron, JToken? wedstrijdToken, string sleutel,
        Func<IAntwoordWedstrijd, IDictionary<string, string>> placeholders,
        Func<(string onderwerp, string body)> standaard, string? gecontroleerdDagdeel = null)
    {
        var wedstrijd = bron.LeesWedstrijd(wedstrijdToken);
        return wedstrijd == null
            ? standaard()
            : await MetSjabloonAsync(bron, sleutel, placeholders(wedstrijd), standaard, gecontroleerdDagdeel);
    }

    private static Func<JObject, bool> Vlag(string naam) => json => json[naam]?.ToObject<bool>() == true;

    private static List<(string datum, JToken? response)> LeesMultiDatumResultaten(JObject json)
    {
        var resultaten = new List<(string datum, JToken? response)>();
        foreach (var item in json["resultaten"]!)
            resultaten.Add((item["datum"]?.ToString() ?? "", item["response"]));
        return resultaten;
    }
}
