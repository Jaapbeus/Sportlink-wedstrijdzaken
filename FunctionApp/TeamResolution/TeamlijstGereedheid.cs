using Microsoft.Extensions.Logging;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Zorgt dat de teamlijst bruikbaar is vóórdat er berichten verwerkt worden (#700).
///
/// <para>
/// <b>Waarom dit bestaat.</b> Sinds #700 is de vertaallaag het enige pad waarlangs een team wordt
/// herkend; de oude regex-normalisatie is verwijderd. <c>dbo.Teams</c> wordt gevuld door de
/// nachtelijke Sportlink-sync, dus tussen een deploy en de eerste sync zou die tabel leeg zijn — en
/// dan herkent het systeem géén enkel team meer, zonder dat er iets kapot lijkt.
/// </para>
/// <para>
/// Deze klasse dicht dat gat: is de teamlijst leeg, dan wordt de canonicalisatie direct uitgevoerd op
/// de al aanwezige <c>his.teams</c>-data. Lukt dat niet, dan volgt een expliciete foutmelding in
/// plaats van stille mismatches.
/// </para>
/// <para>
/// Een gevulde teamlijst is niet automatisch een bruikbare teamlijst: staan de opgeslagen sleutels nog
/// volgens oudere normalisatieregels in de database, dan herkent de resolver alsnog geen enkel team
/// (#766). Daarom wordt bij een gevulde lijst de sleutelmigratie uitgevoerd — idempotent, en zonder
/// drift kost het twee SELECTs.
/// </para>
/// </summary>
public sealed class TeamlijstGereedheid(ITeamCandidateRepository repository, ILogger<TeamlijstGereedheid> logger)
{
    /// <summary>
    /// Controleert of er teams bekend zijn voor deze club en vult de lijst zo nodig aan.
    /// Retourneert false als er daarna nog steeds geen enkel team bekend is — dan kan
    /// teamherkenning niet werken en moet dat zichtbaar zijn.
    /// </summary>
    public async Task<bool> ZorgVoorTeamlijstAsync(string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
        {
            // Onderscheid dit expliciet van "geen teams": een lege clubCode betekent dat
            // dbo.AppSettings niet geladen is, en dan wijst "teamlijst leeg" naar de verkeerde oorzaak.
            logger.LogError(
                "TEAMLIJST - geen clubCode beschikbaar; dbo.AppSettings is niet geladen. "
                + "Teamherkenning kan hierdoor niet werken.");
            return false;
        }

        try
        {
            if (await repository.HeeftActieveTeamsAsync(clubCode))
            {
                // Teams aanwezig, maar hun opgeslagen sleutel kan nog volgens oudere
                // normalisatieregels berekend zijn (#766). Dan staat de lijst er wel, en herkent de
                // resolver alsnog niets. Normaal ruimt de sync dit op, maar die kan uit staan
                // (syncEnabled = 0) of nog niet gelopen hebben sinds de deploy — daarom hier ook.
                var (sleutels, dubbelen) = await TeamCanonicalisatieService.MigreerSleuteldriftAsync(clubCode, logger);
                if (sleutels > 0 || dubbelen > 0)
                    logger.LogWarning(
                        "TEAMLIJST - {Sleutels} teamsleutels gemigreerd en {Dubbelen} dubbele schrijfwijzen "
                        + "samengevoegd voor club {ClubCode} na een normalisatiewijziging", sleutels, dubbelen, clubCode);
                return true;
            }

            logger.LogWarning(
                "TEAMLIJST - geen actieve teams voor club {ClubCode}; canonicalisatie wordt nu uitgevoerd "
                + "(normaal doet de nachtelijke sync dit)", clubCode);

            await TeamCanonicalisatieService.RefreshAsync(clubCode, logger);

            if (await repository.HeeftActieveTeamsAsync(clubCode)) return true;

            logger.LogError(
                "TEAMLIJST - na canonicalisatie nog steeds geen teams voor club {ClubCode}. Teamherkenning "
                + "kan niet werken; controleer of de Sportlink-sync his.teams heeft gevuld.", clubCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TEAMLIJST - controle/aanvulling mislukt voor club {ClubCode}", clubCode);
            return false;
        }
    }
}
