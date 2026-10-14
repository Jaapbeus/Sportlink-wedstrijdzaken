using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.TeamResolution;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Processing;

// #1628: de verwerking per verzoektype van VerwerkMetPlannerAsync (CA1502), met dezelfde volgorde van
// controles, JSON-uitvoer en trace-regels als voorheen. Alleen de indeling is veranderd.
internal static partial class BerichtPipeline
{
    /// <summary>
    /// Beschikbaarheidscheck: eerst (indien nodig) het opponent-pad, dan multi-datum of de
    /// enkelvoudige check. Een via de tegenstander herkend eigen team wordt hier op de classificatie
    /// gezet; daarna wordt <c>eigenTeamHerkend</c> nergens meer gelezen.
    /// </summary>
    private static async Task<string> VerwerkBeschikbaarheidAsync(
        string cs, BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log, ITeamResolver teamResolver,
        string cc, bool eigenTeamHerkend, string? clubCode, TraceBuilder? trace)
    {
        var alleDatums = BerichtDatumHeuristiek.ExpandDoordeweeksDatums(
            classificatie.GetAlleDatums(), bericht.Onderwerp, bericht.Body ?? "");
        trace?.Datum(alleDatums);

        var zoek = await ZoekViaTegenstanderAsync(
            cs, classificatie, alleDatums, teamResolver, cc, eigenTeamHerkend, clubCode, log, trace);
        if (zoek.Antwoord != null) return zoek.Antwoord;
        if (zoek.EigenTeam != null)
        {
            classificatie.TeamNaam = zoek.EigenTeam;
            classificatie.LeeftijdsCategorie = TeamNaamNormalisatie.VulLeeftijdsCategorieAan(classificatie.LeeftijdsCategorie, classificatie.TeamNaam, cc);
        }

        // #1730: elke modeldatum wordt strikt gevalideerd vóór plannercheck en antwoord; de keuze
        // (lijst vóór losse datum, zonder bruikbare datum "datumOnbekend") staat in Planner.Shared.
        var datums = ModelDatum.VoorBeschikbaarheid(alleDatums, classificatie.Datum);
        if (datums.Count == 0) return PlannerAntwoordJson.DatumOnbekend();

        if (datums.Count > 1)
            return await PlannerAntwoordJson.MultiDatumAsync(datums, async datum =>
                await AvailabilityService.CheckAvailabilityAsync(cs, BouwBeschikbaarheidRequest(classificatie, datum), log, clubCode));

        // Dezelfde datum als de tegenstander-lookup hierboven gebruikt: anders loopt de
        // plannercheck over een andere datum dan de rest van de verwerking.
        classificatie.Datum = datums[0];

        var checkResponse = await AvailabilityService.CheckAvailabilityAsync(
            cs, BouwBeschikbaarheidRequest(classificatie, datums[0]), log, clubCode);
        return JsonConvert.SerializeObject(checkResponse);
    }

    /// <summary>
    /// #1139: "opponent kan ons team alsnog vinden"-pad. Is ons eigen team niet herkend maar wel een
    /// tegenstander genoemd (en is er precies één datum), dan kan de wedstrijd nog via die
    /// tegenstander gevonden worden — daar staat ons team in. Zelfde tweestaps-
    /// zoekvolgorde als het SQL Server-origineel: eerst op datum, dan zonder datum.
    /// Geeft een lege uitkomst als het pad niet van toepassing is; <c>Antwoord</c> gevuld betekent
    /// dat de pipeline daarmee direct antwoordt; <c>EigenTeam</c> gevuld is het uit de wedstrijd
    /// afgeleide team (de aanroeper zet dat op de classificatie).
    /// </summary>
    private static async Task<TegenstanderZoekUitkomst> ZoekViaTegenstanderAsync(
        string cs, BerichtClassificatie classificatie, List<string> alleDatums, ITeamResolver teamResolver,
        string cc, bool eigenTeamHerkend, string? clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(classificatie.Tegenstander) || eigenTeamHerkend || alleDatums.Count != 1
            || !ModelDatum.TryParse(alleDatums[0], out var opponentCheckDatum))
            return default;

        var wedstrijdOpDatum = await PlannerMatchRepository.FindMatchByOpponentAsync(
            cs, classificatie.Tegenstander!, opponentCheckDatum, clubCode);
        if (wedstrijdOpDatum != null) trace?.OpponentPad("opponent-op-datum", true);
        if (wedstrijdOpDatum != null)
            return new(PlannerAntwoordJson.WedstrijdAlIngepland(wedstrijdOpDatum), null);

        var wedstrijdAndereDatum = await PlannerMatchRepository.FindMatchByOpponentAsync(
            cs, classificatie.Tegenstander!, null, clubCode);
        trace?.OpponentPad(wedstrijdAndereDatum == null ? "niet-gevonden" : "opponent-andere-datum", wedstrijdAndereDatum != null);
        if (wedstrijdAndereDatum == null)
            return new(PlannerAntwoordJson.TeamOnbekend(classificatie.Tegenstander), null);

        var eigenTeam = await BepaalEigenTeamUitWedstrijdAsync(
            wedstrijdAndereDatum.Wedstrijd, teamResolver, cc, log, trace);
        return new(null, eigenTeam);
    }

    /// <summary>
    /// Herplanverzoek: guard clauses (team en datum nodig, wedstrijd gevonden, deadline), dan de
    /// "verzet zonder datum"-tak (#561/#1141), dan het antwoord met gewenste datum of herplanopties.
    /// </summary>
    private static async Task<string> VerwerkHerplanAsync(
        string cs, BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log, string cc, string? clubCode,
        ClubAppSettingsSnapshot? clubSettings)
    {
        if (string.IsNullOrEmpty(classificatie.TeamNaam) || string.IsNullOrEmpty(classificatie.Datum)
            || !ModelDatum.TryParse(classificatie.Datum, out var datum))
            return PlannerAntwoordJson.OnvoldoendeHerplanGegevens();

        var wedstrijd = await PlannerMatchRepository.FindMatchAsync(cs, classificatie.TeamNaam, datum, clubCode);
        if (wedstrijd == null)
            return PlannerAntwoordJson.HerplanWedstrijdNietGevonden(classificatie.TeamNaam, classificatie.Datum);

        var deadlineDagen = clubSettings?.HerplanDeadlineDagen
            ?? (int.TryParse(PostgresAppSettings.GetSetting("herplanDeadlineDagen"), out var dd) ? dd : 8);
        var teLaat = PlannerAntwoordJson.HerplanTeLaat(wedstrijd, wedstrijd.Datum, deadlineDagen);
        if (teLaat != null) return teLaat;

        // #561: een tegenstander die om herplannen vraagt zonder concrete
        // nieuwe datum krijgt géén toegezegde datum van de AI — dat moet eerst
        // met de begeleiding van ons eigen team worden afgestemd. Val terug op
        // het bestaande gedrag als deze flow niet (volledig) geconfigureerd is.
        if (classificatie.NamensWie == NamensWie.Tegenstander
            && string.IsNullOrWhiteSpace(classificatie.GewensteDatum))
        {
            var verzetZonderDatumJson = await BouwVerzetZonderDatumResponseAsync(
                cs, classificatie, wedstrijd, deadlineDagen, clubSettings, cc, log);
            if (verzetZonderDatumJson != null)
                return verzetZonderDatumJson;
        }

        if (!string.IsNullOrEmpty(classificatie.GewensteDatum))
        {
            var gewenstRequest = new CheckAvailabilityRequest
            {
                Datum = classificatie.GewensteDatum,
                LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                TeamNaam = classificatie.TeamNaam,
                Dagdeel = classificatie.Dagdeel
            };
            var beschikbaarheid = await AvailabilityService.CheckAvailabilityAsync(cs, gewenstRequest, log, clubCode);
            return PlannerAntwoordJson.HerplanMetGewensteDatum(wedstrijd, classificatie.GewensteDatum, beschikbaarheid);
        }

        var herplanRequest = new HerplanCheckRequest
        {
            Wedstrijdcode = wedstrijd.Wedstrijdcode,
            VoorkeurTijd = classificatie.AanvangsTijd,
            Dagdeel = classificatie.Dagdeel,
            Richting = BerichtTekstHeuristiek.DetecteerRichting(bericht.Onderwerp, bericht.Body ?? "")
        };
        var herplanResponse = await RescheduleService.CheckRescheduleAvailabilityAsync(cs, herplanRequest, log, clubCode);
        return PlannerAntwoordJson.HerplanOpties(wedstrijd, herplanResponse);
    }

    private static async Task<string> VerwerkTeamContactAsync(
        string cs, BerichtClassificatie classificatie, string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(classificatie.TeamNaam))
            return PlannerAntwoordJson.TeamContact(null, false);

        // clubCode meegeven zodat een dry-run met de demoklub geselecteerd niet de
        // begeleidingscontacten van de productieclub raadpleegt (#677/#706), zelfde
        // reden als het SQL Server-origineel.
        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(
            cs, classificatie.TeamNaam, clubCode);
        return PlannerAntwoordJson.TeamContact(classificatie.TeamNaam, contact != null);
    }
}
