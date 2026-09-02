using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/TeamAliasLearningService.cs</c>
/// (#889). Vertaling: <c>IF NOT EXISTS ... INSERT ELSE UPDATE</c> →
/// <c>INSERT ... ON CONFLICT (clubcode, upper(ruwetekst)) DO UPDATE SET</c> —
/// <c>public.teamaliassen</c> heeft (#887, herzien in #820) een expression-based unique index op
/// <c>(clubcode, upper(ruwetekst))</c> in plaats van een kale <c>UNIQUE(clubcode, ruwetekst)</c>:
/// Postgres' default-collatie is case-sensitive (SQL Server's CI-collatie niet), dus zonder de
/// <c>upper(...)</c>-wrap zou dezelfde ruwe tekst in afwijkende hoofdlettering een tweede rij
/// aanmaken in plaats van de teller op de bestaande rij te verhogen. Zie
/// <c>Database.Postgres/migrations/007_teams_collation_fix.sql</c>. Een alias wordt — zelfde harde
/// regel als het origineel — NOOIT automatisch als waarheid gebruikt: alleen door een coördinator
/// gevalideerde (<c>status = 'validated'</c>) aliassen tellen mee in teamresolutie.
/// </summary>
internal sealed class TeamAliasLearningService(string connectionString, ILogger logger)
{
    internal async Task LegVastAsync(string clubCode, string ruweTekst, int teamId, string bron)
    {
        var genormaliseerd = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, clubCode);
        if (genormaliseerd.Length == 0) return;

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO public.teamaliassen
                    (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status, aantalkeergebruikt)
                VALUES (@clubcode, @ruwetekst, @genormaliseerd, @teamid, @bron, '{TeamAliasConstanten.StatusPending}', 1)
                ON CONFLICT (clubcode, upper(ruwetekst)) DO UPDATE SET
                    aantalkeergebruikt = public.teamaliassen.aantalkeergebruikt + 1,
                    mta_modified = NOW()
            ", conn);
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            cmd.Parameters.AddWithValue("ruwetekst", ruweTekst);
            cmd.Parameters.AddWithValue("genormaliseerd", genormaliseerd);
            cmd.Parameters.AddWithValue("teamid", teamId);
            cmd.Parameters.AddWithValue("bron", bron);
            await cmd.ExecuteNonQueryAsync();

            logger.LogInformation("Teamalias vastgelegd (pending) voor TeamId={TeamId}, bron={Bron}", teamId, bron);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vastleggen teamalias mislukt voor TeamId={TeamId}", teamId);
        }
    }
}
