using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// Genereert een visuele HTML veldplanner in Sportlink-stijl.
    /// Output is email-compatibel (tabel-layout, inline CSS).
    /// </summary>
    public static class PlannerHtmlGenerator
    {
        // Tijdlijn instellingen
        private const int StartUur = 8;
        private const int EindUur = 20;
        private const int PixelsPerUur = 120;
        private const int VeldRijHoogte = 90;

        // Kleuren (Sportlink-stijl)
        private const string KleurAchtergrond = "#0d1117";
        private const string KleurVeldHeader = "#161b22";
        private const string KleurTijdbalk = "#21262d";
        private const string KleurTekst = "#e6edf3";
        private const string KleurTekstDim = "#8b949e";
        private const string KleurWedstrijdNormaal = "#2d333b";
        private const string KleurWedstrijdRand = "#444c56";
        private const string KleurWedstrijdVast = "#1f3a5f";
        private const string KleurVastRand = "#388bfd";
        private const string KleurSuggestieNieuw = "#4a3000";
        private const string KleurSuggestieRand = "#d29922";
        private const string KleurSuggestieOud = "#1a0d00";
        private const string KleurSuggestieOudRand = "#6e4600";
        private const string KleurVrijgekomen = "#0d2818";
        private const string KleurVrijgekomenRand = "#2ea043";

        public static string GenereerHtml(
            DateOnly datum,
            List<BestaandeWedstrijd> alleWedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            string doel)
        {
            var sb = new StringBuilder();
            var nl = new System.Globalization.CultureInfo("nl-NL");

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='utf-8'>");
            sb.AppendLine("<style>");
            sb.AppendLine(".wedstrijd-blok { transition: box-shadow 0.15s, transform 0.15s; cursor: pointer; }");
            sb.AppendLine(".wedstrijd-blok.highlight { box-shadow: 0 0 0 2px #58a6ff !important; transform: scale(1.02); z-index: 10; }");
            sb.AppendLine(".chrono-rij { transition: background 0.15s; cursor: pointer; }");
            sb.AppendLine($".chrono-rij.highlight {{ background: #1f3a5f !important; }}");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body style='margin:0;padding:20px;font-family:Segoe UI,Arial,sans-serif;background:" + KleurAchtergrond + ";color:" + KleurTekst + ";'>");

            // Titel
            sb.AppendLine($"<h2 style='margin:0 0 5px 0;color:{KleurTekst};'>Veldplanner — Optimalisatieadvies</h2>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;color:{KleurTekstDim};'>{datum.ToString("dddd d MMMM yyyy", nl)} — Sportpark Spitsbergen — Doel: {doel}</p>");

            // Legenda
            sb.AppendLine("<table style='margin-bottom:15px;border-spacing:8px;'><tr>");
            sb.AppendLine($"<td><span style='display:inline-block;width:14px;height:14px;background:{KleurWedstrijdNormaal};border:2px solid {KleurWedstrijdRand};vertical-align:middle;'></span></td><td style='color:{KleurTekstDim};font-size:12px;'>Ongewijzigd</td>");
            sb.AppendLine($"<td><span style='display:inline-block;width:14px;height:14px;background:{KleurWedstrijdVast};border:2px solid {KleurVastRand};vertical-align:middle;'></span></td><td style='color:{KleurTekstDim};font-size:12px;'>Vast (niet verplaatsbaar)</td>");
            sb.AppendLine($"<td><span style='display:inline-block;width:14px;height:14px;background:{KleurSuggestieNieuw};border:2px solid {KleurSuggestieRand};vertical-align:middle;'></span></td><td style='color:{KleurTekstDim};font-size:12px;'>Suggestie: nieuwe positie</td>");
            sb.AppendLine($"<td><span style='display:inline-block;width:14px;height:14px;background:{KleurSuggestieOud};border:2px dashed {KleurSuggestieOudRand};vertical-align:middle;'></span></td><td style='color:{KleurTekstDim};font-size:12px;'>Suggestie: oude positie</td>");
            sb.AppendLine("</tr></table>");

            // Grid container
            int totaleBreedte = (EindUur - StartUur) * PixelsPerUur;
            sb.AppendLine($"<div style='position:relative;width:{totaleBreedte + 80}px;'>");

            // Tijdbalk
            sb.AppendLine($"<div style='margin-left:80px;height:30px;position:relative;background:{KleurTijdbalk};border-radius:4px 4px 0 0;'>");
            for (int uur = StartUur; uur <= EindUur; uur++)
            {
                int x = (uur - StartUur) * PixelsPerUur;
                sb.AppendLine($"<span style='position:absolute;left:{x}px;top:6px;font-size:11px;color:{KleurTekstDim};'>{uur:00}:00</span>");
            }
            sb.AppendLine("</div>");

            // Verplaatsingen indexeren voor snelle lookup
            var verplaatsVan = new Dictionary<string, OptimalisatieSuggestie>();
            var verplaatsNaar = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties)
            {
                verplaatsVan[$"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}"] = s;
                verplaatsNaar[$"{s.NieuwVeldNummer}_{s.NieuweTijd}_{s.Wedstrijd}"] = s;
            }

            // Velden tekenen
            var actieveVelden = velden.Where(v => v.VeldNummer <= 5).OrderBy(v => v.VeldNummer).ToList();
            foreach (var veld in actieveVelden)
            {
                sb.AppendLine($"<div style='display:flex;height:{VeldRijHoogte}px;border-bottom:1px solid #21262d;'>");

                // Veldnaam
                sb.AppendLine($"<div style='width:80px;display:flex;align-items:center;padding-left:8px;font-size:13px;font-weight:bold;color:{KleurTekst};background:{KleurVeldHeader};'>{veld.VeldNaam}</div>");

                // Tijdlijn met wedstrijden
                sb.AppendLine($"<div style='flex:1;position:relative;background:{KleurAchtergrond};'>");

                // Uurlijnen
                for (int uur = StartUur; uur <= EindUur; uur++)
                {
                    int x = (uur - StartUur) * PixelsPerUur;
                    sb.AppendLine($"<div style='position:absolute;left:{x}px;top:0;bottom:0;width:1px;background:#21262d;'></div>");
                }

                // Wedstrijden op dit veld — dedup en sorteer
                var veldWedstrijden = alleWedstrijden
                    .Where(w => w.VeldNummer == veld.VeldNummer)
                    .GroupBy(w => $"{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}")
                    .Select(g => g.First())
                    .OrderBy(w => w.AanvangsTijd)
                    .ToList();

                // Groepeer overlappende deelveld-wedstrijden in tijdblokken
                // Elk tijdblok bevat wedstrijden die gelijktijdig op deelvelden spelen
                var verwerkt = new HashSet<int>();
                for (int i = 0; i < veldWedstrijden.Count; i++)
                {
                    if (verwerkt.Contains(i)) continue;
                    var w = veldWedstrijden[i];

                    if (w.VeldDeelGebruik >= 1.0m)
                    {
                        // Heel-veld wedstrijd: gewoon tekenen over volledige hoogte
                        verwerkt.Add(i);
                        TekenWedstrijdBlok(sb, w, veld, 2, VeldRijHoogte - 4, verplaatsVan);
                    }
                    else
                    {
                        // Deelveld: verzamel alle overlappende deelveld-wedstrijden in dit blok
                        var blok = new List<BestaandeWedstrijd> { w };
                        verwerkt.Add(i);
                        for (int j = i + 1; j < veldWedstrijden.Count; j++)
                        {
                            if (verwerkt.Contains(j)) continue;
                            var andere = veldWedstrijden[j];
                            if (andere.VeldDeelGebruik >= 1.0m) continue;
                            // Overlapt deze wedstrijd met het blok?
                            if (andere.AanvangsTijd < blok.Max(b => b.EindTijd) && andere.EindTijd > blok.Min(b => b.AanvangsTijd))
                            {
                                blok.Add(andere);
                                verwerkt.Add(j);
                            }
                        }

                        // Teken alle wedstrijden in dit blok gestapeld
                        int aantalInBlok = blok.Count;
                        int blokHoogte = (VeldRijHoogte - 4) / Math.Max(aantalInBlok, 1);
                        if (blokHoogte < 16) blokHoogte = 16;

                        for (int k = 0; k < blok.Count; k++)
                        {
                            int topPos = 2 + k * blokHoogte;
                            TekenWedstrijdBlok(sb, blok[k], veld, topPos, blokHoogte - 2, verplaatsVan);
                        }
                    }
                }

                // Teken suggesties die naar dit veld verplaatst worden (nieuwe posities)
                foreach (var s in suggesties.Where(s => s.NieuwVeldNummer == veld.VeldNummer))
                {
                    TimeOnly.TryParse(s.NieuweTijd, out var nieuwStart);
                    // Bereken eindtijd uit originele wedstrijd
                    var origWedstrijd = alleWedstrijden.FirstOrDefault(w =>
                        w.VeldNummer == s.HuidigVeldNummer &&
                        w.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd &&
                        w.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                    int duurMin = origWedstrijd != null
                        ? (int)(origWedstrijd.EindTijd - origWedstrijd.AanvangsTijd).TotalMinutes
                        : 75;

                    int x = TijdNaarPixels(nieuwStart);
                    int breedte = (int)(duurMin * PixelsPerUur / 60.0);
                    if (breedte < 20) breedte = 20;

                    string naam = s.Wedstrijd;
                    if (naam.Length > 28) naam = naam[..28] + "…";

                    string sugDataId = MaakDataId(s.NieuwVeldNummer, nieuwStart, s.Wedstrijd);

                    sb.AppendLine($"<div class='wedstrijd-blok' data-id='{sugDataId}-nieuw' style='position:absolute;left:{x}px;top:2px;width:{breedte - 4}px;height:{VeldRijHoogte - 6}px;" +
                        $"background:{KleurSuggestieNieuw};border:2px solid {KleurSuggestieRand};border-radius:4px;" +
                        $"overflow:hidden;padding:2px 4px;font-size:10px;line-height:1.3;box-sizing:border-box;'>");
                    sb.AppendLine($"<div style='font-weight:bold;color:{KleurSuggestieRand};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;'>★ {nieuwStart:HH:mm} {naam}</div>");
                    sb.AppendLine($"<div style='color:{KleurTekstDim};font-size:9px;'>← van {s.HuidigVeld} {s.HuidigeTijd}</div>");
                    sb.AppendLine("</div>");
                }

                sb.AppendLine("</div>"); // einde tijdlijn
                sb.AppendLine("</div>"); // einde veld-rij
            }

            sb.AppendLine("</div>"); // einde grid container

            // Chronologische lijst met suggesties
            sb.AppendLine("<h3 style='margin:25px 0 10px 0;color:" + KleurTekst + ";'>Chronologisch overzicht</h3>");
            sb.AppendLine("<table style='border-collapse:collapse;width:100%;max-width:900px;font-size:12px;'>");
            sb.AppendLine($"<tr style='background:{KleurTijdbalk};'>");
            sb.AppendLine($"<th style='padding:6px 10px;text-align:left;color:{KleurTekstDim};'>Tijd</th>");
            sb.AppendLine($"<th style='padding:6px 10px;text-align:left;color:{KleurTekstDim};'>Veld</th>");
            sb.AppendLine($"<th style='padding:6px 10px;text-align:left;color:{KleurTekstDim};'>Wedstrijd</th>");
            sb.AppendLine($"<th style='padding:6px 10px;text-align:left;color:{KleurTekstDim};'>Status</th></tr>");

            // Alle wedstrijden + suggesties samenvoegen en sorteren
            var chronoItems = new List<(TimeOnly Tijd, int Veld, string VeldNaam, string Wedstrijd, string Status, string Kleur, string DataId)>();

            var verplaatsteWedstrijden = new HashSet<string>();
            foreach (var s in suggesties)
                verplaatsteWedstrijden.Add($"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}");

            var getoond = new HashSet<string>();
            foreach (var w in alleWedstrijden.OrderBy(w => w.AanvangsTijd).ThenBy(w => w.VeldNummer))
            {
                string key = $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
                if (!getoond.Add(key)) continue;

                var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";
                string dataId = MaakDataId(w.VeldNummer, w.AanvangsTijd, w.Wedstrijd);
                string status;
                string kleur;

                if (verplaatsteWedstrijden.Contains(key))
                {
                    var s = verplaatsVan[key];
                    status = $"⟶ VERPLAATSEN naar {s.NieuwVeld} {s.NieuweTijd} — {s.Reden}";
                    kleur = KleurSuggestieRand;
                }
                else if (w.Wedstrijd != null && w.Wedstrijd.Contains("VRC 1 "))
                {
                    status = "🔒 Vast";
                    kleur = KleurVastRand;
                }
                else
                {
                    status = "Ongewijzigd";
                    kleur = KleurTekstDim;
                }

                chronoItems.Add((w.AanvangsTijd, w.VeldNummer, veldNaam, w.Wedstrijd?.Trim() ?? "", status, kleur, dataId));
            }

            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var tijd);
                string dataId = MaakDataId(s.NieuwVeldNummer, tijd, s.Wedstrijd) + "-nieuw";
                chronoItems.Add((tijd, s.NieuwVeldNummer, s.NieuwVeld,
                    $"★ {s.Wedstrijd}", $"← NIEUW (van {s.HuidigVeld} {s.HuidigeTijd})", KleurSuggestieRand, dataId));
            }

            foreach (var item in chronoItems.OrderBy(i => i.Tijd).ThenBy(i => i.Veld))
            {
                string rijKleur = item.Kleur == KleurSuggestieRand ? KleurSuggestieOud : "transparent";
                sb.AppendLine($"<tr class='chrono-rij' data-id='{item.DataId}' style='background:{rijKleur};border-bottom:1px solid #21262d;'>");
                sb.AppendLine($"<td style='padding:5px 10px;color:{KleurTekst};'>{item.Tijd:HH:mm}</td>");
                sb.AppendLine($"<td style='padding:5px 10px;color:{KleurTekst};'>{item.VeldNaam}</td>");
                sb.AppendLine($"<td style='padding:5px 10px;color:{KleurTekst};'>{item.Wedstrijd}</td>");
                sb.AppendLine($"<td style='padding:5px 10px;color:{item.Kleur};'>{item.Status}</td></tr>");
            }

            sb.AppendLine("</table>");

            // Samenvatting
            sb.AppendLine($"<p style='margin:15px 0;color:{KleurTekstDim};font-size:12px;'>Aantal suggesties: {suggesties.Count} | Van veld 5 verplaatst: {suggesties.Count(s => s.HuidigVeldNummer == 5)}</p>");
            sb.AppendLine($"<p style='color:{KleurTekstDim};font-size:11px;'>Gegenereerd door VRC Veldplanner</p>");

            // JavaScript voor hover-interactie tussen grid en lijst
            sb.AppendLine("<script>");
            sb.AppendLine(@"
document.querySelectorAll('.wedstrijd-blok').forEach(blok => {
    blok.addEventListener('mouseenter', () => {
        const id = blok.dataset.id;
        document.querySelectorAll('.chrono-rij').forEach(rij => {
            if (rij.dataset.id === id) {
                rij.classList.add('highlight');
                rij.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            }
        });
        blok.classList.add('highlight');
    });
    blok.addEventListener('mouseleave', () => {
        document.querySelectorAll('.highlight').forEach(el => el.classList.remove('highlight'));
    });
});
document.querySelectorAll('.chrono-rij').forEach(rij => {
    rij.addEventListener('mouseenter', () => {
        const id = rij.dataset.id;
        document.querySelectorAll('.wedstrijd-blok').forEach(blok => {
            if (blok.dataset.id === id) {
                blok.classList.add('highlight');
                blok.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            }
        });
        rij.classList.add('highlight');
    });
    rij.addEventListener('mouseleave', () => {
        document.querySelectorAll('.highlight').forEach(el => el.classList.remove('highlight'));
    });
});
");
            sb.AppendLine("</script>");

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void TekenWedstrijdBlok(
            StringBuilder sb, BestaandeWedstrijd w, VeldInfo veld,
            int top, int hoogte,
            Dictionary<string, OptimalisatieSuggestie> verplaatsVan)
        {
            int x = TijdNaarPixels(w.AanvangsTijd);
            int breedte = TijdNaarPixels(w.EindTijd) - x;
            if (breedte < 30) breedte = 30;

            string lookupKey = $"{veld.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
            string achtergrond = KleurWedstrijdNormaal;
            string rand = KleurWedstrijdRand;
            string randStijl = "solid";
            string extraTekst = "";

            if (w.Wedstrijd != null && w.Wedstrijd.Contains("VRC 1 "))
            {
                achtergrond = KleurWedstrijdVast;
                rand = KleurVastRand;
            }
            else if (verplaatsVan.ContainsKey(lookupKey))
            {
                achtergrond = KleurSuggestieOud;
                rand = KleurSuggestieOudRand;
                randStijl = "dashed";
                var s = verplaatsVan[lookupKey];
                extraTekst = $" → {s.NieuwVeld} {s.NieuweTijd}";
            }

            string naam = w.Wedstrijd?.Trim() ?? "";
            int maxLen = hoogte < 20 ? 20 : 35;
            string volledigeNaam = naam;
            if (naam.Length > maxLen) naam = naam[..maxLen] + "…";

            string dataId = MaakDataId(veld.VeldNummer, w.AanvangsTijd, w.Wedstrijd);

            sb.AppendLine($"<div class='wedstrijd-blok' data-id='{dataId}' style='position:absolute;left:{x}px;top:{top}px;width:{breedte - 2}px;height:{hoogte}px;" +
                $"background:{achtergrond};border:1px {randStijl} {rand};border-radius:3px;" +
                $"overflow:hidden;padding:1px 4px;font-size:{(hoogte < 20 ? 8 : 10)}px;line-height:1.3;box-sizing:border-box;'>");
            sb.AppendLine($"<span style='font-weight:bold;color:{KleurTekst};white-space:nowrap;'>{w.AanvangsTijd:HH:mm}</span> " +
                $"<span style='color:{KleurTekst};white-space:nowrap;'>{naam}</span>");
            if (!string.IsNullOrEmpty(extraTekst))
                sb.Append($" <span style='color:{KleurSuggestieRand};font-size:8px;'>{extraTekst}</span>");
            sb.AppendLine("</div>");
        }

        /// <summary>
        /// Genereert een versimpelde email-compatibele HTML met een link naar de browser-versie.
        /// Alleen inline CSS, geen JS, geen complex grid — werkt in alle email-clients.
        /// </summary>
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
            sb.AppendLine($"<body style='margin:0;padding:20px;font-family:Arial,sans-serif;background:#ffffff;color:#333333;'>");

            // Link naar browser-versie
            sb.AppendLine($"<p style='margin:0 0 15px 0;'><a href='{browserUrl}' style='color:#0969da;font-size:13px;'>&#128279; Bekijk in browser (meer functies)</a></p>");

            // Titel
            sb.AppendLine($"<h2 style='margin:0 0 5px 0;color:#333;'>Veldplanner — Optimalisatieadvies</h2>");
            sb.AppendLine($"<p style='margin:0 0 15px 0;color:#666;'>{datum.ToString("dddd d MMMM yyyy", nl)} — Sportpark Spitsbergen — Doel: {doel}</p>");

            // Legenda
            sb.AppendLine("<p style='font-size:12px;color:#666;'>");
            sb.AppendLine("<span style='display:inline-block;width:12px;height:12px;background:#f0f0f0;border:1px solid #ccc;vertical-align:middle;'></span> Ongewijzigd &nbsp;");
            sb.AppendLine("<span style='display:inline-block;width:12px;height:12px;background:#dbeafe;border:1px solid #3b82f6;vertical-align:middle;'></span> Vast &nbsp;");
            sb.AppendLine("<span style='display:inline-block;width:12px;height:12px;background:#fef3c7;border:1px solid #d97706;vertical-align:middle;'></span> Verplaatsing &nbsp;");
            sb.AppendLine("</p>");

            if (suggesties.Count > 0)
            {
                sb.AppendLine($"<p style='margin:10px 0;font-weight:bold;color:#d97706;'>&#9733; {suggesties.Count} suggestie(s) voor optimalisatie</p>");
            }
            else
            {
                sb.AppendLine("<p style='margin:10px 0;color:#666;'>Geen optimalisatiesuggesties — de planning is al optimaal.</p>");
            }

            // Chronologische tabel (email-compatibel)
            sb.AppendLine("<table style='border-collapse:collapse;width:100%;max-width:700px;font-size:12px;border:1px solid #e5e7eb;'>");
            sb.AppendLine("<tr style='background:#f9fafb;'>");
            sb.AppendLine("<th style='padding:8px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Tijd</th>");
            sb.AppendLine("<th style='padding:8px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Veld</th>");
            sb.AppendLine("<th style='padding:8px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Wedstrijd</th>");
            sb.AppendLine("<th style='padding:8px;text-align:left;border-bottom:2px solid #e5e7eb;color:#666;'>Status</th></tr>");

            var verplaatsteKeys = new HashSet<string>();
            var verplaatsMap = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties)
            {
                var k = $"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}";
                verplaatsteKeys.Add(k);
                verplaatsMap[k] = s;
            }

            var getoond = new HashSet<string>();
            var rijen = new List<(TimeOnly Tijd, int Veld, string VeldNaam, string Wedstrijd, string Status, string RijKleur, string TekstKleur)>();

            foreach (var w in alleWedstrijden.OrderBy(w => w.AanvangsTijd).ThenBy(w => w.VeldNummer))
            {
                string key = $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
                if (!getoond.Add(key)) continue;
                var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";

                if (verplaatsteKeys.Contains(key))
                {
                    var s = verplaatsMap[key];
                    rijen.Add((w.AanvangsTijd, w.VeldNummer, veldNaam, w.Wedstrijd?.Trim() ?? "",
                        $"⟶ {s.NieuwVeld} {s.NieuweTijd}", "#fef3c7", "#92400e"));
                }
                else if (w.Wedstrijd != null && w.Wedstrijd.Contains("VRC 1 "))
                {
                    rijen.Add((w.AanvangsTijd, w.VeldNummer, veldNaam, w.Wedstrijd?.Trim() ?? "",
                        "🔒 Vast", "#dbeafe", "#1e40af"));
                }
                else
                {
                    rijen.Add((w.AanvangsTijd, w.VeldNummer, veldNaam, w.Wedstrijd?.Trim() ?? "",
                        "", "#ffffff", "#666666"));
                }
            }

            foreach (var s in suggesties)
            {
                TimeOnly.TryParse(s.NieuweTijd, out var tijd);
                rijen.Add((tijd, s.NieuwVeldNummer, s.NieuwVeld,
                    $"★ {s.Wedstrijd}", $"← van {s.HuidigVeld} {s.HuidigeTijd}", "#fef3c7", "#92400e"));
            }

            foreach (var r in rijen.OrderBy(r => r.Tijd).ThenBy(r => r.Veld))
            {
                sb.AppendLine($"<tr style='background:{r.RijKleur};border-bottom:1px solid #e5e7eb;'>");
                sb.AppendLine($"<td style='padding:6px 8px;'>{r.Tijd:HH:mm}</td>");
                sb.AppendLine($"<td style='padding:6px 8px;'>{r.VeldNaam}</td>");
                sb.AppendLine($"<td style='padding:6px 8px;'>{r.Wedstrijd}</td>");
                sb.AppendLine($"<td style='padding:6px 8px;color:{r.TekstKleur};'>{r.Status}</td></tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine($"<p style='margin:15px 0 0 0;font-size:11px;color:#999;'>Gegenereerd door VRC Veldplanner</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string MaakDataId(int veldNummer, TimeOnly tijd, string? wedstrijd)
        {
            var kort = (wedstrijd ?? "").Trim().Replace(" ", "").Replace("-", "").Replace("'", "");
            if (kort.Length > 20) kort = kort[..20];
            return $"v{veldNummer}-{tijd:HHmm}-{kort}".ToLowerInvariant();
        }

        private static int TijdNaarPixels(TimeOnly tijd)
        {
            double uren = tijd.Hour + tijd.Minute / 60.0 - StartUur;
            return (int)(uren * PixelsPerUur);
        }
    }
}
