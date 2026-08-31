using Database.Postgres;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner.Repositories;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Repositories/PlannerAvailabilityRepository.cs</c>
/// (#888). Voedt de gedeelde scheduling-engine (<see cref="Planner.Shared.FieldScheduler"/>, §38) met
/// veldbeschikbaarheid en bestaande bezetting.
///
/// <para>
/// <b>Veldresolutie gebeurt hier in C#, niet in de view</b> — precies zoals
/// <see cref="Database.Postgres.PostgresPlannerViewGenerator"/>'s eigen doc-comment voorschrijft
/// (#819): de view levert voor "Competitie"-rijen de ruwe Sportlink-veldstring (<c>veld_ruw</c>)
/// terug, geen veldnummer. Deze klasse resolveert die met <see cref="Planner.Shared.PlannerShared.VindVeldNummer"/>
/// — dezelfde matching als de SQL Server-tier gebruikt — en laat een rij vallen die niet resolveert
/// (<c>veldnummer == 0</c>), exact het SQL Server-origineel se <c>WHERE v.VeldNummer IS NOT NULL</c>-filter.
/// "Planner"-rijen (<c>planner.geplandewedstrijden</c>) hebben altijd al een resolved <c>veldnummer</c>
/// (een FK-kolom, geen vrije tekst) en slaan deze stap over.
/// </para>
/// </summary>
internal static class PlannerAvailabilityRepository
{
    /// <summary>
    /// Veldbeschikbaarheid op een datum, periode-aware (#581) — zelfde regime-exclusiviteit als het
    /// SQL Server-origineel: is er een actieve <c>VeldPeriode</c> op deze datum, dan gelden
    /// UITSLUITEND de rijen met dat <c>periodeid</c>; anders de rijen met <c>periodeid IS NULL</c>.
    /// </summary>
    internal static async Task<List<VeldBeschikbaarheidInfo>> GetAvailableFieldsAsync(
        string connectionString, DateOnly date, string? clubCode = null)
    {
        var results = new List<VeldBeschikbaarheidInfo>();
        int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH actieve_periode AS (
                SELECT id FROM public.veldperiode
                WHERE clubcode = @clubcode AND actief = true
                  AND @datum BETWEEN datumvan AND datumtot
                ORDER BY datumvan DESC
                LIMIT 1
            )
            SELECT vb.veldnummer, vb.beschikbaarvanaf, vb.beschikbaartot, vb.gebruikzonsondergang
            FROM public.veldbeschikbaarheid vb
            INNER JOIN public.velden v ON v.veldnummer = vb.veldnummer
            WHERE v.actief = true AND vb.dagvanweek = @dag AND vb.clubcode = @clubcode
              AND (
                    ((SELECT id FROM actieve_periode) IS NOT NULL AND vb.periodeid = (SELECT id FROM actieve_periode))
                    OR ((SELECT id FROM actieve_periode) IS NULL AND vb.periodeid IS NULL)
                  )
            ORDER BY vb.veldnummer
            """, conn);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        PostgresClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("datum", date.ToDateTime(TimeOnly.MinValue));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new VeldBeschikbaarheidInfo
            {
                VeldNummer = reader.GetInt32(0),
                BeschikbaarVanaf = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                BeschikbaarTot = TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                GebruikZonsondergang = reader.GetBoolean(3)
            });
        return results;
    }

    /// <summary>
    /// Veldbezetting op één datum, hard gescoped op ClubCode (#580). Combineert de wedstrijdbezetting
    /// uit <c>planner.alle_wedstrijden_op_veld_ruw</c> met trainingsblokken
    /// (<see cref="GetTrainingOccupationsAsync"/>).
    /// </summary>
    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsAsync(
        string connectionString, DateOnly date, string? clubCode = null)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        var velden = await PlannerSettingsRepository.GetVeldenAsync(connectionString, cc);

        var ruw = new List<(DateOnly Datum, TimeOnly Aanvang, DateTime Eind, string? VeldRuw, int? VeldNummer,
            decimal VeldDeelGebruik, string? LeeftijdsCategorie, string? TeamNaam, string? Wedstrijd,
            string Bron, long? Wedstrijdcode)>();

        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                SELECT datum, aanvangstijd, eindtijd, veld_ruw, veldnummer, velddeelgebruik,
                       leeftijdscategorie, teamnaam, wedstrijd, bron, wedstrijdcode
                FROM {PostgresPlannerViewGenerator.ViewName}
                WHERE datum = @datum AND clubcode = @clubcode
                """, conn);
            cmd.Parameters.AddWithValue("datum", date.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("clubcode", cc);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ruw.Add((
                    Datum: DateOnly.FromDateTime(reader.GetDateTime(0)),
                    Aanvang: TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                    Eind: reader.GetDateTime(2),
                    VeldRuw: reader.IsDBNull(3) ? null : reader.GetString(3),
                    VeldNummer: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    VeldDeelGebruik: reader.GetDecimal(5),
                    LeeftijdsCategorie: reader.IsDBNull(6) ? null : reader.GetString(6),
                    TeamNaam: reader.IsDBNull(7) ? null : reader.GetString(7),
                    Wedstrijd: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Bron: reader.GetString(9),
                    Wedstrijdcode: reader.IsDBNull(10) ? null : reader.GetInt64(10)));
        }

        // Resolutie in C# (zie klasse-doc-comment): "Planner"-rijen hebben al een veldnummer,
        // "Competitie"-rijen resolveren via de veldstring. Dezelfde dedup als het SQL Server-
        // origineel (per veldnummer+aanvangstijd+wedstrijd de eerste, op Bron gesorteerd) gebeurt
        // hier ná resolutie, want vóór resolutie is veldnummer voor Competitie-rijen nog leeg.
        var resolved = ruw
            .Select(r => new BestaandeWedstrijd
            {
                Datum = r.Datum,
                AanvangsTijd = r.Aanvang,
                EindTijd = TimeOnly.FromDateTime(r.Eind),
                VeldNummer = r.VeldNummer ?? PlannerShared.VindVeldNummer(r.VeldRuw, velden),
                VeldDeelGebruik = r.VeldDeelGebruik,
                LeeftijdsCategorie = r.LeeftijdsCategorie,
                TeamNaam = r.TeamNaam,
                Wedstrijd = r.Wedstrijd,
                Wedstrijdcode = r.Wedstrijdcode,
                Bron = r.Bron
            })
            .Where(w => w.VeldNummer != 0)
            .GroupBy(w => (w.VeldNummer, w.AanvangsTijd, w.Wedstrijd))
            .Select(g => g.OrderBy(w => w.Bron, StringComparer.Ordinal).First())
            .ToList();

        resolved.AddRange(await GetTrainingOccupationsAsync(connectionString, date, cc));
        return resolved;
    }

    /// <summary>
    /// Terugkerende trainingsbezetting uit <c>public.veldtraining</c> voor de weekdag van
    /// <paramref name="date"/> (#679, spoor B). Vrij per club instelbaar: een club zonder rijen
    /// houdt exact het gedrag van vóór deze feature.
    /// </summary>
    internal static async Task<List<BestaandeWedstrijd>> GetTrainingOccupationsAsync(
        string connectionString, DateOnly date, string? clubCode = null)
    {
        var results = new List<BestaandeWedstrijd>();
        int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;
        var cc = PostgresClubScope.Resolve(clubCode);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT t.veldnummer, t.vantijd, t.tottijd, t.omschrijving
            FROM public.veldtraining t
            INNER JOIN public.velden v ON v.veldnummer = t.veldnummer
            WHERE v.actief = true AND t.actief = true AND t.dagvanweek = @dag AND t.clubcode = @clubcode
            ORDER BY t.veldnummer, t.vantijd
            """, conn);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("clubcode", cc);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new BestaandeWedstrijd
            {
                Datum = date,
                AanvangsTijd = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                EindTijd = TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                VeldNummer = reader.GetInt32(0),
                VeldDeelGebruik = 1.00m,
                Wedstrijd = reader.IsDBNull(3) ? "Training" : reader.GetString(3),
                Bron = "Training"
            });
        return results;
    }

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingAsync(
        string connectionString, DateOnly date, long excludeWedstrijdcode, string? clubCode = null)
    {
        var all = await GetFieldOccupationsAsync(connectionString, date, clubCode);
        return FilterExcludingWedstrijdcode(all, excludeWedstrijdcode);
    }

    /// <summary>
    /// Sluit exact één wedstrijd uit op wedstrijdcode (#574) — nooit op tekst-contains: code 123
    /// matcht dan ook 3123. Rijen zonder wedstrijdcode (planner-slots zonder Sportlink-tegenhanger)
    /// blijven staan.
    /// </summary>
    internal static List<BestaandeWedstrijd> FilterExcludingWedstrijdcode(
        List<BestaandeWedstrijd> occupations, long excludeWedstrijdcode)
        => occupations.Where(o => o.Wedstrijdcode != excludeWedstrijdcode).ToList();

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchAsync(
        string connectionString, DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer,
        string? clubCode = null)
    {
        var all = await GetFieldOccupationsAsync(connectionString, date, clubCode);
        return all.Where(o =>
            !(o.VeldNummer == veldNummer &&
              o.AanvangsTijd == aanvangsTijd &&
              o.Wedstrijd != null && o.Wedstrijd.Trim() == wedstrijdNaam.Trim())
        ).ToList();
    }
}
