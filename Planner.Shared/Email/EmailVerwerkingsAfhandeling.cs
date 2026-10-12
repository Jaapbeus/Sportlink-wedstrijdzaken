using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>
/// Opslagpoort voor de verwerkingsrij van één inkomend bericht (#1650). De tier bindt de adapter
/// aan dat bericht en vertaalt naar zijn eigen persistentie; hier komen geen databasetypen voor.
/// </summary>
public interface IEmailVerwerkingOpslag
{
    /// <summary>Stand van een eerdere verwerking, of <c>null</c> als het bericht nog niet geregistreerd is.</summary>
    Task<EmailVerwerkingStandInfo?> HaalStandOpAsync();

    /// <summary>Maakt een nieuwe verwerkingsrij aan en geeft het Id terug.</summary>
    Task<int> RegistreerNieuweVerwerkingAsync();

    /// <summary>
    /// Is <paramref name="ex"/> de melding dat een andere invocatie het bericht al registreerde
    /// (dubbele MessageId, #707)? De exceptionklasse is tier-eigen, vandaar deze vraag.
    /// </summary>
    bool IsGelijktijdigeRegistratie(Exception ex);

    Task VerhoogPogingenAsync(int verwerkingId);

    Task UpdateFoutAsync(int verwerkingId, string foutMelding);

    /// <summary>Zet de verwerking op status Review (zonder classificatie).</summary>
    Task ZetOpReviewAsync(int verwerkingId);
}

/// <summary>Mailboxacties die de gedeelde afhandeling nodig heeft (#1650); de tier vertaalt naar Graph.</summary>
public interface IEmailMailboxActies
{
    Task MarkeerGelezenAsync(string messageId);

    /// <summary>Zorgt voor de categorie "Geen AI antwoord" en zet hem op het bericht.</summary>
    Task LabelGeenAiAntwoordAsync(string messageId);
}

/// <summary>
/// Tier-onafhankelijke afhandeling rond de verwerkingsrij van één bericht (#1650): het
/// idempotentiebesluit uitvoeren (#712), opgeven na te veel pogingen, een onbekende verzenduitkomst
/// ter beoordeling neerleggen (#716), classificatie- en verwerkingsfouten vastleggen en de
/// buiten-scope-afhandeling. Datatoegang en Graph lopen via <see cref="IEmailVerwerkingOpslag"/> en
/// <see cref="IEmailMailboxActies"/>; de fase-orkestratie zelf blijft per tier (#1271).
/// </summary>
public static class EmailVerwerkingsAfhandeling
{
    /// <summary>
    /// Bepaalt onder welk verwerkingId dit bericht verder verwerkt wordt: hergebruik van een
    /// niet-afgeronde rij, of een nieuwe rij. Retourneert <c>null</c> als de verwerking hier moet
    /// stoppen. De guard kijkt naar de EINDSTATUS en niet naar het bestaan van de rij (#712).
    /// </summary>
    public static async Task<int?> BepaalVerwerkingIdAsync(
        string messageId, IEmailVerwerkingOpslag opslag, IEmailMailboxActies mailbox, ILogger log)
    {
        var stand = await opslag.HaalStandOpAsync();

        switch (stand?.Besluit ?? VerwerkingsBesluit.NieuweVerwerking)
        {
            case VerwerkingsBesluit.OverslaanAlAfgerond:
                log.LogInformation("Email {MessageId} is al definitief afgehandeld (status {Status}), overslaan",
                    messageId, stand!.Status);
                await mailbox.MarkeerGelezenAsync(messageId);
                return null;

            case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                await GeefVerwerkingOpAsync(
                    messageId, stand!.VerwerkingId, stand.Pogingen, "verwerking", opslag, mailbox, log);
                return null;

            case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                await LegVoorBeoordelingNaVerzendPogingAsync(messageId, stand!.VerwerkingId, opslag, mailbox, log);
                return null;

            case VerwerkingsBesluit.HerhaalVerwerking:
                await opslag.VerhoogPogingenAsync(stand!.VerwerkingId);
                log.LogWarning(
                    "Email {MessageId} was niet afgerond (status {Status}) — verwerking {Id} wordt hervat, poging {Poging} van {Max}",
                    messageId, stand.Status, stand.VerwerkingId, stand.Pogingen + 1, VerwerkingsBesluitRegels.MaxPogingen);
                return stand.VerwerkingId;

            default:
                try
                {
                    // DB INSERT — classificatie is al gedaan in fase 1, resultaat wordt evt. verfijnd in fase 2
                    return await opslag.RegistreerNieuweVerwerkingAsync();
                }
                catch (Exception ex) when (opslag.IsGelijktijdigeRegistratie(ex))
                {
                    // Check-then-act: tussen het lezen van de stand en deze INSERT heeft een andere
                    // invocatie hetzelfde bericht geregistreerd. Dit mag géén fout worden: de rij van
                    // die andere verwerking zou dan op 'Fout' komen, terwijl die het antwoord juist wél
                    // kan versturen. Bericht ongelezen laten — de andere verwerking handelt het af. (#707)
                    log.LogWarning(
                        "Email {MessageId} is gelijktijdig door een andere verwerking geregistreerd — deze poging stopt zonder foutstatus",
                        messageId);
                    return null;
                }
        }
    }

    /// <summary>
    /// Legt een mislukte AI-classificatie vast en verhoogt de pogingenteller. Zonder rij is er geen
    /// spoor én geen teller: het bericht komt elke poll terug en kost elke keer een AI-call. Op dit
    /// pad wordt nooit iets verstuurd, dus een extra poging kan geen dubbel antwoord veroorzaken. (#712)
    /// </summary>
    public static async Task RegistreerClassificatieFoutAsync(
        string messageId, IEmailVerwerkingOpslag opslag, IEmailMailboxActies mailbox, ILogger log)
    {
        try
        {
            var stand = await opslag.HaalStandOpAsync();
            int verwerkingId;

            switch (stand?.Besluit ?? VerwerkingsBesluit.NieuweVerwerking)
            {
                case VerwerkingsBesluit.OverslaanAlAfgerond:
                    log.LogInformation(
                        "Email {MessageId}: classificatie mislukt maar verwerking is al definitief afgehandeld — als gelezen gemarkeerd",
                        messageId);
                    await mailbox.MarkeerGelezenAsync(messageId);
                    return;

                case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                    await GeefVerwerkingOpAsync(
                        messageId, stand!.VerwerkingId, stand.Pogingen, "AI-classificatie", opslag, mailbox, log);
                    return;

                case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                    // Kan alleen als een eerdere poging al aan het versturen toe was. Dan is opnieuw
                    // classificeren zinloos en opnieuw versturen onveilig. (#716)
                    await LegVoorBeoordelingNaVerzendPogingAsync(messageId, stand!.VerwerkingId, opslag, mailbox, log);
                    return;

                case VerwerkingsBesluit.HerhaalVerwerking:
                    await opslag.VerhoogPogingenAsync(stand!.VerwerkingId);
                    verwerkingId = stand.VerwerkingId;
                    break;

                default:
                    verwerkingId = await opslag.RegistreerNieuweVerwerkingAsync();
                    break;
            }

            await opslag.UpdateFoutAsync(verwerkingId, "AI-classificatie mislukt");
            log.LogWarning(
                "Email {MessageId}: AI-classificatie mislukt — poging {Poging} van {Max} vastgelegd, bericht blijft ongelezen voor de volgende poll",
                messageId, (stand?.Pogingen ?? 0) + 1, VerwerkingsBesluitRegels.MaxPogingen);
        }
        catch (Exception ex) when (opslag.IsGelijktijdigeRegistratie(ex))
        {
            // Een andere invocatie registreerde het bericht tussen de stand-lezing en deze INSERT.
            // Geen fout: die verwerking is leidend en heeft een eigen pogingenteller. (#707)
            log.LogInformation(
                "Email {MessageId}: al door een andere verwerking geregistreerd — classificatiefout niet vastgelegd",
                messageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon mislukte AI-classificatie van {MessageId} niet vastleggen", messageId);
        }
    }

    /// <summary>
    /// Legt een verwerkingsfout vast voor een bericht waarvan het verwerkingId op deze plek niet
    /// bekend is (#717: mutaties gaan op Id, dus eerst opzoeken). Bestaat er geen rij, dan is er
    /// niets vast te leggen — het bericht blijft ongelezen voor de volgende poll.
    /// </summary>
    public static async Task LegVerwerkingsFoutVastAsync(
        string messageId, string foutMelding, IEmailVerwerkingOpslag opslag, ILogger log)
    {
        try
        {
            var stand = await opslag.HaalStandOpAsync();
            if (stand is null)
            {
                log.LogWarning(
                    "Email {MessageId}: verwerking mislukt vóórdat er een rij bestond — geen foutstatus vastgelegd",
                    messageId);
                return;
            }

            await opslag.UpdateFoutAsync(stand.VerwerkingId, foutMelding);
        }
        catch (Exception ex)
        {
            // Een fout bij het vastleggen van een fout mag nooit cascaderen: de oorspronkelijke fout is
            // al gelogd en het bericht blijft ongelezen voor de volgende poll.
            log.LogWarning(ex, "Kon foutstatus niet vastleggen voor {MessageId}", messageId);
        }
    }

    /// <summary>
    /// Handelt een buiten-scope classificatie af: status vastleggen, labelen, als gelezen markeren,
    /// géén antwoord (#712). Retourneert <c>true</c> als het bericht hiermee is afgehandeld.
    /// </summary>
    public static async Task<bool> HandelBuitenScopeAsync(
        int verwerkingId, string messageId, bool isBuitenScope, Func<Task> legBuitenScopeVastAsync,
        IEmailMailboxActies mailbox, ILogger log)
    {
        if (!isBuitenScope)
            return false;

        await legBuitenScopeVastAsync();
        log.LogInformation("Email {Id} buiten scope na herclassificatie — geen antwoord verstuurd", verwerkingId);

        // Labelen mag niet fataal zijn: de status in de database is al definitief, dus een volgende
        // poll slaat het bericht over en markeert het dan als gelezen.
        try
        {
            await mailbox.LabelGeenAiAntwoordAsync(messageId);
            await mailbox.MarkeerGelezenAsync(messageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling buiten scope mislukt voor {MessageId} — status is wel vastgelegd", messageId);
        }

        return true;
    }

    /// <summary>
    /// Geeft een bericht definitief op na <see cref="VerwerkingsBesluitRegels.MaxPogingen"/> mislukte
    /// pogingen: status Fout met een verklarende melding, en als gelezen markeren zodat het de
    /// wachtrij niet blijft bezetten. Er is nooit een antwoord verstuurd op dit pad, dus de
    /// coördinator moet het bericht handmatig oppakken — het email-log is daarvoor het spoor.
    /// </summary>
    private static async Task GeefVerwerkingOpAsync(
        string messageId, int verwerkingId, int pogingen, string fase,
        IEmailVerwerkingOpslag opslag, IEmailMailboxActies mailbox, ILogger log)
    {
        log.LogError(
            "Email {MessageId} opgegeven na {Pogingen} mislukte pogingen ({Fase}) — als gelezen gemarkeerd "
            + "zodat de wachtrij niet blokkeert. Handmatige opvolging nodig via het email-log.",
            messageId, pogingen, fase);

        try
        {
            await opslag.UpdateFoutAsync(verwerkingId,
                $"Opgegeven na {pogingen} mislukte pogingen ({fase}) — handmatige opvolging nodig");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon opgeven-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        await mailbox.MarkeerGelezenAsync(messageId);
    }

    /// <summary>
    /// Verwerking met onbekende verzenduitkomst (#716): niet opnieuw versturen (de afzender kan dan
    /// een tweede antwoord krijgen), maar op Review zetten en als gelezen markeren, zodat het de
    /// wachtrij niet elke poll bezet. De coördinator beoordeelt het via het email-log.
    /// </summary>
    private static async Task LegVoorBeoordelingNaVerzendPogingAsync(
        string messageId, int verwerkingId, IEmailVerwerkingOpslag opslag, IEmailMailboxActies mailbox, ILogger log)
    {
        log.LogWarning(
            "Email {MessageId}: er staat een verzendpoging vastgelegd zonder bekende uitkomst voor verwerking "
            + "{Id} — waarschijnlijk is een eerdere invocatie afgebroken tussen versturen en vastleggen. "
            + "Er wordt NIET opnieuw verstuurd; status op Review voor beoordeling via het email-log.",
            messageId, verwerkingId);

        try
        {
            await opslag.ZetOpReviewAsync(verwerkingId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon Review-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        try
        {
            await mailbox.LabelGeenAiAntwoordAsync(messageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling mislukt voor verwerking {Id}", verwerkingId);
        }

        await mailbox.MarkeerGelezenAsync(messageId);
    }
}
