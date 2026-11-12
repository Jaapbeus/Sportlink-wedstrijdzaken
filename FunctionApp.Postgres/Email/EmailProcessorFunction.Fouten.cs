using Planner.Shared;
using Planner.Shared.Email;
using FunctionApp.Postgres.Planner;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Email;

public partial class EmailProcessorFunction
{
    // Sanitiseert een foutmelding voor opslag in de DB — verwijdert e-mailadressen en knipt af. (#420)
    private static string SanitizeFoutMelding(string message)
        => EmailSanitizer.SanitizeFoutMelding(message);

    /// <summary>
    /// Legt een mislukte AI-classificatie vast en verhoogt de pogingenteller; de afhandeling staat
    /// tier-onafhankelijk in <see cref="EmailVerwerkingsAfhandeling"/> (#1650).
    /// </summary>
    internal static Task RegistreerClassificatieFoutAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
        => EmailVerwerkingKoppeling.RegistreerClassificatieFoutAsync(cs, clubCode, email, graphService, log);

    /// <summary>
    /// Legt een verwerkingsfout vast voor een bericht waarvan het verwerkingId hier niet bekend is;
    /// zie <see cref="EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync"/> (#1650).
    /// </summary>
    internal static Task LegVerwerkingsFoutVastAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        string foutMelding,
        ILogger log)
        => EmailVerwerkingKoppeling.LegVerwerkingsFoutVastAsync(cs, clubCode, email, foutMelding, log);

    /// <summary>
    /// Teamleider-notificatie bij een herplanverzoek (#66). Sinds #1140 gelijk aan het SQL Server-
    /// origineel: naam+e-mail komen uit <see cref="AllstarsTestDataRepository.GetTeamleiderContactAsync"/>
    /// (zie klassekop) — vóór #1140 gebruikte deze methode i.p.v. daarvan
    /// <c>AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync</c>, dat alleen een e-mailadres
    /// levert en geen naam, dus een generieke aanhef gaf.
    /// </summary>
    internal static async Task StuurTeamleiderNotificatieAsync(
        string cs, string clubCode, IEmailGraphService graphService, string teamNaam, string datum, ILogger log,
        Func<string, Task<TeamleiderContact?>>? zoekContact = null, Func<string, string?>? instelling = null)
    {
        try
        {
            var teamleider = await (zoekContact?.Invoke(teamNaam) ?? AllstarsTestDataRepository.GetTeamleiderContactAsync(cs, teamNaam, clubCode));
            if (teamleider == null)
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.teambegeleiding — notificatie overgeslagen", LogWaarde.Schoon(teamNaam));
                return;
            }

            var plannerNaam = (instelling ?? PostgresAppSettings.GetSetting)("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in public.appsettings");

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
