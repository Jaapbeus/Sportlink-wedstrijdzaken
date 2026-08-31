using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>SystemUtilities.SeasonHelper</c> (#890) — leest
/// seizoensgrenzen uit <c>public.season</c> (migratie 008, #890).
/// <para>
/// <b>Bewust niet geport:</b> <c>GetCurrentKnvbSeizoenAsync</c>. De enige twee consumenten op de
/// SQL Server-tier (<c>EmailReplyPolicyService</c>/<c>BerichtPipeline</c>, het #561
/// KNVB-verzet-zonder-datum-pad) horen bij de volledige e-mail-AI-pijplijn, die al buiten #889's
/// eigen scope-omschrijving valt (zie docs/ARCHITECTUUR-DATABASE-TIERS.md §17) — een fantoom-
/// vertaling zonder consument zou onnodige onderhoudslast zijn.
/// </para>
/// </summary>
internal static class PostgresSeasonHelper
{
    // Zelfde gedocumenteerde fallbackwaarden als SystemUtilities.SeasonHelper op de SQL
    // Server-tier gebruikt wanneer public.season niet bereikbaar is of leeg is.
    internal const int DefaultToWeekOffset = 30;
    internal const int DefaultFromWeekOffset = -40;

    /// <summary>Aantal weken van vandaag tot het einde van het laatste seizoen in public.season.</summary>
    internal static async Task<int> GetSeasonEndWeekOffsetAsync(ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT MAX(dateuntil) FROM public.season", connection);
            var result = await command.ExecuteScalarAsync();
            if (result is DateTime endDate)
                return (int)Math.Ceiling((endDate - DateTime.Today).TotalDays / 7.0);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen seizoenseinde uit public.season");
        }
        return DefaultToWeekOffset;
    }

    /// <summary>
    /// Week-offset van vandaag tot de start van het seizoen waarvan DateFrom in startYear valt.
    /// Negatief wanneer de seizoensstart al voorbij is.
    /// </summary>
    internal static async Task<int> GetSeasonStartWeekOffsetAsync(int startYear, ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT MIN(datefrom) FROM public.season WHERE EXTRACT(YEAR FROM datefrom) = @jaar", connection);
            command.Parameters.AddWithValue("jaar", startYear);
            var result = await command.ExecuteScalarAsync();
            if (result is DateTime startDate)
                return (int)Math.Floor((startDate - DateTime.Today).TotalDays / 7.0);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen seizoensstart voor jaar {StartYear}", startYear);
        }
        return DefaultFromWeekOffset;
    }
}
