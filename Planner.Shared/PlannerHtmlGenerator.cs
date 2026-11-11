using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Planner.Shared
{
    /// <summary>
    /// De vier clubinstellingen die de HTML-generator nodig heeft. Bewust een expliciete parameter
    /// in plaats van een greep in een statische instellingencache (issue 888 vervolg, §42).
    ///
    /// <para>
    /// <b>Waarom dit type bestaat.</b> Dit bestand stond op de SQL Server-tier en las zijn
    /// instellingen rechtstreeks uit <c>SystemUtilities.AppSettings</c> — de enige reden waarom 576
    /// regels pure stringopbouw niet gedeeld kónden worden. Dat is precies het soort duplicatie dat
    /// de architectuurregel in docs/ARCHITECTUUR-DATABASE-TIERS.md wil voorkomen. Door de vier
    /// waarden binnen te geven werkt de generator op beide tiers, met elk zijn eigen cache als bron.
    /// </para>
    ///
    /// <para>
    /// <c>EersteElftalNaam</c> en <c>ClubCode</c> mogen <c>null</c> zijn: dan vervalt de
    /// eerste-elftal-markering en de senioren-sortering, precies zoals het origineel deed wanneer
    /// die instellingen leeg waren. <c>PlannerAfzenderNaam</c> is verplicht — het origineel gooide
    /// daar een <see cref="InvalidOperationException"/> op, en die controle hoort bij de aanroeper
    /// die de instelling ophaalt, niet halverwege het renderen.
    /// </para>
    /// </summary>
    public sealed record HtmlInstellingen(
        string Accommodatie,
        string PlannerAfzenderNaam,
        string? EersteElftalNaam,
        string? ClubCode);

    /// <summary>
    /// Genereert een visuele HTML veldplanner in Sportlink-stijl.
    /// Browser-versie: side-by-side oud/nieuw met klik-interactie.
    /// Email-versie: versimpelde inline CSS tabel.
    /// </summary>
    public static partial class PlannerHtmlGenerator
    {
        private const int PixelsPerUur = 100;
        private const int VeldRijHoogte = 80;
        private const int VeldHeaderBreedte = 50;
        private const int StartMinuutVanDag = 8 * 60 + 30; // 08:30
        private const int MinBlokBreedtePx = 25;

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

        /// <summary>
        /// Encodeert dynamische (uit de database afkomstige) tekst voor een HTML-tekstcontext
        /// (tussen tags, bv. een <c>&lt;td&gt;</c>-inhoud). Voorkomt HTML-injectie via
        /// wedstrijd-/team-/veldnamen (#1010) — <c>WebUtility.HtmlEncode</c> is dezelfde keuze als
        /// <see cref="EmailSanitizer"/> elders in dit project.
        /// </summary>
        private static string EncodeText(string? waarde) => WebUtility.HtmlEncode(waarde ?? "");

        /// <summary>
        /// Encodeert dynamische tekst voor gebruik in een enkel-aangehaald (<c>'…'</c>) HTML-attribuut.
        /// <c>WebUtility.HtmlEncode</c> laat een apostrof onaangeroerd — zonder de extra vervanging
        /// zou ingevoerde tekst met een <c>'</c> het attribuut kunnen afsluiten (#1010).
        /// </summary>
        private static string EncodeAttribute(string? waarde) => EncodeText(waarde).Replace("'", "&#39;");

        /// <summary>
        /// Valideert en encodeert een URL voor gebruik in een <c>href</c>-attribuut. Staat uitsluitend
        /// <c>http</c>/<c>https</c> toe — een <c>javascript:</c>- of ander uitvoerbaar schema wordt
        /// vervangen door <c>#</c> (#1010).
        /// </summary>
        private static string SanitizeHref(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "#";
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return EncodeAttribute(url);
            }
            return "#";
        }

        public static string GenereerHtml(
            DateOnly datum,
            List<BestaandeWedstrijd> alleWedstrijden,
            List<OptimalisatieSuggestie> suggesties,
            List<VeldInfo> velden,
            // doel ("huidig"/"optimaal") wordt momenteel niet gebruikt in de gegenereerde HTML
            string doel,
            HtmlInstellingen instellingen)
        {
            var sb = new StringBuilder();
            var nl = new System.Globalization.CultureInfo("nl-NL");
            var grasveldNummers = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            var actieveVelden = velden.OrderBy(v => v.VeldNummer).ToList();

            // Dedup wedstrijden
            var wedstrijden = alleWedstrijden
                .GroupBy(w => WedstrijdSleutel(w))
                .Select(g => g.First()).ToList();

            int startMin = StartMinuutVanDag;

            if (!wedstrijden.Any() && !suggesties.Any())
                return $"<p style='margin:0;color:{TXT_DIM};font-family:sans-serif;'>Geen wedstrijden gepland op {datum.ToString("dddd d MMMM yyyy", nl)}.</p>";

            var (eindMin, totaalMin, gridBreedte) = BerekenTijdlijn(startMin, wedstrijden, suggesties);

            // Verplaatsingen indexeren
            var verplaatsVan = new Dictionary<string, OptimalisatieSuggestie>();
            foreach (var s in suggesties)
                verplaatsVan[SuggestieSleutel(s)] = s;

            // Bouw nieuwe situatie bezetting
            var nieuweBezetting = BouwNieuweBezetting(wedstrijden, suggesties);

            // HTML start
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            BouwStyleBlock(sb);
            sb.AppendLine($"<body>");

            // Titel
            sb.AppendLine($"<h2 style='margin:0 0 3px 0;'>Veldplanner — Optimalisatieadvies</h2>");
            var accommodatieNaam = instellingen.Accommodatie ?? "";
            var locatieSuffix = string.IsNullOrWhiteSpace(accommodatieNaam) ? "" : $" — {EncodeText(accommodatieNaam)}";
            sb.AppendLine($"<p style='margin:0 0 10px 0;color:{TXT_DIM};font-size:12px;'>{datum.ToString("dddd d MMMM yyyy", nl)}{locatieSuffix} — {suggesties.Count} suggestie(s)</p>");

            // Legenda
            BouwLegenda(sb);

            // Side-by-side container
            sb.AppendLine("<div style='display:flex;gap:15px;overflow-x:auto;'>");

            // === LINKER PANEEL: HUIDIGE SITUATIE ===
            sb.AppendLine("<div>");
            sb.AppendLine($"<div class='sectie-titel' style='background:{BG_TIJD};'>Huidige situatie</div>");
            TekenGrid(sb, "oud", actieveVelden, wedstrijden, verplaatsVan, null, startMin, gridBreedte, totaalMin, instellingen);
            sb.AppendLine("</div>");

            // === RECHTER PANEEL: NIEUWE SITUATIE ===
            sb.AppendLine("<div>");
            sb.AppendLine($"<div class='sectie-titel' style='background:#1a2a1a;border:1px solid {BLK_NIEUW_RAND};'>Nieuwe situatie (suggestie)</div>");
            TekenGrid(sb, "nieuw", actieveVelden, nieuweBezetting, null, verplaatsVan, startMin, gridBreedte, totaalMin, instellingen);
            sb.AppendLine("</div>");

            sb.AppendLine("</div>"); // einde side-by-side

            // Tabellen naast elkaar, zelfde gap als planners
            sb.AppendLine("<div style='display:flex;gap:15px;'>");

            sb.AppendLine($"<div style='flex:1;max-width:{gridBreedte + VeldHeaderBreedte}px;'>");
            sb.AppendLine($"<h3 style='margin:20px 0 8px 0;'>Chronologisch overzicht</h3>");
            TekenChronoTabel(sb, wedstrijden, suggesties, velden, verplaatsVan, instellingen);
            sb.AppendLine("</div>");

            sb.AppendLine($"<div style='flex:1;max-width:{gridBreedte + VeldHeaderBreedte}px;'>");
            sb.AppendLine($"<h3 style='margin:20px 0 8px 0;'>Per leeftijdscategorie</h3>");
            TekenCategorieTabel(sb, wedstrijden, nieuweBezetting, suggesties, velden, verplaatsVan, instellingen);
            sb.AppendLine("</div>");

            sb.AppendLine("</div>");

            // Samenvatting
            var plannerNaam = instellingen.PlannerAfzenderNaam;
            sb.AppendLine($"<p style='margin:10px 0;color:{TXT_DIM};font-size:11px;'>Suggesties: {suggesties.Count} | Van grasveld verplaatst: {suggesties.Count(s => grasveldNummers.Contains(s.HuidigVeldNummer))} | Gegenereerd door {EncodeText(plannerNaam)}</p>");

            // JavaScript: klik-interactie
            sb.AppendLine("<script>");
            sb.AppendLine(BouwKlikScript());
            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

    }
}
