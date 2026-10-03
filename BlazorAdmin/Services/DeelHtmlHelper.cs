using System.Text.RegularExpressions;

namespace BlazorAdmin.Services;

/// <summary>Pure helper voor <c>DeelPaneel</c> (#1362): de enige plek met de script-strip-logica
/// (voorheen <c>ExportHtmlZonderScript</c> in Dagplanning/VeldOptimalisatie, XSS-verdediging #603/#1010).</summary>
/// <remarks>
/// <b>Dit is geen sanitizer en geen beveiligingsgrens.</b> De echte grens is de
/// <c>sandbox</c>-attribuut van de iframe in <c>DeelPaneel.razor</c> (#603). Daarbij mag
/// <c>allow-scripts</c> nooit worden toegevoegd zolang <c>allow-same-origin</c> er staat: die
/// combinatie laat de ingebedde pagina haar eigen sandbox opheffen. De server zet bovendien een
/// strikte CSP op de HTML-export zelf (#1461).
/// </remarks>
public static class DeelHtmlHelper
{
    /// <summary>Haalt <c>&lt;script&gt;</c>-elementen uit de HTML voor preview en e-mailversie. De
    /// download houdt het script wél (in een los HTML-bestand werkt de interactie). Bewust geen
    /// static Regex-veld en geen RegexOptions.Compiled: dat faalt in Blazor WebAssembly
    /// (NullReferenceException bij het renderen, geen buildfout).</summary>
    public static string? ZonderScript(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        return Regex.Replace(
            html, @"<script\b[^>]*>.*?</script>", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
