using System.Text;

namespace SportlinkFunction.Planner;

public static class TeamScheduleHtmlRenderer
{
    private static readonly System.Globalization.CultureInfo NL = new("nl-NL");

    public static string Render(TeamScheduleResponse schedule)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"nl\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>Teamschema {System.Net.WebUtility.HtmlEncode(schedule.Team)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Arial,sans-serif;margin:24px;background:#f5f5f5;}");
        sb.AppendLine("h1{color:#2c3e50;}");
        sb.AppendLine(".kalender{display:flex;flex-wrap:wrap;gap:6px;margin:16px 0;}");
        sb.AppendLine(".dag{width:90px;text-align:center;padding:6px 4px;border-radius:6px;font-size:12px;font-weight:600;}");
        sb.AppendLine(".vrij{background:#27ae60;color:#fff;}");
        sb.AppendLine(".oefenwedstrijd{background:#e67e22;color:#fff;}");
        sb.AppendLine(".bezet{background:#e74c3c;color:#fff;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;background:#fff;border-radius:8px;overflow:hidden;}");
        sb.AppendLine("th{background:#2c3e50;color:#fff;padding:8px 12px;text-align:left;}");
        sb.AppendLine("td{padding:8px 12px;border-bottom:1px solid #eee;}");
        sb.AppendLine(".badge{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600;}");
        sb.AppendLine(".badge-competitie{background:#2980b9;color:#fff;}");
        sb.AppendLine(".badge-beker{background:#8e44ad;color:#fff;}");
        sb.AppendLine(".badge-oefenwedstrijd{background:#e67e22;color:#fff;}");
        sb.AppendLine(".thuis{color:#27ae60;font-weight:600;}");
        sb.AppendLine(".uit{color:#e74c3c;font-weight:600;}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h1>Teamschema — {System.Net.WebUtility.HtmlEncode(schedule.Team)}</h1>");
        sb.AppendLine($"<p>Seizoen loopt tot: <strong>{schedule.SeizoenEinde}</strong></p>");

        // Legenda
        sb.AppendLine("<p><span style=\"display:inline-block;width:14px;height:14px;background:#27ae60;border-radius:3px;margin-right:4px;\"></span>Vrij &nbsp;");
        sb.AppendLine("<span style=\"display:inline-block;width:14px;height:14px;background:#e67e22;border-radius:3px;margin-right:4px;\"></span>Oefenwedstrijd &nbsp;");
        sb.AppendLine("<span style=\"display:inline-block;width:14px;height:14px;background:#e74c3c;border-radius:3px;margin-right:4px;\"></span>Bezet (competitie/beker)</p>");

        // Kalender-strook
        sb.AppendLine("<div class=\"kalender\">");
        foreach (var zat in schedule.Zaterdagen)
        {
            var d = DateOnly.Parse(zat.Datum);
            var label = d.ToString("d MMM", NL);
            var cls = zat.Status switch
            {
                "bezet" => "dag bezet",
                "oefenwedstrijd" => "dag oefenwedstrijd",
                _ => "dag vrij"
            };
            sb.AppendLine($"<div class=\"{cls}\" title=\"{System.Net.WebUtility.HtmlEncode(zat.Status)}\">{System.Net.WebUtility.HtmlEncode(label)}</div>");
        }
        sb.AppendLine("</div>");

        // Wedstrijdenlijst
        if (schedule.Wedstrijden.Count == 0)
        {
            sb.AppendLine("<p><em>Geen wedstrijden gevonden in de komende periode.</em></p>");
        }
        else
        {
            sb.AppendLine("<h2>Wedstrijden</h2>");
            sb.AppendLine("<table><thead><tr><th>Datum</th><th>Aanvang</th><th>Thuis/Uit</th><th>Tegenstander</th><th>Type</th><th>Veld</th></tr></thead><tbody>");
            foreach (var w in schedule.Wedstrijden)
            {
                var d = DateOnly.Parse(w.Datum);
                var datumLabel = d.ToString("ddd d MMM yyyy", NL);
                var thuisUitClass = w.ThuisUit == "thuis" ? "thuis" : "uit";
                var badgeClass = w.Type switch
                {
                    "beker" => "badge badge-beker",
                    "oefenwedstrijd" => "badge badge-oefenwedstrijd",
                    _ => "badge badge-competitie"
                };
                sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(datumLabel)}</td>" +
                    $"<td>{System.Net.WebUtility.HtmlEncode(w.AanvangsTijd)}</td>" +
                    $"<td><span class=\"{thuisUitClass}\">{System.Net.WebUtility.HtmlEncode(w.ThuisUit)}</span></td>" +
                    $"<td>{System.Net.WebUtility.HtmlEncode(w.Tegenstander)}</td>" +
                    $"<td><span class=\"{badgeClass}\">{System.Net.WebUtility.HtmlEncode(w.Type)}</span></td>" +
                    $"<td>{System.Net.WebUtility.HtmlEncode(w.Veld ?? "")}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
