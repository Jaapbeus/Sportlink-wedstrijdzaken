using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// Genereert een visuele HTML veldplanner in Sportlink-stijl.
    /// Browser-versie: side-by-side oud/nieuw met klik-interactie.
    /// Email-versie: versimpelde inline CSS tabel.
    /// </summary>
    public static class PlannerHtmlGenerator
    {
        private const int PixelsPerUur = 100;
        private const int VeldRijHoogte = 80;
        private const int VeldHeaderBreedte = 50;

        // Kleuren
        private const string BG = "#0d1117";
        private const string BG_VELD = "#161b22";
        private const string BG_TIJD = "#21262d";
        private const string TXT = "#e6edf3";
        private const string TXT_DIM = "#8b949e";
        private const string BLK_NORMAAL = "#2d333b";
        private const string BLK_RAND = "#444c56";
        private const string BLK_VAST = "#1f3a5f";
        private const string BLK_VAST_RAND = "#388bfd";
        private const string BLK_OUD = "#3d1f00";
        private const string BLK_OUD_RAND = "#d29922";
        private const string BLK_NIEUW = "#1a3a1a";
        private const string BLK_NIEUW_RAND = "#2ea043";
        private const string HIGHLIGHT = "#58a6ff";

        public static string GenereerHtml(
            DateOnly datum,
            List<BestaandeWedstrijd> alleWedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            string doel)
        {
            var sb = new StringBuilder();
            var nl = new System.Globalization.CultureInfo("nl-NL");
            var actieveVelden = velden.Where(v => v.VeldNummer <= 5).OrderBy(v => v.VeldNummer).ToList();

            // Dedup wedstrijden
            var wedstrijden = alleWedstrijden
                .GroupBy(w => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}")
                .Select(g => g.First()).ToList();

            // Bereken tijdlijn: start bij 08:30, eind bij laatste wedstrijd + 30 min
            int startMin = 8 * 60 + 30; // 08:30
            var laatsteEind = wedstrijden.Max(w => w.EindTijd);
            // Voeg suggestie-eindtijden toe
            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var nt);
                var orig = wedstrijden.FirstOrDefault(w => w.Wedstrijd?.Trim() == s.Wedstrijd.Trim() &&
                    w.VeldNummer == s.HuidigVeldNummer);
                int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 90;
                var eind = nt.AddMinutes(duur);
                if (eind > laatsteEind) laatsteEind = eind;
            }
            int eindMin = laatsteEind.Hour * 60 + laatsteEind.Minute + 30;
            int totaalMin = eindMin - startMin;
            int gridBreedte = (int)(totaalMin * PixelsPerUur / 60.0);

            // Verplaatsingen indexeren
            var verplaatsVan = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties)
                verplaatsVan[$"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}"] = s;

            // Bouw nieuwe situatie bezetting
            var nieuweBezetting = BouwNieuweBezetting(wedstrijden, suggesties);

            // HTML start
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<style>");
            sb.AppendLine($"body {{ margin:0; padding:15px; font-family:'Segoe UI',Arial,sans-serif; background:{BG}; color:{TXT}; }}");
            sb.AppendLine($".planner-grid {{ position:relative; }}");
            sb.AppendLine($".blok {{ position:absolute; border-radius:3px; overflow:hidden; padding:1px 3px; box-sizing:border-box; cursor:pointer; transition: box-shadow 0.15s, opacity 0.15s; font-size:9px; line-height:1.2; }}");
            sb.AppendLine($".blok.selected {{ box-shadow: 0 0 0 2px {HIGHLIGHT}; z-index:20; }}");
            sb.AppendLine($".blok.ghost {{ opacity:0.3; border:2px dashed {HIGHLIGHT}; }}");
            sb.AppendLine($".blok.linked {{ box-shadow: 0 0 0 2px {HIGHLIGHT}; z-index:15; }}");
            sb.AppendLine($".sectie-titel {{ font-size:14px; font-weight:bold; margin:0 0 8px 0; padding:6px 10px; border-radius:4px; }}");
            sb.AppendLine("</style></head>");
            sb.AppendLine($"<body>");

            // Titel
            sb.AppendLine($"<h2 style='margin:0 0 3px 0;'>Veldplanner — Optimalisatieadvies</h2>");
            sb.AppendLine($"<p style='margin:0 0 10px 0;color:{TXT_DIM};font-size:12px;'>{datum.ToString("dddd d MMMM yyyy", nl)} — Sportpark Spitsbergen — {suggesties.Count} suggestie(s)</p>");

            // Legenda
            sb.AppendLine($"<div style='margin-bottom:10px;font-size:11px;color:{TXT_DIM};'>");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_NORMAAL};border:1px solid {BLK_RAND};vertical-align:middle;'></span> Ongewijzigd &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_VAST};border:1px solid {BLK_VAST_RAND};vertical-align:middle;'></span> Vast &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_OUD};border:1px dashed {BLK_OUD_RAND};vertical-align:middle;'></span> Verplaatst (oud) &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_NIEUW};border:1px solid {BLK_NIEUW_RAND};vertical-align:middle;'></span> Verplaatst (nieuw) &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;border:2px dashed {HIGHLIGHT};vertical-align:middle;opacity:0.3;'></span> Klik-markering");
            sb.AppendLine("</div>");

            // Side-by-side container
            sb.AppendLine("<div style='display:flex;gap:15px;overflow-x:auto;'>");

            // === LINKER PANEEL: HUIDIGE SITUATIE ===
            sb.AppendLine("<div>");
            sb.AppendLine($"<div class='sectie-titel' style='background:{BG_TIJD};'>Huidige situatie</div>");
            TekenGrid(sb, "oud", actieveVelden, wedstrijden, verplaatsVan, null, startMin, gridBreedte, totaalMin);
            sb.AppendLine("</div>");

            // === RECHTER PANEEL: NIEUWE SITUATIE ===
            sb.AppendLine("<div>");
            sb.AppendLine($"<div class='sectie-titel' style='background:#1a2a1a;border:1px solid {BLK_NIEUW_RAND};'>Nieuwe situatie (suggestie)</div>");
            TekenGrid(sb, "nieuw", actieveVelden, nieuweBezetting, null, verplaatsVan, startMin, gridBreedte, totaalMin);
            sb.AppendLine("</div>");

            sb.AppendLine("</div>"); // einde side-by-side

            // Tabellen naast elkaar: chronologisch + per leeftijdscategorie
            sb.AppendLine("<div style='display:flex;gap:20px;flex-wrap:wrap;'>");

            sb.AppendLine("<div style='flex:1;min-width:400px;'>");
            sb.AppendLine($"<h3 style='margin:20px 0 8px 0;'>Chronologisch overzicht</h3>");
            TekenChronoTabel(sb, wedstrijden, suggesties, velden, verplaatsVan);
            sb.AppendLine("</div>");

            sb.AppendLine("<div style='flex:1;min-width:400px;'>");
            sb.AppendLine($"<h3 style='margin:20px 0 8px 0;'>Per leeftijdscategorie</h3>");
            TekenCategorieTabel(sb, nieuweBezetting, suggesties, velden);
            sb.AppendLine("</div>");

            sb.AppendLine("</div>");

            // Samenvatting
            sb.AppendLine($"<p style='margin:10px 0;color:{TXT_DIM};font-size:11px;'>Suggesties: {suggesties.Count} | Van veld 5 verplaatst: {suggesties.Count(s => s.HuidigVeldNummer == 5)} | Gegenereerd door VRC Veldplanner</p>");

            // JavaScript: klik-interactie
            sb.AppendLine("<script>");
            sb.AppendLine(@"
let geselecteerd = null;
document.querySelectorAll('.blok').forEach(blok => {
    blok.addEventListener('click', (e) => {
        e.stopPropagation();
        // Reset alles
        document.querySelectorAll('.blok').forEach(b => { b.classList.remove('selected','ghost','linked'); });

        const wedstrijd = blok.dataset.wedstrijd;
        const paneel = blok.dataset.paneel;

        if (geselecteerd === blok) { geselecteerd = null; return; }
        geselecteerd = blok;
        blok.classList.add('selected');

        // Zoek de corresponderende wedstrijd in het andere paneel
        const anderPaneel = paneel === 'oud' ? 'nieuw' : 'oud';
        document.querySelectorAll(`.blok[data-paneel='${anderPaneel}']`).forEach(b => {
            if (b.dataset.wedstrijd === wedstrijd) {
                b.classList.add('linked');
            }
        });

        // Toon ghost-blok: als oud geklikt, toon waar het naartoe gaat in oud paneel
        // Als nieuw geklikt, toon waar het vandaan kwam in nieuw paneel
        if (blok.dataset.van) {
            // Nieuw blok geklikt - markeer waar het vandaan komt in oud paneel
            document.querySelectorAll(`.blok[data-paneel='oud']`).forEach(b => {
                if (b.dataset.wedstrijd === wedstrijd) b.classList.add('linked');
            });
        }
        if (blok.dataset.naar) {
            // Oud blok geklikt - markeer waar het naartoe gaat in nieuw paneel
            document.querySelectorAll(`.blok[data-paneel='nieuw']`).forEach(b => {
                if (b.dataset.wedstrijd === wedstrijd) b.classList.add('linked');
            });
        }
    });
});
document.addEventListener('click', () => {
    document.querySelectorAll('.blok').forEach(b => b.classList.remove('selected','ghost','linked'));
    geselecteerd = null;
});
");
            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void TekenGrid(StringBuilder sb, string paneel,
            List<VeldInfo> velden, List<BestaandeWedstrijd> bezetting,
            Dictionary<string, OptimalisatieSuggestie>? verplaatsVan,
            Dictionary<string, OptimalisatieSuggestie>? isNieuwePlek,
            int startMin, int gridBreedte, int totaalMin)
        {
            sb.AppendLine($"<div class='planner-grid' style='width:{gridBreedte + VeldHeaderBreedte}px;'>");

            // Tijdbalk
            sb.AppendLine($"<div style='margin-left:{VeldHeaderBreedte}px;height:22px;position:relative;background:{BG_TIJD};border-radius:3px 3px 0 0;'>");
            for (int min = 0; min <= totaalMin; min += 60)
            {
                int uur = (startMin + min) / 60;
                int x = (int)(min * PixelsPerUur / 60.0);
                sb.AppendLine($"<span style='position:absolute;left:{x}px;top:4px;font-size:10px;color:{TXT_DIM};'>{uur:00}:00</span>");
            }
            sb.AppendLine("</div>");

            foreach (var veld in velden)
            {
                sb.AppendLine($"<div style='display:flex;height:{VeldRijHoogte}px;border-bottom:1px solid #21262d;'>");
                sb.AppendLine($"<div style='width:{VeldHeaderBreedte}px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:bold;background:{BG_VELD};'>{veld.VeldNaam}</div>");
                sb.AppendLine($"<div style='flex:1;position:relative;background:{BG};'>");

                // Uurlijnen
                for (int min = 0; min <= totaalMin; min += 60)
                {
                    int x = (int)(min * PixelsPerUur / 60.0);
                    sb.AppendLine($"<div style='position:absolute;left:{x}px;top:0;bottom:0;width:1px;background:#1b1f24;'></div>");
                }

                // Wedstrijden groeperen en tekenen
                var veldWedstrijden = bezetting.Where(w => w.VeldNummer == veld.VeldNummer).OrderBy(w => w.AanvangsTijd).ToList();
                var verwerkt = new HashSet<int>();

                for (int i = 0; i < veldWedstrijden.Count; i++)
                {
                    if (verwerkt.Contains(i)) continue;
                    var w = veldWedstrijden[i];

                    if (w.VeldDeelGebruik >= 1.0m)
                    {
                        verwerkt.Add(i);
                        TekenBlok(sb, paneel, w, veld, 1, VeldRijHoogte - 2, startMin, verplaatsVan, isNieuwePlek);
                    }
                    else
                    {
                        var blok = new List<BestaandeWedstrijd> { w };
                        verwerkt.Add(i);
                        for (int j = i + 1; j < veldWedstrijden.Count; j++)
                        {
                            if (verwerkt.Contains(j)) continue;
                            var a = veldWedstrijden[j];
                            if (a.VeldDeelGebruik >= 1.0m) continue;
                            if (a.AanvangsTijd < blok.Max(b => b.EindTijd) && a.EindTijd > blok.Min(b => b.AanvangsTijd))
                            { blok.Add(a); verwerkt.Add(j); }
                        }
                        int bh = (VeldRijHoogte - 2) / Math.Max(blok.Count, 1);
                        for (int k = 0; k < blok.Count; k++)
                            TekenBlok(sb, paneel, blok[k], veld, 1 + k * bh, bh - 1, startMin, verplaatsVan, isNieuwePlek);
                    }
                }

                sb.AppendLine("</div></div>");
            }
            sb.AppendLine("</div>");
        }

        private static void TekenBlok(StringBuilder sb, string paneel,
            BestaandeWedstrijd w, VeldInfo veld, int top, int hoogte, int startMin,
            Dictionary<string, OptimalisatieSuggestie>? verplaatsVan,
            Dictionary<string, OptimalisatieSuggestie>? isNieuwePlek)
        {
            int wStartMin = w.AanvangsTijd.Hour * 60 + w.AanvangsTijd.Minute;
            int wEindMin = w.EindTijd.Hour * 60 + w.EindTijd.Minute;
            int x = (int)((wStartMin - startMin) * PixelsPerUur / 60.0);
            int breedte = (int)((wEindMin - wStartMin) * PixelsPerUur / 60.0);
            if (breedte < 25) breedte = 25;
            if (x < 0) { breedte += x; x = 0; }

            string lookupKey = $"{veld.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
            string bg = BLK_NORMAAL, rand = BLK_RAND, randStijl = "solid";
            string extraAttr = "";
            string wedstrijdNaam = (w.Wedstrijd?.Trim() ?? "").Replace("'", "&#39;");

            bool isVast = w.Wedstrijd != null && w.Wedstrijd.Contains("VRC 1 ");
            bool isVerplaatst = verplaatsVan != null && verplaatsVan.ContainsKey(lookupKey);
            bool isNieuw = isNieuwePlek != null && w.Bron == "Suggestie";

            if (isVast) { bg = BLK_VAST; rand = BLK_VAST_RAND; }
            else if (isVerplaatst) { bg = BLK_OUD; rand = BLK_OUD_RAND; randStijl = "dashed"; var s = verplaatsVan![lookupKey]; extraAttr = $" data-naar='{s.NieuwVeld} {s.NieuweTijd}'"; }
            else if (isNieuw) { bg = BLK_NIEUW; rand = BLK_NIEUW_RAND; }

            string naam = w.Wedstrijd?.Trim() ?? "";
            int maxLen = hoogte < 18 ? 18 : 30;
            if (naam.Length > maxLen) naam = naam[..maxLen] + "…";

            sb.AppendLine($"<div class='blok' data-paneel='{paneel}' data-wedstrijd='{wedstrijdNaam}'{extraAttr} " +
                $"style='left:{x}px;top:{top}px;width:{breedte - 1}px;height:{hoogte}px;" +
                $"background:{bg};border:1px {randStijl} {rand};font-size:{(hoogte < 18 ? 7 : 9)}px;'>" +
                $"<b>{w.AanvangsTijd:HH:mm}</b> {naam}</div>");
        }

        private static List<BestaandeWedstrijd> BouwNieuweBezetting(
            List<BestaandeWedstrijd> origineel,
            List<OptimalisatieSuggestie> suggesties)
        {
            var resultaat = origineel.ToList();
            foreach (var s in suggesties)
            {
                // Verwijder originele positie
                resultaat.RemoveAll(b =>
                    b.VeldNummer == s.HuidigVeldNummer &&
                    b.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd &&
                    b.Wedstrijd?.Trim() == s.Wedstrijd.Trim());

                // Voeg toe op nieuwe positie
                TimeOnly.TryParse(s.NieuweTijd, out var nieuwStart);
                var orig = origineel.FirstOrDefault(w =>
                    w.VeldNummer == s.HuidigVeldNummer &&
                    w.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd &&
                    w.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 75;

                resultaat.Add(new BestaandeWedstrijd
                {
                    Datum = orig?.Datum ?? default,
                    AanvangsTijd = nieuwStart,
                    EindTijd = nieuwStart.AddMinutes(duur),
                    VeldNummer = s.NieuwVeldNummer,
                    VeldDeelGebruik = orig?.VeldDeelGebruik ?? 1.0m,
                    TeamNaam = orig?.TeamNaam,
                    Wedstrijd = orig?.Wedstrijd,
                    Bron = "Suggestie"
                });
            }
            return resultaat;
        }

        private static void TekenChronoTabel(StringBuilder sb,
            List<BestaandeWedstrijd> wedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            Dictionary<string, OptimalisatieSuggestie> verplaatsVan)
        {
            sb.AppendLine($"<table style='border-collapse:collapse;width:100%;font-size:11px;'>");
            sb.AppendLine($"<tr style='background:{BG_TIJD};'><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Veld</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Tijd</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Status</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Wedstrijd</th></tr>");

            var verplaatsteKeys = new HashSet<string>(suggesties.Select(s => $"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}"));
            var getoond = new HashSet<string>();
            var items = new List<(TimeOnly Tijd, string Veld, string Wedstrijd, string Status, string Kleur)>();

            foreach (var w in wedstrijden.OrderBy(w => w.AanvangsTijd).ThenBy(w => w.VeldNummer))
            {
                string key = $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
                if (!getoond.Add(key)) continue;
                var vn = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";

                if (verplaatsteKeys.Contains(key))
                { var s = verplaatsVan[key]; items.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", $"⟶ {s.NieuwVeld} {s.NieuweTijd}", BLK_OUD_RAND)); }
                else if (w.Wedstrijd?.Contains("VRC 1 ") == true)
                    items.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "🔒 Vast", BLK_VAST_RAND));
                else
                    items.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "", TXT_DIM));
            }
            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var t);
                items.Add((t, s.NieuwVeld, $"★ {s.Wedstrijd}", $"← van {s.HuidigVeld} {s.HuidigeTijd}", BLK_NIEUW_RAND));
            }

            foreach (var item in items.OrderBy(i => i.Tijd).ThenBy(i => i.Veld))
            {
                string rijBg = item.Kleur == BLK_OUD_RAND ? "#1a0d00" : item.Kleur == BLK_NIEUW_RAND ? "#0d1a0d" : "transparent";
                sb.AppendLine($"<tr style='background:{rijBg};border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>{item.Veld}</td><td style='padding:4px 8px;'>{item.Tijd:HH:mm}</td><td style='padding:4px 8px;color:{item.Kleur};'>{item.Status}</td><td style='padding:4px 8px;'>{item.Wedstrijd}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        /// <summary>
        /// Versimpelde email HTML met link naar browser-versie.
        /// </summary>
        private static void TekenCategorieTabel(StringBuilder sb,
            List<BestaandeWedstrijd> nieuweBezetting,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden)
        {
            // Sorteer op leeftijdsnummer: JO7, JO8, JO9, JO10, JO11, MO11, JO12, MO12, JO13, MO13, ...
            // Na JO19: MO20, JO23, G, VR, Senioren
            int LeeftijdVolgorde(string cat)
            {
                // Haal nummer uit categorie
                var numStr = new string(cat.Where(char.IsDigit).ToArray());
                int num = numStr.Length > 0 ? int.Parse(numStr) : 99;
                int basis = num * 10; // JO13 = 130, MO13 = 131
                if (cat.StartsWith("MO")) basis += 1;
                else if (cat == "G") basis = 500;
                else if (cat == "VR") basis = 600;
                else if (cat == "O23") basis = 235;
                else if (cat == "Senioren") basis = 700;
                else if (cat == "Overig") basis = 999;
                return basis;
            }

            string BepaalCategorie(string? wedstrijd, string? teamNaam)
            {
                var naam = teamNaam?.Trim() ?? wedstrijd?.Trim() ?? "";
                if (naam.Contains(" G") && !naam.Contains("GV")) return "G";
                if (naam.Contains("VR")) return "VR";
                if (naam.Contains("O23")) return "O23";
                if (naam.Contains("MO20")) return "MO20";
                if (naam.Contains("MO19")) return "MO19";
                if (naam.Contains("MO17")) return "MO17";
                if (naam.Contains("MO15")) return "MO15";
                if (naam.Contains("MO13")) return "MO13";
                if (naam.Contains("MO10")) return "MO10";
                // JO-categorieën van hoog naar laag checken om JO19 niet als JO1 te matchen
                for (int i = 23; i >= 7; i--)
                    if (naam.Contains($"JO{i}")) return $"JO{i}";
                if (System.Text.RegularExpressions.Regex.IsMatch(naam, @"VRC \d"))
                    return "Senioren";
                return "Overig";
            }

            var verplaatsteKeys = new HashSet<string>(suggesties.Select(s => s.Wedstrijd.Trim()));

            var perCategorie = nieuweBezetting
                .GroupBy(w => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}")
                .Select(g => g.First())
                .Select(w => new {
                    Wedstrijd = w,
                    Categorie = BepaalCategorie(w.Wedstrijd, w.TeamNaam),
                    VeldNaam = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}",
                    IsVerplaatst = verplaatsteKeys.Contains(w.Wedstrijd?.Trim() ?? "")
                })
                .OrderBy(x => LeeftijdVolgorde(x.Categorie))
                .ThenBy(x => x.Wedstrijd.AanvangsTijd)
                .ToList();

            sb.AppendLine($"<table style='border-collapse:collapse;width:100%;font-size:11px;'>");
            sb.AppendLine($"<tr style='background:{BG_TIJD};'><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Veld</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Tijd</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Status</th><th style='padding:5px 8px;text-align:left;color:{TXT_DIM};'>Wedstrijd</th></tr>");

            string vorigeCat = "";
            foreach (var item in perCategorie)
            {
                bool nieuweCat = item.Categorie != vorigeCat;
                vorigeCat = item.Categorie;

                string rijBg = item.IsVerplaatst ? "#0d1a0d" : "transparent";
                string kleur = item.IsVerplaatst ? BLK_NIEUW_RAND : TXT;
                string borderTop = nieuweCat ? $"border-top:2px solid {BG_TIJD};" : "";

                sb.AppendLine($"<tr style='background:{rijBg};{borderTop}border-bottom:1px solid #21262d;'>");
                sb.AppendLine($"<td style='padding:4px 8px;color:{kleur};'>{item.VeldNaam}</td>");
                sb.AppendLine($"<td style='padding:4px 8px;color:{kleur};'>{item.Wedstrijd.AanvangsTijd:HH:mm}</td>");
                string status = item.IsVerplaatst ? "★" : "";
                sb.AppendLine($"<td style='padding:4px 8px;color:{BLK_NIEUW_RAND};'>{status}</td>");
                string naam = item.Wedstrijd.Wedstrijd?.Trim() ?? "";
                sb.AppendLine($"<td style='padding:4px 8px;color:{kleur};'>{naam}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        public static string GenereerEmailHtml(
            DateOnly datum,
            List<BestaandeWedstrijd> alleWedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            string doel,
            string browserUrl)
        {
            var sb = new StringBuilder();
            var nl = new System.Globalization.CultureInfo("nl-NL");

            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'></head>");
            sb.AppendLine($"<body style='margin:0;padding:20px;font-family:Arial,sans-serif;background:#ffffff;color:#333;'>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;'><a href='{browserUrl}' style='color:#0969da;font-size:13px;'>&#128279; Bekijk in browser (meer functies)</a></p>");
            sb.AppendLine($"<h2 style='margin:0 0 5px 0;'>Veldplanner — Optimalisatieadvies</h2>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;color:#666;'>{datum.ToString("dddd d MMMM yyyy", nl)} — {suggesties.Count} suggestie(s)</p>");

            if (suggesties.Count == 0)
            { sb.AppendLine("<p style='color:#666;'>Geen suggesties — planning is al optimaal.</p>"); }

            sb.AppendLine("<table style='border-collapse:collapse;width:100%;max-width:700px;font-size:12px;border:1px solid #e5e7eb;'>");
            sb.AppendLine("<tr style='background:#f9fafb;'><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Tijd</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Veld</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Wedstrijd</th><th style='padding:6px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Status</th></tr>");

            var verplaatsteKeys = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties) verplaatsteKeys[$"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}"] = s;

            var getoond = new HashSet<string>();
            var rijen = new List<(TimeOnly T, string V, string W, string S, string Bg, string Fg)>();

            foreach (var w in alleWedstrijden.GroupBy(w => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}").Select(g => g.First()).OrderBy(w => w.AanvangsTijd))
            {
                string key = $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
                var vn = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? "";
                if (verplaatsteKeys.TryGetValue(key, out var s))
                    rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", $"⟶ {s.NieuwVeld} {s.NieuweTijd}", "#fef3c7", "#92400e"));
                else if (w.Wedstrijd?.Contains("VRC 1 ") == true)
                    rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "🔒 Vast", "#dbeafe", "#1e40af"));
                else rijen.Add((w.AanvangsTijd, vn, w.Wedstrijd?.Trim() ?? "", "", "#fff", "#666"));
            }
            foreach (var s in suggesties) { TimeOnly.TryParse(s.NieuweTijd, out var t); rijen.Add((t, s.NieuwVeld, $"★ {s.Wedstrijd}", $"← {s.HuidigVeld} {s.HuidigeTijd}", "#fef3c7", "#92400e")); }

            foreach (var r in rijen.OrderBy(r => r.T).ThenBy(r => r.V))
                sb.AppendLine($"<tr style='background:{r.Bg};border-bottom:1px solid #e5e7eb;'><td style='padding:5px 6px;'>{r.T:HH:mm}</td><td style='padding:5px 6px;'>{r.V}</td><td style='padding:5px 6px;'>{r.W}</td><td style='padding:5px 6px;color:{r.Fg};'>{r.S}</td></tr>");

            sb.AppendLine("</table>");
            sb.AppendLine("<p style='margin:15px 0 0 0;font-size:11px;color:#999;'>VRC Veldplanner</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
    }
}
