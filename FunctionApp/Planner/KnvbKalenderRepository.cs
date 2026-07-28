using Microsoft.Data.SqlClient;

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
        var resultaat = new List<DateOnly>();
        if (string.IsNullOrWhiteSpace(regio) || string.IsNullOrWhiteSpace(seizoen) || maxAantal <= 0)
            return resultaat;

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Datum]
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
        while (await reader.ReadAsync() && resultaat.Count < maxAantal)
        {
            var datum = DateOnly.FromDateTime(reader.GetDateTime(0));
            // Defensief: de kalender behoort alleen zaterdagen (of vrijdag voor Toernooi, dat hier al
            // uitgesloten is door de DagType-filter) te bevatten, maar een verkeerd geseede rij mag
            // nooit een niet-zaterdag als "vrije zaterdag" voorstellen.
            if (datum.DayOfWeek != DayOfWeek.Saturday) continue;
            if (reedsBezetteData.Contains(datum)) continue;
            resultaat.Add(datum);
        }

        return resultaat;
    }
}
