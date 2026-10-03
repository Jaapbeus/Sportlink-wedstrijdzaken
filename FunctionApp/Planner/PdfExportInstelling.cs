using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// #1459: leest de clubinstelling "PDF-export" (<c>dbo.AppSettings.PdfExportIngeschakeld</c>,
/// PostDeployment). Alleen de databasevraag staat per tier; de beslissing (409, fail-closed) staat in
/// <c>Planner.Endpoints.Deel.PlannerDeelEndpointCore.BeslisPdfAsync</c>. Per club gelezen, niet uit de
/// primaire-club-cache: de planner-endpoints werken voor de gekozen club. Geen rij telt als uit.
/// </summary>
public static class PdfExportInstelling
{
    public static async Task<bool> IsIngeschakeldAsync(string clubCode)
    {
        await using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TOP 1 [PdfExportIngeschakeld] FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        return await command.ExecuteScalarAsync() is true;
    }
}
