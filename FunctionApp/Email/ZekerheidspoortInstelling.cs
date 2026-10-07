using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email.Trace;

namespace SportlinkFunction.Email;

/// <summary>
/// #1568 deel D: leest de clubinstelling "zekerheidspoort" (<c>dbo.AppSettings.ZekerheidspoortActief</c>,
/// PostDeployment) per club. Alleen de databasevraag staat per tier; de fail-safe (ontbreekt = aan) en de
/// betekenis staan in <see cref="ZekerheidsPoort"/>.
/// </summary>
internal static class ZekerheidspoortInstelling
{
    public static Task<bool> IsActiefAsync(string clubCode, ILogger log)
        => ZekerheidsPoort.LeesVeiligAsync(async () =>
        {
            await using var verbinding = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await verbinding.OpenAsync();
            await using var opdracht = new SqlCommand(
                "SELECT TOP 1 [ZekerheidspoortActief] FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode", verbinding);
            opdracht.Parameters.AddWithValue("@ClubCode", clubCode);
            return await opdracht.ExecuteScalarAsync();
        }, log);
}
