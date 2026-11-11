using Newtonsoft.Json;
using Planner.Shared.Email.Trace;

namespace Planner.Shared.Email;

/// <summary>
/// De twee voorwaarden vóór de plannercheck van een beschikbaarheidsvraag uit een bericht (#1861), één keer
/// voor beide databasetiers; de tiers leveren alleen de opzoekingen.
/// <list type="number">
///   <item><description><b>Een herkend eigen team.</b> Zonder team is er niets te plannen: de afzender krijgt de
///   wedervraag om het team. Alleen een bestaande wedstrijd tegen de genoemde tegenstander op precies de gevraagde
///   datum geeft nog een antwoord ("staat al ingepland").</description></item>
///   <item><description><b>Een bekende speelduur.</b> Kan de duur van het team niet uit <c>speeltijden</c> worden
///   bepaald, dan is de check niet uit te voeren. Dat is geen "geen veld beschikbaar": het bericht gaat naar de
///   handmatige afhandeling, zodat de beheerinstructie van de planner nooit bij de afzender terechtkomt.</description></item>
/// </list>
/// </summary>
public static class BeschikbaarheidVoorwaarden
{
    /// <summary>Plannerresponse-vlag voor een check die zonder speelduur niet kon worden uitgevoerd.</summary>
    public const string HandmatigePlanningVlag = "handmatigePlanning";

    /// <summary>
    /// Het antwoord op een beschikbaarheidsvraag zonder herkend eigen team. Een wedstrijd tegen dezelfde
    /// tegenstander op een <i>andere</i> datum wordt bewust niet gebruikt: die zegt niets over welk eigen team nu
    /// vraagt, en bij meerdere eigen teams tegen die club werd stil de oudste gekozen (eigenaarsbesluit #1861, 2A;
    /// vervangt de tak <c>opponent-andere-datum</c> van #1139).
    /// </summary>
    /// <param name="zoekWedstrijdOpDatum">Tier-opzoeking: wedstrijd tegen (tegenstander) op (datum), of <c>null</c>.</param>
    public static async Task<string> ZonderEigenTeamAsync<TWedstrijd>(
        BerichtClassificatie classificatie, IReadOnlyList<string> alleDatums,
        Func<string, DateOnly, Task<TWedstrijd?>> zoekWedstrijdOpDatum, TraceBuilder? trace)
        where TWedstrijd : class
    {
        ArgumentNullException.ThrowIfNull(classificatie);
        ArgumentNullException.ThrowIfNull(alleDatums);
        ArgumentNullException.ThrowIfNull(zoekWedstrijdOpDatum);

        var tegenstander = classificatie.Tegenstander;
        if (!string.IsNullOrWhiteSpace(tegenstander) && alleDatums.Count == 1
            && ModelDatum.TryParse(alleDatums[0], out var datum))
        {
            var wedstrijd = await zoekWedstrijdOpDatum(tegenstander, datum);
            trace?.OpponentPad(wedstrijd is null ? "niet-gevonden" : "opponent-op-datum", wedstrijd is not null);
            if (wedstrijd is not null) return PlannerAntwoordJson.WedstrijdAlIngepland(wedstrijd);
        }

        return PlannerAntwoordJson.TeamOnbekend(tegenstander);
    }

    /// <summary>
    /// Controleert of de speelduur van de categorie bekend is: eerst de categorie zoals hij is, daarna na
    /// <see cref="LeeftijdNormalisatie.Normaliseer"/> ("O23" → "JO23", "Senioren" → "1-99"). Gevonden met een duur
    /// groter dan nul: de categorie wordt de sleutel uit <c>speeltijden</c>, zodat de planner dezelfde rij vindt, en
    /// de uitkomst is <c>null</c>. Anders gaat het bericht naar de bestaande afsluitende handmatige afhandeling
    /// (<see cref="BerichtClassificatie.TeamHandmatigBeoordelen"/>, #1816): geen antwoord, wel review en een
    /// mailboxlabel voor handmatige planning.
    /// </summary>
    /// <param name="zoekSpeeltijd">Tier-opzoeking van één speeltijdsleutel, of <c>null</c> als die niet bestaat.</param>
    public static async Task<string?> ControleerSpeelduurAsync(
        BerichtClassificatie classificatie, Func<string, Task<Speeltijd?>> zoekSpeeltijd, TraceBuilder? trace)
    {
        ArgumentNullException.ThrowIfNull(classificatie);
        ArgumentNullException.ThrowIfNull(zoekSpeeltijd);

        var speeltijd = await ZoekSpeeltijdAsync(classificatie.LeeftijdsCategorie, zoekSpeeltijd);
        if (speeltijd is { WedstrijdTotaal: > 0 })
        {
            classificatie.LeeftijdsCategorie = speeltijd.Leeftijd;
            return null;
        }

        trace?.SpeelduurOnbekend(!string.IsNullOrWhiteSpace(classificatie.LeeftijdsCategorie));
        classificatie.TeamHandmatigBeoordelen = true;
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            [HandmatigePlanningVlag] = true,
            ["status"] = "Handmatige planning nodig: speelduur onbekend",
        });
    }

    private static async Task<Speeltijd?> ZoekSpeeltijdAsync(string? categorie, Func<string, Task<Speeltijd?>> zoek)
    {
        if (string.IsNullOrWhiteSpace(categorie)) return null;

        var zoals = categorie.Trim();
        var gevonden = await zoek(zoals);
        if (gevonden is not null) return gevonden;

        var genormaliseerd = LeeftijdNormalisatie.Normaliseer(zoals);
        return genormaliseerd.Length == 0 || string.Equals(genormaliseerd, zoals, StringComparison.OrdinalIgnoreCase)
            ? null
            : await zoek(genormaliseerd);
    }
}
