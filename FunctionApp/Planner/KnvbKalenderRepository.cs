using Microsoft.Data.SqlClient;
using Planner.Shared.Calendars;

namespace SportlinkFunction.Planner;

/// <summary>
/// Leest <c>dbo.KnvbKalenderDag</c> — de landelijke KNVB-speeldagenkalender per regio/seizoen (#561).
/// Gebruikt om "vrije zaterdagen" te bepalen voor de verzet-zonder-datum e-mailflow: zaterdagen
/// waarop volgens de KNVB-kalender gespeeld kán worden (Competitie/Beker/Inhaal), maar waarop ons
/// eigen team volgens het huidige programma nog geen wedstrijd heeft.
/// </summary>
public static class KnvbKalenderRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    /// <summary>
    /// Retourneert maximaal <paramref name="maxAantal"/> zaterdagen, oplopend gesorteerd, waarop
    /// volgens <c>dbo.KnvbKalenderDag</c> gespeeld kan worden en die niet voorkomen in
    /// <paramref name="reedsBezetteData"/>.
    /// </summary>
    /// <param name="clubCode">
    /// Niet gebruikt voor SQL-filtering — <c>dbo.KnvbKalenderDag</c> is landelijke KNVB-data zonder
    /// ClubCode-kolom. Alleen meegenomen voor signatuur-consistentie met de rest van de Planner-laag
    /// (zie <see cref="PlannerDataAccess"/>), zodat toekomstige aanroepers niet per ongeluk aannemen
    /// dat clubscoping hier ontbreekt door onoplettendheid.
    /// </param>
    public static async Task<List<DateOnly>> GetVrijeZaterdagenAsync(
        string regio, string seizoen, DateOnly van, DateOnly totEnMet,
        ISet<DateOnly> reedsBezetteData, int maxAantal, string? clubCode = null)
    {
        if (string.IsNullOrWhiteSpace(regio) || string.IsNullOrWhiteSpace(seizoen) || maxAantal <= 0)
            return [];

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Datum], [DagType]
            FROM [dbo].[KnvbKalenderDag]
            WHERE [Seizoen] = @seizoen
              AND [Regio] = @regio
              AND [Datum] BETWEEN @van AND @tot
              AND [DagType] IN ('Competitie','Beker','Inhaal')
            ORDER BY [Datum] ASC", conn);
        cmd.Parameters.AddWithValue("@seizoen", seizoen);
        cmd.Parameters.AddWithValue("@regio", regio);
        cmd.Parameters.AddWithValue("@van", van.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@tot", totEnMet.ToDateTime(TimeOnly.MinValue));

        using var reader = await cmd.ExecuteReaderAsync();
        var dagen = new List<KnvbKalenderDag>();
        while (await reader.ReadAsync())
        {
            dagen.Add(new KnvbKalenderDag(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetString(1)));
        }

        return KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, van, totEnMet, reedsBezetteData, maxAantal);
    }
}
