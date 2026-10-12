using Planner.Shared;
using Planner.Shared.Email;
using SportlinkFunction.Planner;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    /// <summary>
    /// Legt een mislukte AI-classificatie vast en verhoogt de pogingenteller (#712); de afhandeling
    /// staat tier-onafhankelijk in <see cref="EmailVerwerkingsAfhandeling"/> (#1650).
    /// </summary>
    internal static Task RegistreerClassificatieFoutAsync(
        InkomendBericht email,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
        => EmailVerwerkingKoppeling.RegistreerClassificatieFoutAsync(email, graphService, persistenceService, log);

    /// <summary>
    /// Legt een verwerkingsfout vast voor een bericht waarvan het verwerkingId hier niet bekend is
    /// (#717); zie <see cref="EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync"/> (#1650).
    /// </summary>
    private static Task LegVerwerkingsFoutVastAsync(
        InkomendBericht email,
        string foutMelding,
        IEmailPersistenceService persistenceService,
        ILogger log)
        => EmailVerwerkingKoppeling.LegVerwerkingsFoutVastAsync(email, foutMelding, persistenceService, log);

    private static async Task StuurTeamleiderNotificatieAsync(
        IEmailGraphService graphService, string teamNaam, string datum, ILogger log)
    {
        try
        {
            var teamleider = await SportlinkFunction.Planner.PlannerDataAccess.GetTeamleiderContactAsync(teamNaam);
            if (teamleider == null)
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.Teambegeleiding — notificatie overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            var plannerNaam = SystemUtilities.AppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings");

            DateOnly.TryParse(datum, out var datumDate);
            var datumDisplay = datumDate != default
                ? datumDate.ToString("dddd d MMMM yyyy", new System.Globalization.CultureInfo("nl-NL"))
                : datum;

            var notificatieBody = $"Hoi {teamleider.Naam},\n\n"
                + $"Er is een herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}.\n\n"
                + $"De coördinator heeft automatisch gereageerd op dit verzoek. "
                + $"Je hoeft zelf geen actie te ondernemen, maar we willen je op de hoogte houden.\n\n"
                + $"Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.\n\n"
                + $"Met vriendelijke groet,\n{plannerNaam}";

            await graphService.SendReplyAsync(
                teamleider.Emailadres,
                $"Herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}",
                notificatieBody,
                null);

            log.LogInformation("Teamleider-notificatie verstuurd voor team {Team}", LogWaarde.Schoon(teamNaam));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij versturen teamleider-notificatie voor {Team} — hoofdverwerking niet onderbroken", LogWaarde.Schoon(teamNaam));
        }
    }

    private static async Task StuurTeamContactBerichtDoorAsync(
        IEmailGraphService graphService, string teamNaam, InkomendBericht email, ILogger log)
    {
        try
        {
            var coach = await SportlinkFunction.Planner.PlannerDataAccess.GetTeamleiderContactAsync(teamNaam);
            if (coach == null)
            {
                log.LogInformation("Geen coach gevonden voor {Team} — doorsturen overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            // AVG-maatregel: BCC-audit-kopie naar de veldplanner bij het doorsturen van
            // persoonsgegevens. Stond eerder op de sleutel 'coordinatorEmail' — die bestaat niet in
            // dbo.AppSettings, dus de kopie ging nóóit uit terwijl code en documentatie dat wel
            // beloofden. De juiste sleutel is 'plannerEmailAdres' (kolom PlannerEmailAdres). (#712)
            var auditKopieAdres = SystemUtilities.AppSettings.GetSetting("plannerEmailAdres");
            if (string.IsNullOrWhiteSpace(auditKopieAdres))
            {
                // Stil overslaan mag niet: de audit-kopie is een AVG-maatregel, geen nice-to-have.
                auditKopieAdres = null;
                log.LogWarning(
                    "Instelling 'plannerEmailAdres' ontbreekt of is leeg — teambegeleidingsvraag voor {Team} wordt "
                    + "doorgestuurd ZONDER BCC-audit-kopie. Vul het e-mailadres van de veldplanner in bij Instellingen.",
                    teamNaam);
            }

            var subject = $"[{teamNaam}] vraag van {email.AfzenderNaam}";
            var body = $"Er is een vraag binnengekomen over de begeleiding van {teamNaam}.\n\n"
                     + $"Vraag van: {email.AfzenderNaam}\n\n"
                     + $"---\n{email.Body}\n---\n\n"
                     + "U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.";

            // AVG: Reply-To = email.Afzender zodat coach rechtstreeks kan antwoorden
            // BCC veldplanner voor audit; coach-email nooit in logs
            await graphService.StuurTeamContactDoorAsync(
                [coach.Emailadres], subject, body, email.Afzender, auditKopieAdres);

            log.LogInformation("Teambegeleiding-vraag doorgestuurd voor {Team}", LogWaarde.Schoon(teamNaam));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij doorsturen teambegeleiding-vraag voor {Team} — hoofdverwerking niet onderbroken", LogWaarde.Schoon(teamNaam));
        }
    }
}
