using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;


namespace Planner.Shared
{
    public static partial class PlannerHtmlGenerator
    {
        private static void SchrijfTabelHeader(StringBuilder sb)
        {
            sb.AppendLine($"<tr style='background:{BG_TIJD};'><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Veld</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Aanvang</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Einde</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Status</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Wedstrijd</th></tr>");
        }

        /// <summary>Eén tabelrij van de chrono- en categorietabel; <paramref name="borderTop"/> is een kant-en-klare CSS-declaratie of leeg.</summary>
        private static void SchrijfItemRij(StringBuilder sb, string veld, TimeOnly tijd, TimeOnly einde,
            string status, string kleur, string wedstrijd, string borderTop)
        {
            string rijBg = kleur == BLK_OUD_RAND ? "#1a0d00" : kleur == BLK_NIEUW_RAND ? "#0d1a0d" : "transparent";
            sb.AppendLine($"<tr style='background:{rijBg};{borderTop}border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>{EncodeText(veld)}</td><td style='padding:4px 8px;'>{tijd:HH:mm}</td><td style='padding:4px 8px;'>{einde:HH:mm}</td><td style='padding:4px 8px;color:{kleur};'>{EncodeText(status)}</td><td style='padding:4px 8px;'>{EncodeText(wedstrijd)}</td></tr>");
        }

        private static void TekenChronoTabel(StringBuilder sb,
            List<BestaandeWedstrijd> wedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            Dictionary<string, OptimalisatieSuggestie> verplaatsVan,
            HtmlInstellingen instellingen)
        {
            sb.AppendLine($"<table style='border-collapse:collapse;width:100%;font-size:11px;'>");
            SchrijfTabelHeader(sb);

            var verplaatsteKeys = new HashSet<string>(suggesties.Select(SuggestieSleutel));
            var getoond = new HashSet<string>();
            var items = new List<(TimeOnly Tijd, TimeOnly Einde, string Veld, string Wedstrijd, string Status, string Kleur)>();

            foreach (var w in wedstrijden.OrderBy(w => w.AanvangsTijd).ThenBy(w => w.VeldNummer))
            {
                string key = WedstrijdSleutel(w);
                if (!getoond.Add(key)) continue;
                var vn = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";

                if (verplaatsteKeys.Contains(key))
                { var s = verplaatsVan[key]; items.Add((w.AanvangsTijd, w.EindTijd, vn, w.Wedstrijd?.Trim() ?? "", $"⟶ {s.NieuwVeld} {s.NieuweTijd}", BLK_OUD_RAND)); }
                else if (IsEersteElftalWedstrijd(w.Wedstrijd, instellingen))
                    items.Add((w.AanvangsTijd, w.EindTijd, vn, w.Wedstrijd?.Trim() ?? "", "🔒 Vast", BLK_VAST_RAND));
                else
                    items.Add((w.AanvangsTijd, w.EindTijd, vn, w.Wedstrijd?.Trim() ?? "", "", TXT_DIM));
            }
            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var t);
                var orig = wedstrijden.FirstOrDefault(w => w.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 75;
                items.Add((t, t.AddMinutes(duur), s.NieuwVeld, $"★ {s.Wedstrijd}", $"← van {s.HuidigVeld} {s.HuidigeTijd}", BLK_NIEUW_RAND));
            }

            foreach (var item in items.OrderBy(i => i.Tijd).ThenBy(i => i.Veld))
            {
                SchrijfItemRij(sb, item.Veld, item.Tijd, item.Einde, item.Status, item.Kleur, item.Wedstrijd, "");
            }
            sb.AppendLine("</table>");
        }

        private static void TekenCategorieTabel(StringBuilder sb,
            List<BestaandeWedstrijd> wedstrijden,
            List<BestaandeWedstrijd> nieuweBezetting,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            Dictionary<string, OptimalisatieSuggestie> verplaatsVan,
            HtmlInstellingen instellingen)
        {
            // Gebruik dezelfde data als de chrono-tabel: originele wedstrijden + suggesties
            var verplaatsteKeys = new HashSet<string>(suggesties.Select(SuggestieSleutel));
            var getoond = new HashSet<string>();
            var items = new List<(int Leeftijd, TimeOnly Tijd, TimeOnly Einde, string Veld, string Wedstrijd, string Status, string Kleur)>();

            foreach (var w in wedstrijden.OrderBy(w => w.AanvangsTijd).ThenBy(w => w.VeldNummer))
            {
                string key = WedstrijdSleutel(w);
                if (!getoond.Add(key)) continue;
                var vn = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";
                string naam = w.Wedstrijd?.Trim() ?? "";
                int leeftijd = LeeftijdUitNaam(naam, instellingen.ClubCode);

                if (verplaatsteKeys.Contains(key))
                { var s = verplaatsVan[key]; items.Add((leeftijd, w.AanvangsTijd, w.EindTijd, vn, naam, $"⟶ {s.NieuwVeld} {s.NieuweTijd}", BLK_OUD_RAND)); }
                else if (IsEersteElftalWedstrijd(naam, instellingen))
                    items.Add((leeftijd, w.AanvangsTijd, w.EindTijd, vn, naam, "🔒 Vast", BLK_VAST_RAND));
                else
                    items.Add((leeftijd, w.AanvangsTijd, w.EindTijd, vn, naam, "", TXT_DIM));
            }
            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var t);
                int leeftijd = LeeftijdUitNaam(s.Wedstrijd, instellingen.ClubCode);
                var orig = wedstrijden.FirstOrDefault(w => w.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 75;
                items.Add((leeftijd, t, t.AddMinutes(duur), s.NieuwVeld, $"★ {s.Wedstrijd}", $"← van {s.HuidigVeld} {s.HuidigeTijd}", BLK_NIEUW_RAND));
            }

            sb.AppendLine($"<table style='border-collapse:collapse;width:100%;font-size:11px;'>");
            SchrijfTabelHeader(sb);

            int vorigeLeeftijd = -1;
            foreach (var item in items.OrderBy(i => i.Leeftijd).ThenBy(i => i.Tijd).ThenBy(i => i.Veld))
            {
                string borderTop = item.Leeftijd != vorigeLeeftijd && vorigeLeeftijd >= 0 ? $"border-top:2px solid {BG_TIJD};" : "";
                vorigeLeeftijd = item.Leeftijd;
                SchrijfItemRij(sb, item.Veld, item.Tijd, item.Einde, item.Status, item.Kleur, item.Wedstrijd, borderTop);
            }
            sb.AppendLine("</table>");
        }

    }
}
