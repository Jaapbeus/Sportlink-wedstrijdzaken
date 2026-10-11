using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using FunctionApp.Postgres.Sync;
using FunctionApp.Postgres.TeamResolution;

namespace FunctionApp.Postgres.Processing;

// #1628: de verwerking per verzoektype van VerwerkMetPlannerAsync (CA1502), met dezelfde volgorde van
// controles, JSON-uitvoer en trace-regels als voorheen. Alleen de indeling is veranderd.
internal static partial class BerichtPipeline
{
    /// <summary>Uitkomst van <see cref="ZoekViaTegenstanderAsync"/>: óf een kant-en-klaar antwoord, óf het via de tegenstander herkende eigen team.</summary>
    private readonly record struct TegenstanderZoekUitkomst(string? Antwoord, string? EigenTeam);

    /// <summary>
    /// Beschikbaarheidscheck: eerst (indien nodig) het opponent-pad, dan multi-datum of de
    /// enkelvoudige check. Een via de tegenstander herkend eigen team wordt hier op de classificatie
    /// gezet; daarna wordt <c>eigenTeamHerkend</c> nergens meer gelezen.
    /// </summary>
    private static async Task<string> VerwerkBeschikbaarheidAsync(
        string cs, BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log, ITeamResolver teamResolver,
        string cc, bool eigenTeamHerkend, string? clubCode, TraceBuilder? trace)
    {
        var alleDatums = ExpandDoordeweeksDatums(
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

        if (alleDatums.Count > 1)
            return await VerwerkMultiDatumAsync(cs, classificatie, alleDatums, clubCode, log);

        // Dezelfde datum als de tegenstander-lookup hierboven gebruikt: anders loopt de
        // plannercheck over een andere datum dan de rest van de verwerking.
        var primaireDatum = KiesPrimaireDatum(alleDatums, classificatie.Datum);
        if (string.IsNullOrWhiteSpace(primaireDatum) || !DateOnly.TryParse(primaireDatum, out _))
        {
            // Zonder bruikbare datum heeft een plannercheck geen zin: die levert een
            // interne foutstring ("Ongeldige datum: .") op die in het antwoord aan de
            // afzender zou belanden.
            return JsonConvert.SerializeObject(new { datumOnbekend = true });
        }
        classificatie.Datum = primaireDatum;

        var checkRequest = BouwBeschikbaarheidRequest(classificatie, primaireDatum);
        var checkResponse = await AvailabilityService.CheckAvailabilityAsync(cs, checkRequest, log, clubCode);
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
        bool heeftExterneTegenstander = !string.IsNullOrWhiteSpace(classificatie.Tegenstander);
        if (!(heeftExterneTegenstander && !eigenTeamHerkend && alleDatums.Count == 1
              && DateOnly.TryParse(alleDatums[0], out var opponentCheckDatum)))
            return default;

        var wedstrijdOpDatum = await PlannerMatchRepository.FindMatchByOpponentAsync(
                cs, classificatie.Tegenstander!, opponentCheckDatum, clubCode);
        if (wedstrijdOpDatum != null) trace?.OpponentPad("opponent-op-datum", true);
        if (wedstrijdOpDatum != null)
            return new(JsonConvert.SerializeObject(new { wedstrijdAlIngepland = true, wedstrijd = wedstrijdOpDatum }), null);

        var wedstrijdAndereDatum = await PlannerMatchRepository.FindMatchByOpponentAsync(
                cs, classificatie.Tegenstander!, null, clubCode);
        trace?.OpponentPad(wedstrijdAndereDatum == null ? "niet-gevonden" : "opponent-andere-datum", wedstrijdAndereDatum != null);
        if (wedstrijdAndereDatum == null)
            return new(JsonConvert.SerializeObject(new { teamOnbekend = true, tegenstander = classificatie.Tegenstander }), null);

        var eigenTeam = await BepaalEigenTeamUitWedstrijdAsync(
            wedstrijdAndereDatum.Wedstrijd, teamResolver, cc, log, trace);
        return new(null, eigenTeam);
    }

    private static async Task<string> VerwerkMultiDatumAsync(
        string cs, BerichtClassificatie classificatie, List<string> alleDatums, string? clubCode, ILogger log)
    {
        var multiResults = new List<object>();
        foreach (var datum in alleDatums)
        {
            var req = BouwBeschikbaarheidRequest(classificatie, datum);
            var resp = await AvailabilityService.CheckAvailabilityAsync(cs, req, log, clubCode);
            multiResults.Add(new { datum, response = resp });
        }
        return JsonConvert.SerializeObject(new { multiDatum = true, resultaten = multiResults });
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
            || !DateOnly.TryParse(classificatie.Datum, out var datum))
            return JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

        var wedstrijd = await PlannerMatchRepository.FindMatchAsync(cs, classificatie.TeamNaam, datum, clubCode);
        if (wedstrijd == null)
            return JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {classificatie.TeamNaam} op {classificatie.Datum}" });

        var deadlineDagen = clubSettings?.HerplanDeadlineDagen
            ?? (int.TryParse(PostgresAppSettings.GetSetting("herplanDeadlineDagen"), out var dd) ? dd : 8);
        var teLaat = BepaalHerplanTeLaat(wedstrijd, deadlineDagen);
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

        return await BouwHerplanAntwoordAsync(cs, classificatie, bericht, wedstrijd, clubCode, log);
    }

    /// <summary>Antwoord <c>herplanTeLaat</c> als de wedstrijd binnen de deadline valt, anders <c>null</c>.</summary>
    private static string? BepaalHerplanTeLaat(ZoekWedstrijdResponse wedstrijd, int deadlineDagen)
    {
        if (!DateOnly.TryParse(wedstrijd.Datum, out var wedstrijdDatum)) return null;

        var dagenTotWedstrijd = (wedstrijdDatum.ToDateTime(TimeOnly.MinValue) - DateTime.Today).TotalDays;
        if (dagenTotWedstrijd >= deadlineDagen) return null;

        return JsonConvert.SerializeObject(new
        {
            herplanTeLaat = true,
            wedstrijd,
            deadlineDagen,
            dagenTotWedstrijd = (int)dagenTotWedstrijd
        });
    }

    private static async Task<string> BouwHerplanAntwoordAsync(
        string cs, BerichtClassificatie classificatie, InkomendBericht bericht, ZoekWedstrijdResponse wedstrijd,
        string? clubCode, ILogger log)
    {
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
            return JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum = classificatie.GewensteDatum, beschikbaarheid });
        }

        var herplanRequest = new HerplanCheckRequest
        {
            Wedstrijdcode = wedstrijd.Wedstrijdcode,
            VoorkeurTijd = classificatie.AanvangsTijd,
            Dagdeel = classificatie.Dagdeel,
            Richting = BerichtTekstHeuristiek.DetecteerRichting(bericht.Onderwerp, bericht.Body ?? "")
        };
        var herplanResponse = await RescheduleService.CheckRescheduleAvailabilityAsync(cs, herplanRequest, log, clubCode);
        return JsonConvert.SerializeObject(new { wedstrijd, herplanOpties = herplanResponse });
    }

    private static async Task<string> VerwerkTeamContactAsync(
        string cs, BerichtClassificatie classificatie, string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(classificatie.TeamNaam))
            return JsonConvert.SerializeObject(new { teamContactOpgevraagd = true, teamNaam = (string?)null, coachGevonden = false });

        // clubCode meegeven zodat een dry-run met de demoklub geselecteerd niet de
        // begeleidingscontacten van de productieclub raadpleegt (#677/#706), zelfde
        // reden als het SQL Server-origineel.
        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(
            cs, classificatie.TeamNaam, clubCode);
        return JsonConvert.SerializeObject(new
        {
            teamContactOpgevraagd = true,
            teamNaam = classificatie.TeamNaam,
            coachGevonden = contact != null
        });
    }
}
