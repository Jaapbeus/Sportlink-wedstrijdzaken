using Planner.Shared;
using Planner.Shared.Email;
using SportlinkFunction.Planner;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    // Sanitiseert een foutmelding voor opslag in de DB — verwijdert e-mailadressen en knipt af. (#420)
    private static string SanitizeFoutMelding(string message)
        => EmailSanitizer.SanitizeFoutMelding(message);

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
    internal static Task LegVerwerkingsFoutVastAsync(
        InkomendBericht email,
        string foutMelding,
        IEmailPersistenceService persistenceService,
        ILogger log)
        => EmailVerwerkingKoppeling.LegVerwerkingsFoutVastAsync(email, foutMelding, persistenceService, log);

    internal static async Task StuurTeamleiderNotificatieAsync(
        IEmailGraphService graphService, string teamNaam, string datum, ILogger log,
        Func<string, Task<TeamleiderContact?>>? zoekContact = null, Func<string, string?>? instelling = null)
    {
        try
        {
            var teamleider = await (zoekContact?.Invoke(teamNaam) ?? SportlinkFunction.Planner.PlannerDataAccess.GetTeamleiderContactAsync(teamNaam));
            if (teamleider == null)
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.Teambegeleiding — notificatie overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            var plannerNaam = (instelling ?? SystemUtilities.AppSettings.GetSetting)("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings");

            var bericht = AntwoordTekst.TeamleiderNotificatie(teamNaam, datum, teamleider.Naam, plannerNaam);
            await graphService.SendReplyAsync(teamleider.Emailadres, bericht.Onderwerp, bericht.Inhoud, null);

            log.LogInformation("Teamleider-notificatie verstuurd voor team {Team}", LogWaarde.Schoon(teamNaam));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij versturen teamleider-notificatie voor {Team} — hoofdverwerking niet onderbroken", LogWaarde.Schoon(teamNaam));
        }
    }
}
