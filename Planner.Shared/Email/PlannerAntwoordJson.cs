using Newtonsoft.Json;

namespace Planner.Shared.Email;

/// <summary>
/// Uitkomst van het opponent-pad (#1139): óf een kant-en-klaar antwoord, óf het via de tegenstander
/// herkende eigen team, óf (default) niets omdat het pad niet van toepassing is.
/// </summary>
public readonly record struct TegenstanderZoekUitkomst(string? Antwoord, string? EigenTeam);

/// <summary>
/// De JSON-antwoorden die <c>BerichtPipeline.VerwerkMetPlannerAsync</c> per verzoektype teruggeeft
/// (#1628). Ze bevatten geen databasetoegang en zijn op beide databasetiers identiek, dus staan ze
/// hier één keer; de tiers bepalen alleen <i>welk</i> antwoord van toepassing is. Veldnamen en
/// volgorde zijn contract met <see cref="TemplateAntwoordBouwer"/>: wijzig ze niet zonder de
/// consument mee te nemen.
/// </summary>
public static class PlannerAntwoordJson
{
    public static string DatumOnbekend() => JsonConvert.SerializeObject(new { datumOnbekend = true });

    public static string WedstrijdAlIngepland(object wedstrijd)
        => JsonConvert.SerializeObject(new { wedstrijdAlIngepland = true, wedstrijd });

    public static string TeamOnbekend(string? tegenstander)
        => JsonConvert.SerializeObject(new { teamOnbekend = true, tegenstander });

    public static string MultiDatum(IReadOnlyList<(string Datum, object? Response)> resultaten)
        => JsonConvert.SerializeObject(new
        {
            multiDatum = true,
            resultaten = resultaten.Select(r => new { datum = r.Datum, response = r.Response }).ToList()
        });

    /// <summary>
    /// Het meer-datum-antwoord: één plannercheck per datum, in volgorde, na elkaar (#1730). De tiers
    /// leveren alleen de check; de lus en het antwoord staan hier één keer.
    /// </summary>
    public static async Task<string> MultiDatumAsync(IReadOnlyList<string> datums, Func<string, Task<object?>> check)
    {
        var resultaten = new List<(string Datum, object? Response)>();
        foreach (var datum in datums)
            resultaten.Add((datum, await check(datum)));
        return MultiDatum(resultaten);
    }

    public static string OnvoldoendeHerplanGegevens()
        => JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

    public static string HerplanWedstrijdNietGevonden(string? teamNaam, string? datum)
        => JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {teamNaam} op {datum}" });

    /// <summary>
    /// Antwoord <c>herplanTeLaat</c> als de wedstrijd binnen de deadline valt, anders <c>null</c>
    /// (ook bij een onparseerbare wedstrijddatum).
    /// </summary>
    public static string? HerplanTeLaat(object wedstrijd, string? wedstrijdDatum, int deadlineDagen)
    {
        if (!DateOnly.TryParse(wedstrijdDatum, out var datum)) return null;

        var dagenTotWedstrijd = (datum.ToDateTime(TimeOnly.MinValue) - DateTime.Today).TotalDays;
        if (dagenTotWedstrijd >= deadlineDagen) return null;

        return JsonConvert.SerializeObject(new
        {
            herplanTeLaat = true,
            wedstrijd,
            deadlineDagen,
            dagenTotWedstrijd = (int)dagenTotWedstrijd
        });
    }

    public static string HerplanMetGewensteDatum(object wedstrijd, string gewensteDatum, object? beschikbaarheid)
        => JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum, beschikbaarheid });

    public static string HerplanOpties(object wedstrijd, object? herplanOpties)
        => JsonConvert.SerializeObject(new { wedstrijd, herplanOpties });

    public static string TeamContact(string? teamNaam, bool coachGevonden)
        => JsonConvert.SerializeObject(new { teamContactOpgevraagd = true, teamNaam, coachGevonden });
}
