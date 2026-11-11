using Microsoft.Data.SqlClient;
using Planner.Shared;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Planner;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// SQL Server-tier van <see cref="IAfzenderTeamBron"/> (#1862): de teamwaarden van een afzender uit
/// <c>avg.Teambegeleiding</c>. De vergelijking staat op <c>LOWER(LTRIM(RTRIM(Emailadres)))</c>; SQL Server matcht die
/// expressie met de persisted computed column <c>EmailadresGenormaliseerd</c> en de index erop (PostDeployment).
/// Alleen de teamkolom komt terug: nooit naam, telefoonnummer of rol, en het adres zit alleen als parameter in de
/// query (nooit in log of trace). Clubafbakening gelijk aan de rest van de begeleiding-opzoekingen.
/// </summary>
internal sealed class SqlAfzenderTeamBron(Func<string>? connectionString = null) : IAfzenderTeamBron
{
    public async Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode)
    {
        using var conn = new SqlConnection((connectionString ?? (() => SystemUtilities.DatabaseConfig.ConnectionString))());
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT DISTINCT tb.[Team]
            FROM [avg].[Teambegeleiding] tb
            WHERE LOWER(LTRIM(RTRIM(tb.[Emailadres]))) = @adres
              AND tb.[Team] IS NOT NULL AND LTRIM(RTRIM(tb.[Team])) <> ''
              AND {ClubScope.LegacyFilter("tb")}
        ", conn);
        cmd.Parameters.AddWithValue("@adres", afzender);
        ClubScope.AddHisParams(cmd, clubCode);

        using var reader = await cmd.ExecuteReaderAsync();
        return await AfzenderTeamBronHulp.LeesTeamsAsync(reader);
    }
}
