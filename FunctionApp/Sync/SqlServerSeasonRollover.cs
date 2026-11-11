using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;
using Planner.Shared;

namespace SportlinkFunction.Sync;

/// <summary>
/// Additieve tegenhanger van de Postgres seizoensdoorrol voor de SQL Server-tier. De deployprocedure
/// wordt hier bewust niet aangeroepen, omdat die ook dbo.DateTable laat herbouwen.
/// De huidige datum komt uit de strikte bedrijfsklok; reconciliatie heeft een eigen conservatieve fallback.
/// </summary>
internal static class SqlServerSeasonRollover
{
    internal static DateOnly VandaagInNederland =>
        BepaalVandaag(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById);

    internal static DateOnly BepaalVandaag(DateTimeOffset utcNu, Func<string, TimeZoneInfo> zoekTijdzone) =>
        NederlandseBedrijfsKlok.Vandaag(utcNu, zoekTijdzone);

    internal static async Task<int> EnsureAsync(string clubCode, DateOnly vandaag, ILogger log)
    {
        try
        {
            await using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var toegevoegd = await EnsureAsync(connection, transaction, clubCode, vandaag);
            await transaction.CommitAsync();
            if (toegevoegd > 0)
                log.LogInformation("SEIZOENEN - {Aantal} seizoensrij(en) toegevoegd aan dbo.ClubSeason", toegevoegd);
            return toegevoegd;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij het doorrollen van dbo.ClubSeason — sync afgebroken");
            throw;
        }
    }

    internal static async Task<int> EnsureAsync(
        SqlConnection connection, SqlTransaction transaction, string clubCode, DateOnly vandaag)
    {
        int? opgeslagen;
        await using (var command = new SqlCommand(
            "SELECT [SeasonStartMonth] FROM [dbo].[AppSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [ClubCode] = @ClubCode",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var value = await command.ExecuteScalarAsync();
            if (value is null) return 0;
            opgeslagen = value is DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var startMaand = SeizoenStartMaand.OfStandaard(opgeslagen);
        var toegevoegd = 0;
        if (!await HeeftSeizoenenAsync(connection, transaction, clubCode))
        {
            toegevoegd += await VoegToeAsync(connection, transaction, clubCode, vandaag.Year - 2, startMaand);
            toegevoegd += await VoegToeAsync(connection, transaction, clubCode, vandaag.Year - 1, startMaand);
        }

        foreach (var startjaar in SeizoenKalender.DoorTeRollenStartjaren(startMaand, vandaag))
            toegevoegd += await VoegToeAsync(connection, transaction, clubCode, startjaar, startMaand);

        return toegevoegd;
    }

    private static async Task<bool> HeeftSeizoenenAsync(
        SqlConnection connection, SqlTransaction transaction, string clubCode)
    {
        await using var command = new SqlCommand(
            "SELECT TOP (1) 1 FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode",
            connection, transaction);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<int> VoegToeAsync(
        SqlConnection connection, SqlTransaction transaction, string clubCode, int startjaar, int startMaand)
    {
        var seizoen = SeizoenKalender.Seizoen(startjaar, startMaand);
        await using var command = new SqlCommand(@"
            IF NOT EXISTS (
                SELECT 1 FROM [dbo].[ClubSeason] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ClubCode] = @ClubCode AND [Name] = @Name)
            BEGIN
                INSERT INTO [dbo].[ClubSeason] ([ClubCode], [Name], [DateFrom], [DateUntil])
                VALUES (@ClubCode, @Name, @DateFrom, @DateUntil);
                SELECT 1;
            END
            ELSE SELECT 0;", connection, transaction);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Name", seizoen.Naam);
        command.Parameters.Add(new SqlParameter("@DateFrom", System.Data.SqlDbType.Date)
        {
            Value = seizoen.Van.ToDateTime(TimeOnly.MinValue)
        });
        command.Parameters.Add(new SqlParameter("@DateUntil", System.Data.SqlDbType.Date)
        {
            Value = seizoen.Tot.ToDateTime(TimeOnly.MinValue)
        });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
