using System;
using System.Linq;
using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Planner.Shared.Deel
{
    /// <summary>
    /// Rendert een <see cref="PlannerShareModel"/> als PDF met QuestPDF (#1363, epic #1365).
    /// <para>
    /// Een eigen, eenvoudig documentmodel naast de bestaande HTML-export — bewust géén
    /// HTML→PDF-conversie, en bewust geen wijziging aan <see cref="PlannerHtmlGenerator"/>.
    /// Achtergrond, licentie en platformbewijs: docs/ARCHITECTUUR-PDF-EXPORT.md.
    /// </para>
    /// <para>
    /// <b>Tekst is geen opmaak.</b> Alle waarden gaan via QuestPDF's <c>Text(...)</c> de PDF in als
    /// tekst; er is geen markup-taal die een teamnaam als opdracht kan interpreteren. Er is hier dus
    /// geen tegenhanger nodig van de HTML-encoding uit #1010.
    /// </para>
    /// </summary>
    public static class PlannerPdfGenerator
    {
        private const string KopKleur = "#1F3A5F";
        private const string RasterKleur = "#D0D7DE";
        private const string ZebraKleur = "#F6F8FA";
        private const string GedimdKleur = "#57606A";

        // Het lettertype dat QuestPDF zelf meelevert (QuestPDF.Fonts.Lato.br in de publish-output).
        // Als string, niet via de sinds 2026.9 verouderde Fonts-klasse.
        internal const string Lettertype = "Lato";

        /// <summary>
        /// QuestPDF's globale instellingen, één keer per proces. Hier en niet in de twee
        /// <c>Program.cs</c>-bestanden: dan geldt het automatisch op beide tiers én in de tests, en
        /// kan een aanroeper de generator nooit gebruiken voordat de licentie gezet is.
        /// </summary>
        static PlannerPdfGenerator()
        {
            // #1459: de licentie wordt hier bewust NIET gezet maar in Genereer, alleen als de club
            // PDF-export heeft ingeschakeld (zelfverklaring Community-voorwaarden, zie
            // docs/ARCHITECTUUR-PDF-EXPORT.md §2).

            // Alleen het meegeleverde Lato-lettertype, nooit systeemfonts: de Functions-host
            // garandeert geen geïnstalleerde fonts, en dezelfde invoer moet overal dezelfde PDF geven.
            Settings.UseSystemFonts = false;

            // Geen recursieve scan van de app-map (/home/site/wwwroot) naar fontbestanden bij de
            // eerste PDF — er staan er geen, en het kost koude-starttijd. Lato komt uit QuestPDF zelf.
            Settings.FontDiscoveryPath = null;

            // Eén onbekend teken (bv. een emoji in een teamnaam) mag niet de hele export laten
            // mislukken; het wordt dan als vervangteken getoond. Nederlandse tekens zitten in Lato —
            // dat bewijst PlannerPdfGeneratorTests.
            Settings.ThrowOnMissingTextGlyphs = false;
        }

        /// <summary>
        /// Genereert de PDF. Een lege wedstrijdlijst geeft een geldige PDF met een melding.
        /// <paramref name="pdfIngeschakeld"/> is de clubinstelling "PDF-export" (#1459): alleen dan
        /// wordt <c>QuestPDF.Settings.License = Community</c> gezet. Bij <c>false</c> gooit dit een
        /// <see cref="InvalidOperationException"/> — de endpoints weigeren al eerder met 409, dit is
        /// de laatste vangrail zodat er nooit een PDF ontstaat zonder bevestigde licentie.
        /// </summary>
        public static byte[] Genereer(PlannerShareModel model, bool pdfIngeschakeld)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (!pdfIngeschakeld)
                throw new InvalidOperationException("PDF-export is voor deze club niet ingeschakeld (QuestPDF-licentie niet bevestigd).");

            // Zonder deze regel weigert QuestPDF te renderen. "Community" is een zelfverklaring van
            // de club die deze installatie draait: < USD 1.000.000 jaaromzet.
            Settings.License = LicenseType.Community;

            return Document.Create(document =>
                {
                    document.Page(page =>
                    {
                        page.Size(PageSizes.A4.Landscape());
                        page.Margin(1.5f, Unit.Centimetre);
                        page.DefaultTextStyle(stijl => stijl.FontFamily(Lettertype).FontSize(10));

                        page.Header().Element(kop => Kop(kop, model));
                        page.Content().PaddingTop(12).Element(inhoud => Inhoud(inhoud, model));
                        page.Footer().AlignRight().Text(tekst =>
                        {
                            tekst.DefaultTextStyle(stijl => stijl.FontSize(8).FontColor(GedimdKleur));
                            tekst.Span("Pagina ");
                            tekst.CurrentPageNumber();
                            tekst.Span(" van ");
                            tekst.TotalPages();
                        });
                    });
                })
                .WithMetadata(new DocumentMetadata
                {
                    Title = model.Titel,
                    Subject = $"Planning {model.ClubCode}",
                    Creator = "Sportlink-wedstrijdzaken",
                })
                .GeneratePdf();
        }

        private static void Kop(IContainer container, PlannerShareModel model)
        {
            container.Column(kolom =>
            {
                kolom.Item().Text(model.Titel).FontSize(16).Bold().FontColor(KopKleur);
                kolom.Item().Text($"Club: {model.ClubCode} · Speeldag: {PlannerShareModelBuilder.DatumTekst(model.Peildatum)}")
                    .FontSize(9).FontColor(GedimdKleur);
            });
        }

        private static void Inhoud(IContainer container, PlannerShareModel model)
        {
            if (model.Wedstrijden.Count == 0)
            {
                container.Text($"Geen wedstrijden gepland op {PlannerShareModelBuilder.DatumTekst(model.Peildatum)}.")
                    .Italic().FontColor(GedimdKleur);
                return;
            }

            // "Scheidsrechter waar bekend": de kolom verschijnt alleen als minstens één regel er een heeft.
            var metScheidsrechter = model.Wedstrijden.Any(w => !string.IsNullOrWhiteSpace(w.Scheidsrechter));

            container.Table(tabel =>
            {
                tabel.ColumnsDefinition(kolommen =>
                {
                    kolommen.ConstantColumn(48);   // Tijd
                    kolommen.RelativeColumn(3);    // Team
                    kolommen.RelativeColumn(3);    // Tegenstander
                    kolommen.RelativeColumn(2);    // Veld
                    kolommen.RelativeColumn(2);    // Competitie
                    if (metScheidsrechter)
                        kolommen.RelativeColumn(3); // Scheidsrechter
                });

                tabel.Header(kop =>
                {
                    KopCel(kop.Cell(), "Tijd");
                    KopCel(kop.Cell(), "Team");
                    KopCel(kop.Cell(), "Tegenstander");
                    KopCel(kop.Cell(), "Veld");
                    KopCel(kop.Cell(), "Competitie");
                    if (metScheidsrechter)
                        KopCel(kop.Cell(), "Scheidsrechter");
                });

                for (var i = 0; i < model.Wedstrijden.Count; i++)
                {
                    var w = model.Wedstrijden[i];
                    var zebra = i % 2 == 1;
                    Cel(tabel.Cell(), w.Tijd, zebra);
                    Cel(tabel.Cell(), w.TeamWeergave, zebra);
                    Cel(tabel.Cell(), w.Tegenstander, zebra);
                    Cel(tabel.Cell(), w.Veld, zebra);
                    Cel(tabel.Cell(), w.Competitie, zebra);
                    if (metScheidsrechter)
                        Cel(tabel.Cell(), w.Scheidsrechter, zebra);
                }
            });
        }

        private static void KopCel(IContainer cel, string tekst) =>
            cel.Background(KopKleur).PaddingVertical(4).PaddingHorizontal(5)
                .Text(tekst).FontColor(Colors.White).Bold();

        private static void Cel(IContainer cel, string? tekst, bool zebra)
        {
            var opgemaakt = cel.BorderBottom(0.5f).BorderColor(RasterKleur);
            if (zebra)
                opgemaakt = opgemaakt.Background(ZebraKleur);
            opgemaakt.PaddingVertical(3).PaddingHorizontal(5).Text(string.IsNullOrWhiteSpace(tekst) ? "—" : tekst);
        }
    }
}
