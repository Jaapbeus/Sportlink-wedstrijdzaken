using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// #1459: de clubinstelling "PDF-export" (<c>dbo.AppSettings.PdfExportIngeschakeld</c>). Standaard
/// <c>false</c> — fail-closed tot een beheerder bevestigt dat de QuestPDF Community-licentievoorwaarden
/// voor de club gelden. Per club gelezen; een nog niet bestaande kolom (PostDeployment niet gedraaid)
/// telt als uit. Postgres-tegenhanger: <c>FunctionApp.Postgres.Planner.PostgresPdfExportInstelling</c>.
/// </summary>
public static class PdfExportInstelling
{
    public static async Task<bool> LeesAsync(string clubCode)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            DECLARE @v BIT = 0;
            IF COL_LENGTH('[dbo].[AppSettings]', 'PdfExportIngeschakeld') IS NOT NULL
                EXEC sp_executesql
                    N'SELECT TOP 1 @v = [PdfExportIngeschakeld] FROM [dbo].[AppSettings] WHERE [ClubCode] = @cc',
                    N'@v BIT OUTPUT, @cc NVARCHAR(20)', @v = @v OUTPUT, @cc = @ClubCode;
            SELECT @v;", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        return await command.ExecuteScalarAsync() is true;
    }
}
