using System.Text;
using AwesomeAssertions;
using Planner.Shared.Deel;
using UglyToad.PdfPig;
using Xunit;

namespace Planner.Shared.Tests.Deel;

/// <summary>
/// #1363: de PDF die <see cref="PlannerPdfGenerator"/> maakt, teruggelezen met een losse
/// PDF-parser (PdfPig). Een test op alleen de <c>%PDF-</c>-header zou ook slagen als er geen enkel
/// teken op de pagina staat — de tekstextractie bewijst dat de inhoud er werkelijk in zit, en dat
/// Nederlandse tekens als glyph bestaan in het meegeleverde lettertype (een ontbrekende glyph komt
/// terug als vervangteken, niet als "ë").
/// </summary>
public class PlannerPdfGeneratorTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 3);

    private static PlannerShareModel Model(params PlannerShareWedstrijd[] wedstrijden) =>
        new("Veldbezetting op zaterdag 3 oktober 2026", "ALLSTARS", Zaterdag, wedstrijden);

    private static (int Paginas, string Tekst) Lees(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var tekst = new StringBuilder();
        foreach (var pagina in document.GetPages())
            tekst.AppendLine(string.Join(" ", pagina.GetWords().Select(w => w.Text)));
        return (document.NumberOfPages, tekst.ToString());
    }

    [Fact]
    public void Genereer_GeeftEenGeldigePdfMetTitelClubEnTabelinhoud()
    {
        var pdf = PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null)));

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var (paginas, tekst) = Lees(pdf);
        paginas.Should().Be(1);
        tekst.Should().Contain("Veldbezetting op zaterdag 3 oktober 2026")
            .And.Contain("ALLSTARS")
            .And.Contain("09:30")
            .And.Contain("JO10-1")
            .And.Contain("Gasten JO10-2")
            .And.Contain("veld 3 A")
            .And.Contain("competitie");
        tekst.Should().Contain("Tijd").And.Contain("Team").And.Contain("Tegenstander").And.Contain("Veld").And.Contain("Competitie");
    }

    [Fact]
    public void Genereer_LegeLijst_GeeftNettePdfMetMelding()
    {
        var pdf = PlannerPdfGenerator.Genereer(Model());

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var (paginas, tekst) = Lees(pdf);
        paginas.Should().Be(1);
        tekst.Should().Contain("Geen wedstrijden gepland op zaterdag 3 oktober 2026.");
        tekst.Should().NotContain("Tegenstander", "zonder wedstrijden hoort er geen lege tabel te staan");
    }

    [Fact]
    public void Genereer_NederlandseTekens_KomenOngeschondenTerug()
    {
        const string team = "Zoë’s Café JO11-1";
        const string tegenstander = "Coöperatie Reünie ü é è à ç";

        var pdf = PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("10:00", team, tegenstander, "veld één", null, null)));

        var (_, tekst) = Lees(pdf);
        tekst.Should().Contain("Zoë’s").And.Contain("Café").And.Contain("Coöperatie").And.Contain("Reünie")
            .And.Contain("één").And.Contain("ü é è à ç");
        tekst.Should().NotContain("�");
    }

    [Fact]
    public void Genereer_OnbekendTeken_LaatDeExportNietMislukken()
    {
        // Een emoji staat niet in Lato. ThrowOnMissingTextGlyphs staat bewust uit: één vreemd
        // teken in een teamnaam mag niet de hele export blokkeren.
        var genereer = () => PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("10:00", "JO9-1 ⚽", null, null, null, null)));

        genereer.Should().NotThrow();
        Lees(genereer()).Tekst.Should().Contain("JO9-1");
    }

    [Fact]
    public void Genereer_TekstMetOpmaaktekens_WordtLetterlijkWeergegeven()
    {
        // Geen markup-taal in een PDF: wat in de HTML-export een injectie zou zijn (#1010) is hier tekst.
        var pdf = PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("10:00", "<script>alert(1)</script>", "A & B", null, null, null)));

        Lees(pdf).Tekst.Should().Contain("<script>alert(1)</script>").And.Contain("A & B");
    }

    [Fact]
    public void Genereer_ScheidsrechterKolom_AlleenAlsErEenBekendIs()
    {
        var zonder = Lees(PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("10:00", "JO10-1", null, null, null, null)))).Tekst;
        var met = Lees(PlannerPdfGenerator.Genereer(Model(
            new PlannerShareWedstrijd("10:00", "JO10-1", null, null, null, "J. Fluitist"),
            new PlannerShareWedstrijd("11:00", "JO10-2", null, null, null, null)))).Tekst;

        zonder.Should().NotContain("Scheidsrechter");
        met.Should().Contain("Scheidsrechter").And.Contain("J. Fluitist");
    }

    [Fact]
    public void Genereer_VeelWedstrijden_LoopenDoorOpVolgendePaginasMetPaginanummer()
    {
        var regels = Enumerable.Range(1, 120)
            .Select(i => new PlannerShareWedstrijd("10:00", $"Team{i:000}", null, null, null, null))
            .ToArray();

        var (paginas, tekst) = Lees(PlannerPdfGenerator.Genereer(Model(regels)));

        paginas.Should().BeGreaterThan(1);
        tekst.Should().Contain("Team001").And.Contain("Team120");
        tekst.Should().Contain($"Pagina {paginas} van {paginas}");
    }

    [Fact]
    public void Genereer_ZetDeTitelInDeDocumentMetadata()
    {
        using var document = PdfDocument.Open(PlannerPdfGenerator.Genereer(Model()));

        document.Information.Title.Should().Be("Veldbezetting op zaterdag 3 oktober 2026");
    }

    [Fact]
    public void Genereer_WeigertNull()
    {
        var genereer = () => PlannerPdfGenerator.Genereer(null!);

        genereer.Should().Throw<ArgumentNullException>();
    }
}
