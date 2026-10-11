using Planner.Shared;
using SportlinkFunction.Planner;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    /// <summary>
    /// Geeft een bericht definitief op na <see cref="EmailIdempotentie.MaxPogingen"/> mislukte
    /// pogingen: status Fout met een verklarende melding, en als gelezen markeren zodat het de
    /// wachtrij van 10 oudste ongelezen berichten niet blijft bezetten. Er is nooit een antwoord
    /// verstuurd op dit pad, dus de coördinator moet het bericht handmatig oppakken — het email-log
    /// is daarvoor het spoor.
    /// </summary>
    private static async Task GeefVerwerkingOpAsync(
        InkomendBericht email,
        int verwerkingId,
        int pogingen,
        string fase,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        log.LogError(
            "Email {MessageId} opgegeven na {Pogingen} mislukte pogingen ({Fase}) — als gelezen gemarkeerd "
            + "zodat de wachtrij niet blokkeert. Handmatige opvolging nodig via het email-log.",
            email.MessageId, pogingen, fase);

        try
        {
            await persistenceService.UpdateFoutAsync(verwerkingId,
                $"Opgegeven na {pogingen} mislukte pogingen ({fase}) — handmatige opvolging nodig");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon opgeven-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    /// <summary>
    /// Legt een verwerkingsfout vast voor een bericht waarvan het verwerkingId op deze plek niet bekend
    /// is: de fout kan overal in de verwerking zijn opgetreden, ook vóór de rij bestond.
    /// <para>
    /// Sinds #717 muteert de foutafhandeling op <c>Id</c> en niet meer op MessageId, dus wordt het Id
    /// hier eerst opgezocht. Bestaat er geen rij, dan is er ook niets vast te leggen — de fout is dan al
    /// gelogd en het bericht blijft ongelezen voor de volgende poll.
    /// </para>
    /// </summary>
    private static async Task LegVerwerkingsFoutVastAsync(
        InkomendBericht email,
        string foutMelding,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        try
        {
            var stand = await persistenceService.HaalVerwerkingStandOpAsync(email.MessageId);
            if (stand is null)
            {
                log.LogWarning(
                    "Email {MessageId}: verwerking mislukt vóórdat er een rij bestond — geen foutstatus vastgelegd",
                    email.MessageId);
                return;
            }

            await persistenceService.UpdateFoutAsync(stand.VerwerkingId, foutMelding);
        }
        catch (Exception ex)
        {
            // Een fout bij het vastleggen van een fout mag nooit cascaderen: de oorspronkelijke fout is
            // al gelogd en het bericht blijft ongelezen voor de volgende poll.
            log.LogWarning(ex, "Kon foutstatus niet vastleggen voor {MessageId}", email.MessageId);
        }
    }

    /// <summary>
    /// Handelt een verwerking af waarvan de verzenduitkomst onbekend is (#716): niet opnieuw versturen,
    /// maar op <see cref="EmailStatus.Review"/> zetten en als gelezen markeren.
    /// <para>
    /// Opnieuw versturen zou de afzender een tweede antwoord kunnen geven; niets doen zou het bericht
    /// elke poll laten terugkomen en de wachtrij van tien oudste ongelezen berichten bezetten. De
    /// coördinator ziet het bericht terug in het email-log met status Review en kan zelf vaststellen of
    /// er al een antwoord is aangekomen.
    /// </para>
    /// </summary>
    private static async Task LegVoorBeoordelingNaVerzendPogingAsync(
        InkomendBericht email,
        int verwerkingId,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        log.LogWarning(
            "Email {MessageId}: er staat een verzendpoging vastgelegd zonder bekende uitkomst voor verwerking "
            + "{Id} — waarschijnlijk is een eerdere invocatie afgebroken tussen versturen en vastleggen. "
            + "Er wordt NIET opnieuw verstuurd; status op Review voor beoordeling via het email-log.",
            email.MessageId, verwerkingId);

        try
        {
            await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon Review-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        try
        {
            await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graphService.SetCategoriesAsync(email.MessageId, EmailCategorieLabels.GeenAiAntwoord);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling mislukt voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    /// <summary>
    /// Legt een mislukte AI-classificatie vast en verhoogt de pogingenteller.
    /// <para>
    /// Zonder rij in de database is er geen spoor én geen teller: het bericht blijft ongelezen, komt
    /// elke poll terug en kost elke keer opnieuw een AI-call. Tien zulke berichten blokkeren de hele
    /// wachtrij. Op dit pad wordt nooit iets verstuurd, dus een extra poging kan geen dubbel antwoord
    /// veroorzaken. (#712)
    /// </para>
    /// </summary>
    internal static async Task RegistreerClassificatieFoutAsync(
        InkomendBericht email,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        try
        {
            var stand = await persistenceService.HaalVerwerkingStandOpAsync(email.MessageId);
            int verwerkingId;

            switch (EmailIdempotentie.Bepaal(stand))
            {
                case VerwerkingsBesluit.OverslaanAlAfgerond:
                    log.LogInformation(
                        "Email {MessageId}: classificatie mislukt maar verwerking is al definitief afgehandeld — als gelezen gemarkeerd",
                        email.MessageId);
                    await graphService.MarkAsReadAsync(email.MessageId);
                    return;

                case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                    await GeefVerwerkingOpAsync(
                        email, stand!.VerwerkingId, stand.Pogingen, "AI-classificatie",
                        graphService, persistenceService, log);
                    return;

                case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                    // Kan alleen als een eerdere poging al aan het versturen toe was. Dan is opnieuw
                    // classificeren zinloos en opnieuw versturen onveilig. (#716)
                    await LegVoorBeoordelingNaVerzendPogingAsync(
                        email, stand!.VerwerkingId, graphService, persistenceService, log);
                    return;

                case VerwerkingsBesluit.HerhaalVerwerking:
                    await persistenceService.VerhoogPogingenAsync(stand!.VerwerkingId);
                    verwerkingId = stand.VerwerkingId;
                    break;

                default:
                    verwerkingId = await persistenceService.InsertEmailVerwerkingAsync(email);
                    break;
            }

            await persistenceService.UpdateFoutAsync(verwerkingId, "AI-classificatie mislukt");
            log.LogWarning(
                "Email {MessageId}: AI-classificatie mislukt — poging {Poging} van {Max} vastgelegd, bericht blijft ongelezen voor de volgende poll",
                email.MessageId, (stand?.Pogingen ?? 0) + 1, EmailIdempotentie.MaxPogingen);
        }
        catch (DubbeleMessageIdException)
        {
            // Een andere invocatie registreerde het bericht tussen de stand-lezing en deze INSERT.
            // Geen fout: die verwerking is leidend en heeft een eigen pogingenteller. (#707)
            log.LogInformation(
                "Email {MessageId}: al door een andere verwerking geregistreerd — classificatiefout niet vastgelegd",
                email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon mislukte AI-classificatie van {MessageId} niet vastleggen", email.MessageId);
        }
    }

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
