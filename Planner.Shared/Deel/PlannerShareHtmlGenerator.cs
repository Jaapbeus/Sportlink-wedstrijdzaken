using System;
using System.Linq;
using System.Net;
using System.Text;

namespace Planner.Shared.Deel
{
    /// <summary>
    /// Rendert een <see cref="PlannerShareModel"/> als zelfstandige HTML-pagina (#1364): dezelfde
    /// kolommen als de PDF, voor de Planning-pagina (<c>GET /api/planner/veldbezetting</c>) en voor
    /// de gedeelde weergave van Veld optimalisatie. Geen script, uitsluitend inline stijl, zodat de
    /// uitvoer zowel in de sandboxed preview als in een e-mail werkt.
    /// <para>
    /// <b>Encodering.</b> Elke waarde gaat door <see cref="WebUtility.HtmlEncode"/> — teamnamen en
    /// tegenstanders komen uit Sportlink en zijn dus niet vertrouwd (zelfde discipline als #1010 in
    /// <see cref="PlannerHtmlGenerator"/>). Er worden geen href's of stijlwaarden uit data opgebouwd.
    /// </para>
    /// </summary>
    public static class PlannerShareHtmlGenerator
    {
        private const string KopKleur = "#1F3A5F";
        private const string RasterKleur = "#D0D7DE";
        private const string ZebraKleur = "#F6F8FA";
        private const string GedimdKleur = "#57606A";

        /// <summary>Genereert de HTML. Een lege lijst geeft een pagina met een melding.</summary>
        public static string Genereer(PlannerShareModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var titel = Enc(model.Titel);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"nl\"><head><meta charset=\"utf-8\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append("<title>").Append(titel).Append("</title></head>")
              .Append("<body style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#1F2328;margin:16px;\">")
              .Append("<h2 style=\"color:").Append(KopKleur).Append(";margin:0 0 4px 0;\">").Append(titel).Append("</h2>")
              .Append("<p style=\"color:").Append(GedimdKleur).Append(";font-size:12px;margin:0 0 12px 0;\">Club: ")
              .Append(Enc(model.ClubCode)).Append(" &middot; Speeldag: ")
              .Append(Enc(PlannerShareModelBuilder.DatumTekst(model.Peildatum))).Append("</p>");

            if (model.Wedstrijden.Count == 0)
            {
                sb.Append("<p style=\"font-style:italic;color:").Append(GedimdKleur).Append(";\">Geen wedstrijden gepland op ")
                  .Append(Enc(PlannerShareModelBuilder.DatumTekst(model.Peildatum))).Append(".</p>");
            }
            else
            {
                var metScheids = model.Wedstrijden.Any(w => !string.IsNullOrWhiteSpace(w.Scheidsrechter));
                sb.Append("<table style=\"border-collapse:collapse;width:100%;\"><thead><tr>");
                Kop(sb, "Tijd"); Kop(sb, "Team"); Kop(sb, "Tegenstander"); Kop(sb, "Veld"); Kop(sb, "Competitie");
                if (metScheids) Kop(sb, "Scheidsrechter");
                sb.Append("</tr></thead><tbody>");
                for (var i = 0; i < model.Wedstrijden.Count; i++)
                {
                    var w = model.Wedstrijden[i];
                    sb.Append("<tr>");
                    var zebra = i % 2 == 1;
                    Cel(sb, w.Tijd, zebra); Cel(sb, w.TeamWeergave, zebra); Cel(sb, w.Tegenstander, zebra);
                    Cel(sb, w.Veld, zebra); Cel(sb, w.Competitie, zebra);
                    if (metScheids) Cel(sb, w.Scheidsrechter, zebra);
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }

            return sb.Append("</body></html>").ToString();
        }

        private static void Kop(StringBuilder sb, string tekst) =>
            sb.Append("<th style=\"background:").Append(KopKleur)
              .Append(";color:#fff;text-align:left;padding:4px 6px;\">").Append(tekst).Append("</th>");

        private static void Cel(StringBuilder sb, string? tekst, bool zebra) =>
            sb.Append("<td style=\"border-bottom:1px solid ").Append(RasterKleur).Append(';')
              .Append(zebra ? "background:" + ZebraKleur + ";" : "")
              .Append("padding:3px 6px;\">").Append(string.IsNullOrWhiteSpace(tekst) ? "&mdash;" : Enc(tekst)).Append("</td>");

        private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
