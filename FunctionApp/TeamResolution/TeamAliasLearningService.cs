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
                    WHERE [ClubCode] = @clubCode AND [RuweTekstGenormaliseerd] = @genormaliseerd)
                    INSERT INTO [dbo].[TeamAliassen]
                        ([ClubCode], [RuweTekst], [RuweTekstGenormaliseerd], [TeamId], [Bron], [Status], [AantalKeerGebruikt])
                    VALUES (@clubCode, @ruweTekst, @genormaliseerd, @teamId, @bron, 'pending', 1);
                ELSE
                    UPDATE [dbo].[TeamAliassen]
                    SET [AantalKeerGebruikt] = [AantalKeerGebruikt] + 1, [mta_modified] = GETUTCDATE()
                    WHERE [ClubCode] = @clubCode AND [RuweTekstGenormaliseerd] = @genormaliseerd;
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
