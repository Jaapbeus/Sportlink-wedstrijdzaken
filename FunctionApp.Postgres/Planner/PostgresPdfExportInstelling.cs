using Npgsql;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// #1459: de clubinstelling "PDF-export" (<c>public.appsettings.pdfexportingeschakeld</c>, migratie
/// 034). Standaard <c>false</c> — fail-closed tot een beheerder bevestigt dat de QuestPDF
/// Community-licentievoorwaarden voor de club gelden. Per club gelezen (niet uit de
/// primaire-club-cache): de planner-endpoints werken voor de gekozen club, ook voor de democlub.
/// SQL Server-tegenhanger: <c>SportlinkFunction.Planner.PdfExportInstelling</c> (zelfde regel, SQL Server).
/// </summary>
public static class PostgresPdfExportInstelling
{
    public static async Task<bool> IsIngeschakeldAsync(string clubCode)
    {
        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pdfexportingeschakeld FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        return await command.ExecuteScalarAsync() is true;
    }
}
