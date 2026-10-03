using Microsoft.Extensions.Logging;

namespace Planner.Shared.Feedback;

/// <summary>
/// Dagelijkse bewaartermijn-run voor feedbackmeldingen (#764), tier-onafhankelijk. De timers van
/// beide tiers roepen dit aan met hun eigen <see cref="IFeedbackStore"/>.
///
/// <para>
/// Volgorde is belangrijk: eerst de issuestatus synchroniseren, dán pas anonimiseren — anders
/// beslist de anonimisering op een verouderd sluitmoment. Lukt de GitHub-controle niet (geen
/// configuratie, geen netwerk, EgressGuard dicht), dan wordt er alleen geanonimiseerd op wat al
/// bekend was; er wordt nooit op een gok gewist.
/// </para>
/// <para>
/// De regels (eigenaarsbesluit 2026-10-03): identiteit (object-ID + naam) blijft bewaard zolang het
/// issue open is en 24 maanden na sluiting; een melding die nooit is gepubliceerd valt na 24 maanden
/// na aanmaak onder dezelfde termijn, zodat een vergeten melding de identiteit niet eeuwig vasthoudt.
/// De meldingstekst zelf blijft onbeperkt bewaard — die staat (na publicatie) al openbaar. De
/// technische context verdwijnt na 90 dagen, de inzageregels na 24 maanden.
/// </para>
/// </summary>
public static class FeedbackRetentieCore
{
    /// <param name="store">De opslag van de actieve tier.</param>
    /// <param name="issueSluiting">
    /// Haalt de sluitstatus van een GitHub-issue op; <c>null</c> als GitHub niet bereikbaar of niet
    /// geconfigureerd is (dan wordt de statussynchronisatie overgeslagen).
    /// </param>
    public static async Task<FeedbackRetentieResultaat> VoerUitAsync(
        IFeedbackStore store,
        Func<int, Task<(bool gelukt, DateTime? geslotenOpUtc)>>? issueSluiting,
        ILogger log,
        DateTime? nuUtc = null)
    {
        var nu = nuUtc ?? DateTime.UtcNow;

        if (issueSluiting is not null)
        {
            var gecontroleerd = 0;
            foreach (var verwijzing in await store.TeControlerenIssuesAsync(FeedbackRetentie.MaxStatusControlesPerRun))
            {
                try
                {
                    var (gelukt, geslotenOp) = await issueSluiting(verwijzing.IssueNummer);
                    if (!gelukt) continue;
                    await store.ZetIssueStatusAsync(verwijzing.FeedbackId, geslotenOp);
                    gecontroleerd++;
                }
                catch (Exception ex)
                {
                    // Eén onbereikbaar issue mag de rest niet tegenhouden — en de run als geheel
                    // moet doorlopen naar het wissen van wat sowieso verlopen is.
                    log.LogWarning(ex, "Issuestatus controleren mislukt voor issue #{Nr}", verwijzing.IssueNummer);
                }
            }
            log.LogInformation("Feedbackretentie: {Aantal} issuestatussen gesynchroniseerd", gecontroleerd);
        }
        else
        {
            log.LogInformation("Feedbackretentie: GitHub-statussynchronisatie overgeslagen (niet geconfigureerd of uitgeschakeld)");
        }

        var resultaat = await store.VoerRetentieUitAsync(nu);
        log.LogInformation(
            "Feedbackretentie klaar: {Geanonimiseerd} geanonimiseerd, {Telemetrie} telemetrie-rijen en {Inzage} inzageregels verwijderd",
            resultaat.Geanonimiseerd, resultaat.TelemetrieVerwijderd, resultaat.InzageVerwijderd);
        return resultaat;
    }
}
