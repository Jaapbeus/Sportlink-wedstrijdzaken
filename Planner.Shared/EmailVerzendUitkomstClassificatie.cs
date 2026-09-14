namespace Planner.Shared;

/// <summary>
/// Uitkomst van een classificatie van een mislukte e-mail-verzendpoging (#1133, bevinding 8 van de
/// Codex-review #1107).
/// </summary>
public enum EmailVerzendUitkomst
{
    /// <summary>
    /// Graph heeft het verzoek aantoonbaar afgewezen — er is niets verstuurd. Veilig om de
    /// verzendintentie te wissen en het bij een volgende poll opnieuw te proberen.
    /// </summary>
    ExplicieteAfwijzing,

    /// <summary>
    /// De uitkomst is onbekend: Graph kan het bericht al geaccepteerd hebben vóór de exception
    /// ontstond. Nooit opnieuw versturen op basis hiervan.
    /// </summary>
    OnbekendeUitkomst
}

/// <summary>
/// Onderscheidt, ná een mislukte Graph-verzendpoging, een bewijsbare afwijzing van een onbekende
/// uitkomst (#1133).
///
/// <para>
/// Vóór deze klasse wiste <c>EmailReplyPolicyService</c> de verzendintentie bij élke exception van
/// <c>SendReplyAsync</c> — ook bij een time-out, annulering of verbindingsverlies, waarbij Graph het
/// bericht mogelijk al had geaccepteerd. De volgende poll zag daardoor geen onbesliste intentie meer
/// en verstuurde een tweede antwoord op hetzelfde inkomende bericht (gereproduceerd met een fake
/// Graph die eerst accepteert en dan een <c>TaskCanceledException</c> gooit).
/// </para>
///
/// <para>
/// Alleen een HTTP-statuscode die aantoont dat Graph het verzóek zelf heeft afgewezen — vóórdat er
/// een bericht de deur uit kon gaan — mag de verzendintentie laten wissen. Alles daarbuiten (een
/// time-out, annulering, verbindingsverlies, een 5xx/408-status, of een ontbrekende statuscode) telt
/// als een onbekende uitkomst: de intentie moet blijven staan zodat het bericht ter beoordeling
/// wordt neergelegd in plaats van opnieuw verstuurd.
/// </para>
///
/// <para>
/// Bewust zonder afhankelijkheid van de Microsoft.Graph SDK: de aanroeper (tier-specifieke
/// <c>EmailReplyPolicyService</c>) haalt de statuscode uit
/// <c>Microsoft.Graph.Models.ODataErrors.ODataError.ResponseStatusCode</c> en geeft die als
/// <c>int?</c> door, zodat deze classificatie puur en op beide tiers identiek getest kan worden.
/// </para>
/// </summary>
public static class EmailVerzendFoutClassificatie
{
    /// <summary>
    /// HTTP-statuscodes die bewijzen dat Graph niets verstuurd heeft: het verzoek zelf werd
    /// afgewezen (validatie, autorisatie, ontbrekend object, te grote payload, rate-limit) vóórdat er
    /// een bericht kon worden aangemaakt of verzonden.
    /// <para>
    /// Bewust exclusief 5xx en 408 ("Request Timeout"): die betekenen dat Graph zelf een probleem had
    /// tijdens/na het verwerken van het verzoek, wat niet uitsluit dat het bericht al (deels)
    /// verwerkt of verstuurd is.
    /// </para>
    /// </summary>
    private static readonly HashSet<int> ExplicieteAfwijzingStatusCodes = [400, 401, 403, 404, 413, 422, 429];

    /// <summary>
    /// Bepaalt de uitkomst-classificatie voor een mislukte verzendpoging.
    /// </summary>
    /// <param name="ex">De exception die <c>SendReplyAsync</c> gooide.</param>
    /// <param name="graphStatusCode">
    /// De HTTP-statuscode uit een Graph-foutrespons (bijv.
    /// <c>ODataError.ResponseStatusCode</c>), of <c>null</c> als die er niet is — bijvoorbeeld omdat
    /// de exception ontstond vóórdat er ooit een respons was (time-out, verbindingsverlies).
    /// </param>
    public static EmailVerzendUitkomst Classificeer(Exception ex, int? graphStatusCode)
    {
        // Deze exception-types betekenen allemaal: geen (volledige) respons ontvangen van Graph. Ook
        // met een toevallig meegegeven statuscode is er geen garantie dat Graph het verzoek niet
        // toch heeft verwerkt vóór de time-out of het verbindingsverlies — dus altijd onbeslist.
        if (IsOnbekendeUitkomstExceptie(ex))
            return EmailVerzendUitkomst.OnbekendeUitkomst;

        return graphStatusCode is int code && ExplicieteAfwijzingStatusCodes.Contains(code)
            ? EmailVerzendUitkomst.ExplicieteAfwijzing
            : EmailVerzendUitkomst.OnbekendeUitkomst;
    }

    private static bool IsOnbekendeUitkomstExceptie(Exception ex) => ex switch
    {
        TaskCanceledException => true,
        OperationCanceledException => true,
        HttpRequestException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ => false
    };
}
