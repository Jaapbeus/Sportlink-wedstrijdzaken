using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;


namespace Planner.Shared
{
    public static partial class PlannerHtmlGenerator
    {
        public static string GenereerEmailHtml(
            DateOnly datum,
            List<BestaandeWedstrijd> alleWedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            // doel ("huidig"/"optimaal") wordt momenteel niet gebruikt in de gegenereerde HTML
            string doel,
            string browserUrl,
            HtmlInstellingen instellingen)
        {
            var sb = new StringBuilder();
            var nl = new System.Globalization.CultureInfo("nl-NL");

            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'></head>");
            sb.AppendLine($"<body style='margin:0;padding:20px;font-family:Arial,sans-serif;background:#ffffff;color:#333;'>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;'><a href='{SanitizeHref(browserUrl)}' style='color:#0969da;font-size:13px;'>&#128279; Bekijk in browser (meer functies)</a></p>");
            sb.AppendLine($"<h2 style='margin:0 0 5px 0;'>Veldplanner — Optimalisatieadvies</h2>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;color:#666;'>{datum.ToString("dddd d MMMM yyyy", nl)} — {suggesties.Count} suggestie(s)</p>");

            if (suggesties.Count == 0)
            { sb.AppendLine("<p style='color:#666;'>Geen suggesties — planning is al optimaal.</p>"); }

            sb.AppendLine("<table style='border-collapse:collapse;width:100%;max-width:700px;font-size:12px;border:1px solid #e5e7eb;'>");
            sb.AppendLine("<tr style='background:#f9fafb;'><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Tijd</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Veld</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Wedstrijd</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Status</th></tr>");

            var verplaatsteKeys = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties) verplaatsteKeys[SuggestieSleutel(s)] = s;

            var getoond = new HashSet<string>();
            var rijen = new List<(TimeOnly T, string V, string W, string S, string Bg, string Fg)>();

            foreach (var w in alleWedstrijden.GroupBy(w => WedstrijdSleutel(w)).Select(g => g.First()).OrderBy(w => w.AanvangsTijd))
            {
                string key = WedstrijdSleutel(w);
                var vn = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? "";
                if (verplaatsteKeys.TryGetValue(key, out var s))
                    rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", $"⟶ {s.NieuwVeld} {s.NieuweTijd}", "#fef3c7", "#92400e"));
                else if (IsEersteElftalWedstrijd(w.Wedstrijd, instellingen))
                    rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "🔒 Vast", "#dbeafe", "#1e40af"));
                else rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "", "#fff", "#666"));
            }
            foreach (var s in suggesties) { TimeOnly.TryParse(s.NieuweTijd, out var t); rijen.Add((t, s.NieuwVeld, $"★ {s.Wedstrijd}", $"← {s.HuidigVeld} {s.HuidigeTijd}", "#fef3c7", "#92400e")); }

            foreach (var r in rijen.OrderBy(r => r.T).ThenBy(r => r.V))
                sb.AppendLine($"<tr style='background:{r.Bg};border-bottom:1px solid #e5e7eb;'><td style='padding:5px 6px;'>{r.T:HH:mm}</td><td style='padding:5px 6px;'>{EncodeText(r.V)}</td><td style='padding:5px 6px;'>{EncodeText(r.W)}</td><td style='padding:5px 6px;color:{r.Fg};'>{EncodeText(r.S)}</td></tr>");

            sb.AppendLine("</table>");
            var plannerNaamFooter = instellingen.PlannerAfzenderNaam;
            sb.AppendLine($"<p style='margin:15px 0 0 0;font-size:11px;color:#999;'>{EncodeText(plannerNaamFooter)}</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
    }
}
