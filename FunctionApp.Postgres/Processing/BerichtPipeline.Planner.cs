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
    /// Beschikbaarheidscheck (#1861): eerst een herkend eigen team (anders de wedervraag om het team, of het
    /// antwoord dat de wedstrijd tegen de genoemde tegenstander op die datum al staat), dan een bruikbare datum,
    /// dan een bekende speelduur (anders handmatige planning), en pas dan multi-datum of de enkelvoudige check.
    /// De keuzes staan in <see cref="BeschikbaarheidVoorwaarden"/>; hier alleen de opzoekingen.
    /// </summary>
    private static async Task<string> VerwerkBeschikbaarheidAsync(
        BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log,
        bool eigenTeamHerkend, string? clubCode, TraceBuilder? trace)
    {
        var alleDatums = BerichtDatumHeuristiek.ExpandDoordeweeksDatums(
            classificatie.GetAlleDatums(), bericht.Onderwerp, bericht.Body ?? "");
        trace?.Datum(alleDatums);

        // De connectiestring wordt pas gelezen als er echt een query volgt: uitgangen zonder query (geen team en
        // geen tegenstander, geen datum) werken ook zonder databaseconfiguratie (#1816).
        if (!eigenTeamHerkend)
            return await BeschikbaarheidVoorwaarden.ZonderEigenTeamAsync(classificatie, alleDatums,
                (tegenstander, datum) => PlannerMatchRepository.FindMatchByOpponentAsync(
                    PostgresDatabaseConfig.ConnectionString, tegenstander, datum, clubCode), trace);

        // #1730: elke modeldatum wordt strikt gevalideerd vóór plannercheck en antwoord; de keuze
        // (lijst vóór losse datum, zonder bruikbare datum "datumOnbekend") staat in Planner.Shared.
        var datums = ModelDatum.VoorBeschikbaarheid(alleDatums, classificatie.Datum);
        if (datums.Count == 0) return PlannerAntwoordJson.DatumOnbekend();

        var cs = PostgresDatabaseConfig.ConnectionString;
        var handmatig = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            categorie => PlannerSettingsRepository.GetSpeeltijdAsync(cs, categorie, PostgresClubScope.Resolve(clubCode)), trace);
        if (handmatig != null) return handmatig;

        if (datums.Count > 1)
            return await PlannerAntwoordJson.MultiDatumAsync(datums, async datum =>
                await AvailabilityService.CheckAvailabilityAsync(cs, BouwBeschikbaarheidRequest(classificatie, datum), log, clubCode));

        // Dezelfde datum als de datumcontrole hierboven: anders loopt de plannercheck over een andere
        // datum dan de rest van de verwerking.
        classificatie.Datum = datums[0];

        var checkResponse = await AvailabilityService.CheckAvailabilityAsync(
            cs, BouwBeschikbaarheidRequest(classificatie, datums[0]), log, clubCode);
        return JsonConvert.SerializeObject(checkResponse);
    }

    /// <summary>
    /// Herplanverzoek: guard clauses (team en datum nodig, wedstrijd gevonden, deadline), dan de
    /// "verzet zonder datum"-tak (#561/#1141), dan het antwoord met gewenste datum of herplanopties.
    /// </summary>
    private static async Task<string> VerwerkHerplanAsync(
        BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log, string cc, string? clubCode,
        ClubAppSettingsSnapshot? clubSettings)
    {
        if (string.IsNullOrEmpty(classificatie.TeamNaam) || string.IsNullOrEmpty(classificatie.Datum)
            || !ModelDatum.TryParse(classificatie.Datum, out var datum))
            return PlannerAntwoordJson.OnvoldoendeHerplanGegevens();

        var cs = PostgresDatabaseConfig.ConnectionString;
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
        BerichtClassificatie classificatie, string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(classificatie.TeamNaam))
            return PlannerAntwoordJson.TeamContact(null, false);

        // clubCode meegeven zodat een dry-run met de demoklub geselecteerd niet de
        // begeleidingscontacten van de productieclub raadpleegt (#677/#706), zelfde
        // reden als het SQL Server-origineel.
        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(
            PostgresDatabaseConfig.ConnectionString, classificatie.TeamNaam, clubCode);
        return PlannerAntwoordJson.TeamContact(classificatie.TeamNaam, contact != null);
    }
}
