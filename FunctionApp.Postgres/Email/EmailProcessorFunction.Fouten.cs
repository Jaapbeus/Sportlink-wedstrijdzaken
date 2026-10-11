using Planner.Shared;
using FunctionApp.Postgres.Planner;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Email;

public partial class EmailProcessorFunction
{
    private static async Task GeefVerwerkingOpAsync(
        string cs,
        InkomendBericht email,
        int verwerkingId,
        int pogingen,
        string fase,
        IEmailGraphService graphService,
        ILogger log)
    {
        log.LogError(
            "Email {MessageId} opgegeven na {Pogingen} mislukte pogingen ({Fase}) — als gelezen gemarkeerd "
            + "zodat de wachtrij niet blokkeert. Handmatige opvolging nodig via het email-log.",
            email.MessageId, pogingen, fase);

        try
        {
            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, verwerkingId,
                $"Opgegeven na {pogingen} mislukte pogingen ({fase}) — handmatige opvolging nodig");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon opgeven-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    private static async Task LegVerwerkingsFoutVastAsync(
        string cs,
        InkomendBericht email,
        string foutMelding,
        ILogger log)
    {
        try
        {
            var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);
            if (stand is null)
            {
                log.LogWarning(
                    "Email {MessageId}: verwerking mislukt vóórdat er een rij bestond — geen foutstatus vastgelegd",
                    email.MessageId);
                return;
            }

            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, stand.VerwerkingId, foutMelding);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon foutstatus niet vastleggen voor {MessageId}", email.MessageId);
        }
    }

    private static async Task LegVoorBeoordelingNaVerzendPogingAsync(
        string cs,
        InkomendBericht email,
        int verwerkingId,
        IEmailGraphService graphService,
        ILogger log)
    {
        log.LogWarning(
            "Email {MessageId}: er staat een verzendpoging vastgelegd zonder bekende uitkomst voor verwerking "
            + "{Id} — waarschijnlijk is een eerdere invocatie afgebroken tussen versturen en vastleggen. "
            + "Er wordt NIET opnieuw verstuurd; status op Review voor beoordeling via het email-log.",
            email.MessageId, verwerkingId);

        try
        {
            await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Review, null);
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

    internal static async Task RegistreerClassificatieFoutAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        try
        {
            var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);
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
                        cs, email, stand!.VerwerkingId, stand.Pogingen, "AI-classificatie", graphService, log);
                    return;

                case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                    await LegVoorBeoordelingNaVerzendPogingAsync(
                        cs, email, stand!.VerwerkingId, graphService, log);
                    return;

                case VerwerkingsBesluit.HerhaalVerwerking:
                    await SqlEmailPersistenceRepository.VerhoogPogingenAsync(cs, stand!.VerwerkingId);
                    verwerkingId = stand.VerwerkingId;
                    break;

                default:
                    verwerkingId = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs, email, clubCode);
                    break;
            }

            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, verwerkingId, "AI-classificatie mislukt");
            log.LogWarning(
                "Email {MessageId}: AI-classificatie mislukt — poging {Poging} van {Max} vastgelegd, bericht blijft ongelezen voor de volgende poll",
                email.MessageId, (stand?.Pogingen ?? 0) + 1, EmailIdempotentie.MaxPogingen);
        }
        catch (DubbeleMessageIdException)
        {
            log.LogInformation(
                "Email {MessageId}: al door een andere verwerking geregistreerd — classificatiefout niet vastgelegd",
                email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon mislukte AI-classificatie van {MessageId} niet vastleggen", email.MessageId);
        }
    }

    /// <summary>
    /// Teamleider-notificatie bij een herplanverzoek (#66). Sinds #1140 gelijk aan het SQL Server-
    /// origineel: naam+e-mail komen uit <see cref="AllstarsTestDataRepository.GetTeamleiderContactAsync"/>
    /// (zie klassekop) — vóór #1140 gebruikte deze methode i.p.v. daarvan
    /// <c>AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync</c>, dat alleen een e-mailadres
    /// levert en geen naam, dus een generieke aanhef gaf.
    /// </summary>
    private static async Task StuurTeamleiderNotificatieAsync(
        string cs, string clubCode, IEmailGraphService graphService, string teamNaam, string datum, ILogger log)
    {
        try
        {
            var teamleider = await AllstarsTestDataRepository.GetTeamleiderContactAsync(cs, teamNaam, clubCode);
            if (teamleider == null)
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.teambegeleiding — notificatie overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            var plannerNaam = PostgresAppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in public.appsettings");

            DateOnly.TryParse(datum, out var datumDate);
            var datumDisplay = datumDate != default
                ? datumDate.ToString("dddd d MMMM yyyy", new System.Globalization.CultureInfo("nl-NL"))
                : datum;

            var notificatieBody = $"Hoi {teamleider.Naam},\n\n"
                + $"Er is een herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}.\n\n"
                + "De coördinator heeft automatisch gereageerd op dit verzoek. "
                + "Je hoeft zelf geen actie te ondernemen, maar we willen je op de hoogte houden.\n\n"
                + "Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.\n\n"
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

    /// <summary>
    /// Stuurt een teambegeleiding-vraag door naar de begeleider (#168). Zie
    /// <see cref="StuurTeamleiderNotificatieAsync"/> voor dezelfde #1140-lookup.
    /// </summary>
    private static async Task StuurTeamContactBerichtDoorAsync(
        string cs, string clubCode, IEmailGraphService graphService, string teamNaam, InkomendBericht email, ILogger log)
    {
        try
        {
            var coach = await AllstarsTestDataRepository.GetTeamleiderContactAsync(cs, teamNaam, clubCode);
            if (coach == null)
            {
                log.LogInformation("Geen begeleider gevonden voor {Team} — doorsturen overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            // AVG-maatregel: BCC-audit-kopie naar de veldplanner bij het doorsturen van
            // persoonsgegevens. 'plannerEmailAdres' wordt vandaag niet door
            // PostgresAppSettings.LoadSettingsAsync geladen (zelfde gat als in
            // AdminTeambegeleidingFunction.Doorsturen) — vandaar de expliciete null-guard i.p.v.
            // een stille lege waarde.
            var auditKopieAdres = PostgresAppSettings.GetSetting("plannerEmailAdres");
            if (string.IsNullOrWhiteSpace(auditKopieAdres))
            {
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

            // AVG: Reply-To = email.Afzender zodat begeleider rechtstreeks kan antwoorden;
            // BCC veldplanner voor audit; begeleider-email nooit in logs.
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
