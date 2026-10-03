using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Deel;

namespace Planner.Endpoints.Deel;

/// <summary>
/// De gedeelde vertaling van een <see cref="PlannerShareModel"/> naar een HTTP-respons voor
/// <c>?format=html|pdf</c> op <c>planner/veldbezetting</c> en <c>planner/auto-plan</c> (#1364).
/// Staat hier en niet in de twee <c>PlannerFunction.cs</c>-bestanden: dit is tier-onafhankelijk
/// aansluitwerk (codekwaliteitsregel 1), de databasevraag zelf blijft per tier.
/// </summary>
public static class PlannerDeelEndpointCore
{
    public const string FormatJson = "json";
    public const string FormatHtml = "html";
    public const string FormatPdf = "pdf";

    /// <summary>Foutmelding bij <c>?format=pdf</c> terwijl de clubinstelling PDF-export uit staat (409).</summary>
    public const string PdfUitgeschakeldMelding =
        "PDF-export staat uit voor deze club. Een beheerder kan hem inschakelen bij Instellingen " +
        "(PDF-export), na bevestiging dat de QuestPDF Community-licentievoorwaarden gelden.";

    /// <summary>
    /// Leest en valideert <c>?format=</c>. Leeg of <c>json</c> = de bestaande JSON-respons
    /// (<paramref name="format"/> wordt <c>null</c>); een onbekende waarde is een fout.
    /// </summary>
    public static IActionResult? ControleerFormat(string? rauw, out string? format)
    {
        format = null;
        var waarde = (rauw ?? "").Trim().ToLowerInvariant();
        if (waarde.Length == 0 || waarde == FormatJson)
            return null;
        if (waarde is FormatHtml or FormatPdf)
        {
            format = waarde;
            return null;
        }
        return new BadRequestObjectResult(new { error = "Query parameter 'format' moet 'json', 'html' of 'pdf' zijn." });
    }

    /// <summary>
    /// #1459: DE beslissing voor elk deel-endpoint. PDF-export staat per club standaard UIT tot een
    /// beheerder bevestigt dat de QuestPDF Community-voorwaarden gelden. Bij <paramref name="format"/>
    /// <c>pdf</c> zonder die bevestiging: <c>409 Conflict</c> (zelfde code als de andere
    /// uitgeschakelde-functie-meldingen, bv. de Sportlink-schakelaar). Voor json/html wordt de
    /// instelling niet eens gelezen. De tierbestanden geven alleen de lees-delegate mee
    /// (<c>PdfExportInstelling.IsIngeschakeldAsync</c>, per tier alleen de query).
    /// </summary>
    /// <returns><c>Weigering</c> = de 409-respons of <c>null</c>; <c>PdfToegestaan</c> = mag de generator een PDF maken.</returns>
    public static async Task<(IActionResult? Weigering, bool PdfToegestaan)> BeslisPdfAsync(
        string? format, Func<Task<bool>> pdfIngeschakeld)
    {
        if (format != FormatPdf) return (null, false);
        return await pdfIngeschakeld()
            ? (null, true)
            : (new ConflictObjectResult(new { error = PdfUitgeschakeldMelding }), false);
    }

    /// <summary>
    /// #1459: <c>GET planner/pdf-export</c> — of PDF-export voor de club van de aanroeper aan staat.
    /// Open voor elke ingelogde rol, zodat Planning de PDF-knop ook voor de rol <c>user</c> correct
    /// toont (de volledige instellingen onder <c>beheer/settings</c> zijn admin-only).
    /// </summary>
    public static async Task<IActionResult> PdfExportStatusAsync(Func<Task<bool>> pdfIngeschakeld)
        => new OkObjectResult(new { pdfExportIngeschakeld = await pdfIngeschakeld() });

    /// <summary>Leest <c>?tab=huidig|optimaal</c> (default huidig).</summary>
    public static IActionResult? ControleerTab(string? rauw, out PlanWeergave weergave)
    {
        weergave = PlanWeergave.Huidig;
        var waarde = (rauw ?? "").Trim().ToLowerInvariant();
        switch (waarde)
        {
            case "":
            case "huidig":
                return null;
            case "optimaal":
                weergave = PlanWeergave.Optimaal;
                return null;
            default:
                return new BadRequestObjectResult(new { error = "Query parameter 'tab' moet 'huidig' of 'optimaal' zijn." });
        }
    }

    /// <summary>Strikt <c>yyyy-MM-dd</c>; geeft de 400-respons bij een ongeldige waarde.</summary>
    public static IActionResult? ControleerDatum(string? rauw, out DateOnly datum)
    {
        if (DateOnly.TryParseExact(rauw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out datum))
            return null;
        return new BadRequestObjectResult(new { error = "'datum' moet het formaat yyyy-MM-dd hebben." });
    }

    /// <summary>
    /// De respons voor <paramref name="format"/> <c>html</c> of <c>pdf</c>.
    /// <paramref name="bestandsBasis"/> is een vaste tekst uit de code (nooit invoer van de
    /// aanroeper); de datum komt uit de gevalideerde <see cref="PlannerShareModel.Peildatum"/>.
    /// </summary>
    public static IActionResult Maak(PlannerShareModel model, string format, string bestandsBasis, bool pdfToegestaan)
    {
        var naam = $"{bestandsBasis}-{model.Peildatum:yyyy-MM-dd}";
        if (format == FormatPdf)
            return new FileContentResult(PlannerPdfGenerator.Genereer(model, pdfToegestaan), "application/pdf")
            {
                FileDownloadName = naam + ".pdf",
            };

        return new BeveiligdeHtmlResult
        {
            Content = PlannerShareHtmlGenerator.Genereer(model),
            ContentType = "text/html; charset=utf-8",
            StatusCode = 200,
        };
    }

    /// <summary>
    /// De HTML-export wordt vanaf de API-origin geserveerd en bevat tekst uit Sportlink-data.
    /// Mocht iemand de URL rechtstreeks openen, dan mag die pagina geen script draaien, niets
    /// extern laden en geen same-origin-rechten krijgen (#1461): strikte CSP met <c>sandbox</c>,
    /// plus <c>nosniff</c> zodat de browser het type niet zelf gaat raden.
    /// </summary>
    public const string HtmlContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    private sealed class BeveiligdeHtmlResult : ContentResult
    {
        public override Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.Headers["Content-Security-Policy"] = HtmlContentSecurityPolicy;
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.StatusCode = StatusCode ?? 200;
            response.ContentType = ContentType;
            return response.WriteAsync(Content ?? "", System.Text.Encoding.UTF8);
        }
    }

    /// <summary>
    /// Het gevalideerde deel-verzoek van één aanroep: <see cref="Format"/> is <c>null</c> voor de
    /// gewone JSON-respons. De tierbestanden roepen alleen <see cref="Lees"/> en
    /// <see cref="VanVeldbezetting{T}"/> / <see cref="VanPlan{T}"/> aan, zodat de validatie en de
    /// responsvorm op beide tiers niet uit elkaar kunnen lopen.
    /// </summary>
    public sealed record DeelVerzoek(string? Format, PlanWeergave Weergave, DateOnly Datum)
    {
        /// <summary>
        /// Alleen <c>true</c> nadat <see cref="WeigerPdfAsync"/> de clubinstelling heeft gelezen en
        /// PDF heeft toegestaan (#1459). Standaard <c>false</c>: zonder die controle maakt de generator
        /// geen PDF (fail-closed).
        /// </summary>
        public bool PdfToegestaan { get; private set; }

        /// <summary>
        /// #1459: past <see cref="BeslisPdfAsync"/> toe op dit verzoek — 409 als PDF gevraagd is en
        /// uit staat, anders <c>null</c> en wordt <see cref="PdfToegestaan"/> gezet.
        /// </summary>
        public async Task<IActionResult?> WeigerPdfAsync(Func<Task<bool>> pdfIngeschakeld)
        {
            var (weigering, toegestaan) = await BeslisPdfAsync(Format, pdfIngeschakeld);
            PdfToegestaan = toegestaan;
            return weigering;
        }

        /// <summary>Het deel-document voor <c>planner/veldbezetting</c>, of <c>null</c> (= JSON).</summary>
        public IActionResult? VanVeldbezetting<T>(IEnumerable<T> items, string clubCode) where T : IVeldbezettingRegel
            => Format == null ? null : Maak(
                PlannerShareModelBuilder.VanVeldbezetting(items.Cast<IVeldbezettingRegel>(), Datum, clubCode),
                Format, "veldbezetting", PdfToegestaan);

        /// <summary>Het deel-document voor <c>planner/auto-plan</c>, of <c>null</c> (= JSON).</summary>
        public IActionResult? VanPlan<T>(IEnumerable<T> wedstrijden, string clubCode) where T : IPlanWedstrijdRegel
            => Format == null ? null : Maak(
                PlannerShareModelBuilder.VanPlan(wedstrijden.Cast<IPlanWedstrijdRegel>(), Datum, clubCode, Weergave),
                Format, Weergave == PlanWeergave.Optimaal ? "optimale-planning" : "huidige-planning", PdfToegestaan);
    }

    /// <summary>
    /// Valideert <c>?format=</c>, <c>?tab=</c> en — alleen bij een deel-document — de datum. Geeft
    /// de 400-respons terug bij een ongeldige waarde, anders <c>null</c>.
    /// </summary>
    public static IActionResult? Lees(string? format, string? tab, string? datum, out DeelVerzoek verzoek)
    {
        verzoek = new DeelVerzoek(null, PlanWeergave.Huidig, default);
        var fout = ControleerFormat(format, out var f);
        if (fout != null) return fout;
        fout = ControleerTab(tab, out var weergave);
        if (fout != null) return fout;
        var datumWaarde = default(DateOnly);
        if (f != null)
        {
            fout = ControleerDatum(datum, out datumWaarde);
            if (fout != null) return fout;
        }
        verzoek = new DeelVerzoek(f, weergave, datumWaarde);
        return null;
    }
}
