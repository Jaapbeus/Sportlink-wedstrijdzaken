using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;


namespace Planner.Shared
{
    public static partial class PlannerHtmlGenerator
    {
        private static void BouwStyleBlock(StringBuilder sb)
        {
            sb.AppendLine("<style>");
            sb.AppendLine($"body {{ margin:0; padding:15px; font-family:'Segoe UI',Arial,sans-serif; background:{BG}; color:{TXT}; }}");
            sb.AppendLine($".planner-grid {{ position:relative; }}");
            sb.AppendLine($".blok {{ position:absolute; border-radius:3px; overflow:hidden; padding:1px 3px; box-sizing:border-box; cursor:pointer; transition: box-shadow 0.15s, opacity 0.15s; font-size:9px; line-height:1.2; }}");
            sb.AppendLine($".blok.selected {{ box-shadow: 0 0 0 2px {HIGHLIGHT}; z-index:20; }}");
            sb.AppendLine($".blok.ghost {{ opacity:0.3; border:2px dashed {HIGHLIGHT}; }}");
            sb.AppendLine($".blok.linked {{ box-shadow: 0 0 0 2px {HIGHLIGHT}; z-index:15; }}");
            sb.AppendLine($".sectie-titel {{ font-size:14px; font-weight:bold; margin:0 0 8px 0; padding:6px 10px; border-radius:4px; }}");
            sb.AppendLine("</style></head>");
        }

        private static void BouwLegenda(StringBuilder sb)
        {
            sb.AppendLine($"<div style='margin-bottom:10px;font-size:11px;color:{TXT_DIM};'>");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_NORMAAL};border:1px solid {BLK_RAND};vertical-align:middle;'></span> Ongewijzigd &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_VAST};border:1px solid {BLK_VAST_RAND};vertical-align:middle;'></span> Vast &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_OUD};border:1px dashed {BLK_OUD_RAND};vertical-align:middle;'></span> Verplaatst (oud) &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;background:{BLK_NIEUW};border:1px solid {BLK_NIEUW_RAND};vertical-align:middle;'></span> Verplaatst (nieuw) &nbsp;");
            sb.AppendLine($"<span style='display:inline-block;width:12px;height:12px;border:2px dashed {HIGHLIGHT};vertical-align:middle;opacity:0.3;'></span> Klik-markering");
            sb.AppendLine("</div>");
        }

        private static (int eindMin, int totaalMin, int gridBreedte) BerekenTijdlijn(
            int startMin, List<BestaandeWedstrijd> wedstrijden, List<OptimalisatieSuggestie> suggesties)
        {
            var laatsteEind = wedstrijden.Any() ? wedstrijden.Max(w => w.EindTijd) : new TimeOnly(16, 0);
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
            return (eindMin, totaalMin, gridBreedte);
        }

        private static string BouwKlikScript() => @"
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
";

        private static void TekenGrid(StringBuilder sb, string paneel,
            List<VeldInfo> velden, List<BestaandeWedstrijd> bezetting,
            Dictionary<string, OptimalisatieSuggestie>? verplaatsVan,
            Dictionary<string, OptimalisatieSuggestie>? isNieuwePlek,
            int startMin, int gridBreedte, int totaalMin,
            HtmlInstellingen instellingen)
        {
            sb.AppendLine($"<div class='planner-grid' style='width:{gridBreedte + VeldHeaderBreedte}px;'>");

            TekenTijdbalk(sb, totaalMin, startMin);

            foreach (var veld in velden)
            {
                sb.AppendLine($"<div style='display:flex;height:{VeldRijHoogte}px;border-bottom:1px solid #21262d;'>");
                sb.AppendLine($"<div style='width:{VeldHeaderBreedte}px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:bold;background:{BG_VELD};'>{EncodeText(veld.VeldNaam)}</div>");
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
                        TekenBlok(sb, paneel, w, veld, 1, VeldRijHoogte - 2, startMin, verplaatsVan, isNieuwePlek, instellingen);
                    }
                    else
                    {
                        // Deelveld: positioneer op basis van veldsubpositie (A/B/A1/A2/B1/B2)
                        verwerkt.Add(i);
                        var sub = (w.VeldSubpositie ?? "").Trim();
                        int topPos; int hoogte;
                        BerekenSubpositie(sub, w.VeldDeelGebruik, out topPos, out hoogte);
                        TekenBlok(sb, paneel, w, veld, topPos, hoogte, startMin, verplaatsVan, isNieuwePlek, instellingen);

                        // Zoek overige deelveld-wedstrijden die overlappen
                        for (int j = i + 1; j < veldWedstrijden.Count; j++)
                        {
                            if (verwerkt.Contains(j)) continue;
                            var a = veldWedstrijden[j];
                            if (a.VeldDeelGebruik >= 1.0m) continue;
                            if (a.AanvangsTijd < w.EindTijd && a.EindTijd > w.AanvangsTijd)
                            {
                                verwerkt.Add(j);
                                var subA = (a.VeldSubpositie ?? "").Trim();
                                BerekenSubpositie(subA, a.VeldDeelGebruik, out int topA, out int hoogteA);
                                TekenBlok(sb, paneel, a, veld, topA, hoogteA, startMin, verplaatsVan, isNieuwePlek, instellingen);
                            }
                        }
                    }
                }

                TekenBufferLabels(sb, veldWedstrijden, startMin);

                sb.AppendLine("</div></div>");
            }
            sb.AppendLine("</div>");
        }

        private static void TekenTijdbalk(StringBuilder sb, int totaalMin, int startMin)
        {
            sb.AppendLine($"<div style='margin-left:{VeldHeaderBreedte}px;height:22px;position:relative;background:{BG_TIJD};border-radius:3px 3px 0 0;'>");
            for (int min = 0; min <= totaalMin; min += 60)
            {
                int uur = (startMin + min) / 60;
                int x = (int)(min * PixelsPerUur / 60.0);
                sb.AppendLine($"<span style='position:absolute;left:{x}px;top:4px;font-size:10px;color:{TXT_DIM};'>{uur:00}:00</span>");
            }
            sb.AppendLine("</div>");
        }

        private static void TekenBufferLabels(StringBuilder sb, List<BestaandeWedstrijd> veldWedstrijden, int startMin)
        {
            var alleBlokken = veldWedstrijden
                .GroupBy(w => w.AanvangsTijd)
                .Select(g => new { Start = g.Key, Einde = g.Max(w => w.EindTijd) })
                .OrderBy(b => b.Start).ToList();

            for (int bi = 0; bi < alleBlokken.Count - 1; bi++)
            {
                var einde = alleBlokken[bi].Einde;
                var volgendeStart = alleBlokken[bi + 1].Start;
                int bufMin = (int)(volgendeStart - einde).TotalMinutes;
                if (bufMin > 0 && bufMin <= 60)
                {
                    int eindeMin = einde.Hour * 60 + einde.Minute;
                    int xBuf = (int)((eindeMin - startMin) * PixelsPerUur / 60.0);
                    int bufBreedte = (int)(bufMin * PixelsPerUur / 60.0);
                    sb.AppendLine($"<div style='position:absolute;left:{xBuf}px;top:0;width:{bufBreedte}px;height:{VeldRijHoogte}px;" +
                        $"display:flex;align-items:center;justify-content:center;pointer-events:none;'>" +
                        $"<span style='font-size:8px;color:#ffffff;writing-mode:vertical-lr;text-orientation:mixed;'>{bufMin}m</span></div>");
                }
            }
        }

        /// <summary>
        /// Bereken verticale positie en hoogte op basis van veldsubpositie.
        /// A = bovenste helft, B = onderste helft
        /// A1/A2 = kwarten bovenste helft, B1/B2 = kwarten onderste helft
        /// </summary>
        private static void BerekenSubpositie(string sub, decimal veldDeelGebruik, out int top, out int hoogte)
        {
            int h = VeldRijHoogte - 2;
            switch (sub)
            {
                case "A":   top = 1;          hoogte = h / 2 - 1;    break; // bovenste helft
                case "B":   top = h / 2 + 1;  hoogte = h / 2 - 1;    break; // onderste helft
                case "A1":  top = 1;           hoogte = h / 4 - 1;    break; // kwart linksboven
                case "A2":  top = h / 4 + 1;   hoogte = h / 4 - 1;    break; // kwart rechtsboven
                case "B1":  top = h / 2 + 1;   hoogte = h / 4 - 1;    break; // kwart linksonder
                case "B2":  top = h * 3/4 + 1; hoogte = h / 4 - 1;    break; // kwart rechtsonder
                default:
                    // Geen subpositie: gebruik veldDeelGebruik voor schatting
                    if (veldDeelGebruik <= 0.25m) { top = 1; hoogte = h / 4 - 1; }
                    else if (veldDeelGebruik <= 0.50m) { top = 1; hoogte = h / 2 - 1; }
                    else { top = 1; hoogte = h - 1; }
                    break;
            }
            if (hoogte < 14) hoogte = 14;
        }

        private static void TekenBlok(StringBuilder sb, string paneel,
            BestaandeWedstrijd w, VeldInfo veld, int top, int hoogte, int startMin,
            Dictionary<string, OptimalisatieSuggestie>? verplaatsVan,
            Dictionary<string, OptimalisatieSuggestie>? isNieuwePlek,
            HtmlInstellingen instellingen)
        {
            int wStartMin = w.AanvangsTijd.Hour * 60 + w.AanvangsTijd.Minute;
            int wEindMin = w.EindTijd.Hour * 60 + w.EindTijd.Minute;
            int x = (int)((wStartMin - startMin) * PixelsPerUur / 60.0);
            int breedte = (int)((wEindMin - wStartMin) * PixelsPerUur / 60.0);
            if (breedte < MinBlokBreedtePx) breedte = MinBlokBreedtePx;
            if (x < 0) { breedte += x; x = 0; }

            string lookupKey = WedstrijdSleutel(w);
            string bg = BLK_NORMAAL, rand = BLK_RAND, randStijl = "solid";
            string extraAttr = "";
            string wedstrijdNaam = EncodeAttribute(w.Wedstrijd?.Trim());

            bool isVast = IsEersteElftalWedstrijd(w.Wedstrijd, instellingen);
            bool isVerplaatst = verplaatsVan != null && verplaatsVan.ContainsKey(lookupKey);
            bool isNieuw = isNieuwePlek != null && w.Bron == "Suggestie";

            if (isVast) { bg = BLK_VAST; rand = BLK_VAST_RAND; }
            else if (isVerplaatst) { bg = BLK_OUD; rand = BLK_OUD_RAND; randStijl = "dashed"; var s = verplaatsVan![lookupKey]; extraAttr = $" data-naar='{EncodeAttribute(s.NieuwVeld)} {EncodeAttribute(s.NieuweTijd)}'"; }
            else if (isNieuw) { bg = BLK_NIEUW; rand = BLK_NIEUW_RAND; }

            string naam = w.Wedstrijd?.Trim() ?? "";
            int maxLen = hoogte < 18 ? 18 : 30;
            if (naam.Length > maxLen) naam = naam[..maxLen] + "…";

            sb.AppendLine($"<div class='blok' data-paneel='{paneel}' data-wedstrijd='{wedstrijdNaam}'{extraAttr} " +
                $"style='left:{x}px;top:{top}px;width:{breedte - 1}px;height:{hoogte}px;" +
                $"background:{bg};border:1px {randStijl} {rand};font-size:{(hoogte < 18 ? 7 : 9)}px;'>" +
                $"<b>{w.AanvangsTijd:HH:mm}</b> {EncodeText(naam)}</div>");
        }

        private static string WedstrijdSleutel(BestaandeWedstrijd w) => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";

        private static string SuggestieSleutel(OptimalisatieSuggestie s) => $"{s.HuidigVeldNummer}_{s.HuidigeTijd}_{s.Wedstrijd}";

        private static bool IsEersteElftalWedstrijd(string? wedstrijd, HtmlInstellingen instellingen)
        {
            if (string.IsNullOrWhiteSpace(wedstrijd)) return false;
            var naam = instellingen.EersteElftalNaam;
            if (string.IsNullOrWhiteSpace(naam))
            {
                var clubCode = instellingen.ClubCode;
                naam = string.IsNullOrWhiteSpace(clubCode) ? null : $"{clubCode} 1";
            }
            return !string.IsNullOrWhiteSpace(naam) && wedstrijd.Contains(naam + " ");
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

    }
}
