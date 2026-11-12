using Npgsql;
using Planner.Shared.Calendars;

namespace FunctionApp.Postgres.Planner.Repositories;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/KnvbKalenderRepository.cs</c> (#561/#1141).
/// Leest <c>public.knvbkalenderdag</c> — de landelijke KNVB-speeldagenkalender per regio/seizoen
/// (migratie 019). Gebruikt om "vrije zaterdagen" te bepalen voor de verzet-zonder-datum
/// e-mailflow: zaterdagen waarop volgens de KNVB-kalender gespeeld kán worden
/// (Competitie/Beker/Inhaal), maar waarop ons eigen team volgens het huidige programma nog geen
/// wedstrijd heeft.
/// </summary>
internal static class KnvbKalenderRepository
{
    /// <summary>
    /// Retourneert maximaal <paramref name="maxAantal"/> zaterdagen, oplopend gesorteerd, waarop
    /// volgens <c>public.knvbkalenderdag</c> gespeeld kan worden en die niet voorkomen in
    /// <paramref name="reedsBezetteData"/>.
    /// </summary>
    /// <param name="clubCode">
    /// Niet gebruikt voor SQL-filtering — <c>public.knvbkalenderdag</c> is landelijke KNVB-data
    /// zonder clubcode-kolom, zelfde als het SQL Server-origineel. Alleen meegenomen voor
    /// signatuur-consistentie met de rest van de Planner-laag.
    /// </param>
    internal static async Task<List<DateOnly>> GetVrijeZaterdagenAsync(
        string connectionString, string regio, string seizoen, DateOnly van, DateOnly totEnMet,
        ISet<DateOnly> reedsBezetteData, int maxAantal, string? clubCode = null)
    {
        if (string.IsNullOrWhiteSpace(regio) || string.IsNullOrWhiteSpace(seizoen) || maxAantal <= 0)
            return [];

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT datum
            FROM public.knvbkalenderdag
            WHERE seizoen = @seizoen
              AND regio = @regio
              AND datum BETWEEN @van AND @tot
              AND dagtype IN ('Competitie','Beker','Inhaal')
            ORDER BY datum ASC", conn);
        cmd.Parameters.AddWithValue("seizoen", seizoen);
        cmd.Parameters.AddWithValue("regio", regio);
        cmd.Parameters.AddWithValue("van", van.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("tot", totEnMet.ToDateTime(TimeOnly.MinValue));

        await using var reader = await cmd.ExecuteReaderAsync();
        var dagen = new List<DateOnly>();
        while (await reader.ReadAsync())
        {
            dagen.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, reedsBezetteData, maxAantal);
    }
}
