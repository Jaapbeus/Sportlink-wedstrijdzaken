using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Legt nieuwe teamnaam-schrijfwijzen vast als <c>pending</c> alias (#697), analoog aan het
/// bestaande leermomenten-concept (<c>planner.ClassificatieCorrectie</c>).
///
/// Belangrijk: een alias wordt NOOIT automatisch als waarheid gebruikt. Alleen aliassen die een
/// coördinator via de Admin-UI heeft gevalideerd (status <c>validated</c>) tellen mee in
/// <see cref="TeamResolver"/>. Zo kan een foutieve AI-disambiguatie zich niet zelfversterken.
/// </summary>
/// <remarks>
/// #1294: de exists-check en de UPDATE hieronder vergelijken <c>RuweTekstGenormaliseerd</c> nu
/// expliciet via <c>UPPER(...)</c>, gelijk aan <see cref="TeamCandidateRepository"/> (#820) — zie
/// diens class-remarks voor de volledige onderbouwing. Behoud van gedrag, geen wijziging: onder de
/// huidige case-insensitieve modelcollatie (<c>1033, CI</c>) was de kale <c>=</c> hier al
/// hoofdletterongevoelig, dus dit kan geen bestaande rij die vandaag als "nieuw" gold nu opeens als
/// "bestaand" (UPDATE i.p.v. INSERT) laten tellen.
/// </remarks>
public sealed class TeamAliasLearningService(ILogger<TeamAliasLearningService> logger)
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    public async Task LegVastAsync(string clubCode, string ruweTekst, int teamId, string bron)
    {
        // clubCode meegeven: de resolver normaliseert mét prefix-strip, en een alias die op een andere
        // sleutel is vastgelegd dan waarmee gezocht wordt, zou nooit gevonden worden.
        var genormaliseerd = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, clubCode);
        if (genormaliseerd.Length == 0) return;

        try
        {
            using var conn = new SqlConnection(Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                IF NOT EXISTS (
                    SELECT 1 FROM [dbo].[TeamAliassen]
                    WHERE [ClubCode] = @clubCode AND UPPER([RuweTekstGenormaliseerd]) = UPPER(@genormaliseerd))
                    INSERT INTO [dbo].[TeamAliassen]
                        ([ClubCode], [RuweTekst], [RuweTekstGenormaliseerd], [TeamId], [Bron], [Status], [AantalKeerGebruikt])
                    VALUES (@clubCode, @ruweTekst, @genormaliseerd, @teamId, @bron, 'pending', 1);
                ELSE
                    UPDATE [dbo].[TeamAliassen]
                    SET [AantalKeerGebruikt] = [AantalKeerGebruikt] + 1, [mta_modified] = GETUTCDATE()
                    WHERE [ClubCode] = @clubCode AND UPPER([RuweTekstGenormaliseerd]) = UPPER(@genormaliseerd);
            ", conn);
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
            cmd.Parameters.AddWithValue("@ruweTekst", ruweTekst);
            cmd.Parameters.AddWithValue("@genormaliseerd", genormaliseerd);
            cmd.Parameters.AddWithValue("@teamId", teamId);
            cmd.Parameters.AddWithValue("@bron", bron);
            await cmd.ExecuteNonQueryAsync();

            logger.LogInformation("Teamalias vastgelegd (pending) voor TeamId={TeamId}, bron={Bron}", teamId, bron);
        }
        catch (Exception ex)
        {
            // Leren is een bijzaak: nooit de e-mailverwerking laten falen op een alias-write.
            logger.LogError(ex, "Vastleggen teamalias mislukt voor TeamId={TeamId}", teamId);
        }
    }
}
