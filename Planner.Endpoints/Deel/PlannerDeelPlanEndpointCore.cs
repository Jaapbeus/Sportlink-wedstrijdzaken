using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Planner.Shared.Deel;

namespace Planner.Endpoints.Deel;

/// <summary>
/// Stateless <c>POST planner/auto-plan/deel</c> (#1460): de browser stuurt de planning zoals die op
/// Veld optimalisatie getoond wordt — inclusief handmatig versleepte blokken — en de server rendert
/// daar hetzelfde HTML/PDF-document van als van <c>planner/auto-plan?format=</c>. Niets wordt
/// opgeslagen of herberekend. Tier-onafhankelijk (codekwaliteitsregel 1): beide <c>PlannerFunction</c>-
/// bestanden roepen alleen <see cref="VerwerkAsync"/> aan, met hun eigen lees-delegate voor de PDF-instelling.
/// <para>
/// <b>Vertrouwensgrens.</b> De inhoud komt van de client en wordt dus als niet-vertrouwd behandeld:
/// begrensd aantal regels, begrensde veldlengtes, strikte tijdnotatie, geen stuurtekens. Het
/// document wordt daarna door dezelfde generators gerenderd, die elke waarde HTML-encoden. De club
/// komt altijd van de aanroeper (Easy Auth/<c>X-Club-Code</c>), nooit uit de body; een meegestuurde
/// <c>clubCode</c> die daarvan afwijkt wordt geweigerd.
/// </para>
/// </summary>
public static class PlannerDeelPlanEndpointCore
{
    public const int MaxRegels = 500;
    public const int MaxTekstLengte = 200;
    public const int MaxBodyBytes = 512 * 1024;
    public static readonly DateOnly MinDatum = new(2020, 1, 1);
    public static readonly DateOnly MaxDatum = new(2100, 12, 31);

    private static readonly Regex TijdPatroon = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed class Verzoek
    {
        [JsonProperty("datum")] public string? Datum { get; set; }
        [JsonProperty("tab")] public string? Tab { get; set; }
        [JsonProperty("clubCode")] public string? ClubCode { get; set; }
        [JsonProperty("wedstrijden")] public List<Regel>? Wedstrijden { get; set; }
    }

    public sealed class Regel
    {
        [JsonProperty("teamNaam")] public string? TeamNaam { get; set; }
        [JsonProperty("wedstrijd")] public string? Wedstrijd { get; set; }
        [JsonProperty("competitiesoort")] public string? Competitiesoort { get; set; }
        [JsonProperty("tijd")] public string? Tijd { get; set; }
        [JsonProperty("veld")] public string? Veld { get; set; }
    }

    /// <summary>
    /// Leest body en <c>?format=</c> uit het verzoek en roept <see cref="Verwerk"/> aan — het hele
    /// tier-aansluitwerk. #1459: bij <c>format=pdf</c> eerst de gedeelde beslissing
    /// <see cref="PlannerDeelEndpointCore.BeslisPdfAsync"/> (409 als PDF-export voor de club uit staat);
    /// <paramref name="pdfIngeschakeld"/> is de per-tier lees-delegate (<c>PdfExportInstelling.IsIngeschakeldAsync</c>).
    /// </summary>
    public static async Task<IActionResult> VerwerkAsync(
        Microsoft.AspNetCore.Http.HttpRequest req, string clubCode, Func<Task<bool>> pdfIngeschakeld)
    {
        string? format = req.Query["format"];
        var (weigering, pdfToegestaan) = await PlannerDeelEndpointCore.BeslisPdfAsync(
            (format ?? "").Trim().ToLowerInvariant(), pdfIngeschakeld);
        if (weigering != null) return weigering;
        return Verwerk(await new StreamReader(req.Body).ReadToEndAsync(), format, clubCode, pdfToegestaan);
    }

    /// <summary>
    /// Valideert en rendert. <paramref name="body"/> is de ruwe JSON, <paramref name="format"/> de
    /// ruwe <c>?format=</c> (alleen <c>html</c>/<c>pdf</c>), <paramref name="clubCode"/> de club van de aanroeper.
    /// <paramref name="pdfToegestaan"/> is de uitkomst van <see cref="PlannerDeelEndpointCore.BeslisPdfAsync"/>
    /// (#1459); zonder die toestemming maakt de generator geen PDF.
    /// </summary>
    public static IActionResult Verwerk(string? body, string? format, string clubCode, bool pdfToegestaan)
    {
        var f = (format ?? "").Trim().ToLowerInvariant();
        if (f != PlannerDeelEndpointCore.FormatHtml && f != PlannerDeelEndpointCore.FormatPdf)
            return Fout("Query parameter 'format' moet 'html' of 'pdf' zijn.");

        if (body != null && body.Length > MaxBodyBytes)
            return new ObjectResult(new { error = "Request body is te groot." }) { StatusCode = 413 };
        if (string.IsNullOrWhiteSpace(body))
            return Fout("Request body is verplicht.");

        Verzoek? v;
        try { v = JsonConvert.DeserializeObject<Verzoek>(body); }
        catch (JsonException) { return Fout("Request body is geen geldige JSON."); }
        if (v == null) return Fout("Request body is verplicht.");

        var datumFout = PlannerDeelEndpointCore.ControleerDatum(v.Datum, out var datum);
        if (datumFout != null) return datumFout;
        if (datum < MinDatum || datum > MaxDatum)
            return Fout($"'datum' moet tussen {MinDatum:yyyy-MM-dd} en {MaxDatum:yyyy-MM-dd} liggen.");

        var tabFout = PlannerDeelEndpointCore.ControleerTab(v.Tab, out var weergave);
        if (tabFout != null) return tabFout;

        if (!string.IsNullOrWhiteSpace(v.ClubCode)
            && !string.Equals(v.ClubCode.Trim(), clubCode, StringComparison.OrdinalIgnoreCase))
            return new ObjectResult(new { error = "'clubCode' wijkt af van de club van de aanroeper." }) { StatusCode = 403 };

        if (v.Wedstrijden == null) return Fout("'wedstrijden' is verplicht.");
        if (v.Wedstrijden.Count > MaxRegels) return Fout($"Maximaal {MaxRegels} wedstrijden toegestaan.");

        var regels = new List<GetoondePlanRegel>(v.Wedstrijden.Count);
        for (var i = 0; i < v.Wedstrijden.Count; i++)
        {
            var r = v.Wedstrijden[i];
            if (r == null) return Fout($"wedstrijden[{i}] ontbreekt.");
            var fout = ControleerRegel(r, i);
            if (fout != null) return fout;
            regels.Add(new GetoondePlanRegel(r.TeamNaam!.Trim(), r.Wedstrijd ?? "", r.Competitiesoort, r.Tijd, r.Veld));
        }

        var model = PlannerShareModelBuilder.VanGetoondePlan(regels, datum, clubCode, weergave);
        return PlannerDeelEndpointCore.Maak(model, f,
            weergave == PlanWeergave.Optimaal ? "optimale-planning" : "huidige-planning", pdfToegestaan);
    }

    private static IActionResult? ControleerRegel(Regel r, int i)
    {
        if (string.IsNullOrWhiteSpace(r.TeamNaam)) return Fout($"wedstrijden[{i}].teamNaam is verplicht.");
        foreach (var (naam, waarde) in new[]
                 {
                     ("teamNaam", r.TeamNaam), ("wedstrijd", r.Wedstrijd),
                     ("competitiesoort", r.Competitiesoort), ("veld", r.Veld),
                 })
        {
            if (waarde == null) continue;
            if (waarde.Length > MaxTekstLengte) return Fout($"wedstrijden[{i}].{naam} is te lang (max {MaxTekstLengte} tekens).");
            if (waarde.Any(char.IsControl)) return Fout($"wedstrijden[{i}].{naam} bevat ongeldige tekens.");
        }
        if (!string.IsNullOrWhiteSpace(r.Tijd) && !TijdPatroon.IsMatch(r.Tijd.Trim()))
            return Fout($"wedstrijden[{i}].tijd moet het formaat HH:mm hebben.");
        return null;
    }

    private static BadRequestObjectResult Fout(string melding) => new(new { error = melding });
}
