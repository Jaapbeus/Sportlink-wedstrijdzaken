using Microsoft.Extensions.Logging;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Shadow-mode voor de teamnaam→ID-vertaallaag (#698). Draait de nieuwe
/// <see cref="ITeamResolver"/> náást de bestaande regex/LIKE-matching en logt of ze tot dezelfde
/// uitkomst komen — zonder het gedrag van de e-mailverwerking te veranderen.
///
/// <para>
/// Doel: bewijs opbouwen vóór de cutover (#699/#700). Zolang deze logging structureel afwijkingen
/// laat zien, mag de nieuwe matching niet leidend worden.
/// </para>
///
/// <para>
/// Faalt nooit door naar buiten: een uitzondering in de shadow-vergelijking mag de echte
/// e-mailverwerking niet raken.
/// </para>
/// </summary>
public sealed class TeamResolutionShadowLogger(
    ITeamResolver resolver,
    ILogger<TeamResolutionShadowLogger> logger)
{
    /// <param name="oudeUitkomst">
    /// De teamnaam zoals de bestaande pipeline die na regex-normalisatie oplevert (inclusief
    /// clubprefix), of <c>null</c> als de oude weg niets opleverde.
    /// </param>
    public async Task VergelijkAsync(string? ruweTeamTekst, string? oudeUitkomst, string clubCode)
    {
        if (string.IsNullOrWhiteSpace(ruweTeamTekst) || string.IsNullOrWhiteSpace(clubCode))
            return;

        try
        {
            var resultaat = await resolver.ResolveAsync(
                new TeamResolutionRequest(ruweTeamTekst, null, null, clubCode));

            // Vergelijk op genormaliseerde sleutel: de oude weg levert de clubprefix-vorm op
            // ("[club] JO13-1"), de nieuwe de canonieke naam. Alleen de genormaliseerde vorm
            // is zinvol vergelijkbaar.
            var oudeSleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(oudeUitkomst, clubCode);
            var nieuweSleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(resultaat.CanoniekeTeamnaam, clubCode);

            var gelijk = oudeSleutel.Length > 0
                         && string.Equals(oudeSleutel, nieuweSleutel, StringComparison.Ordinal);

            logger.LogInformation(
                "TEAMRESOLUTIE SHADOW - overeenkomst={Gelijk} bron={Bron} confidence={Confidence} "
                + "teamId={TeamId} kandidaten={Kandidaten} oudeSleutel={OudeSleutel} nieuweSleutel={NieuweSleutel}",
                gelijk, resultaat.Bron, resultaat.Confidence, resultaat.TeamId,
                resultaat.Kandidaten.Count, Kort(oudeSleutel), Kort(nieuweSleutel));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TEAMRESOLUTIE SHADOW - vergelijking mislukt (verwerking gaat ongewijzigd door)");
        }
    }

    /// <summary>
    /// Teamaanduidingen zijn geen persoonsgegevens, maar we begrenzen de gelogde lengte zodat er
    /// nooit per ongeluk een stuk e-mailtekst in de logs belandt (AVG-hygiëne, vgl. #210).
    /// </summary>
    private static string Kort(string? waarde)
        => string.IsNullOrEmpty(waarde) ? "(geen)" : waarde.Length <= 40 ? waarde : waarde[..40];
}
