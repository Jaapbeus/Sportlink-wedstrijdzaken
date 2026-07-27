using FluentAssertions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor de idempotentie-guard van de e-mailverwerking (#712).
///
/// Het faalscenario dat hier wordt vastgezet:
///   1. Poll 1 maakt de verwerkingsrij aan, daarna gooit het versturen (Graph 429/503/time-out) en
///      belandt de rij op status 'Fout'. Het bericht blijft bewust ongelezen voor de volgende poll.
///   2. Poll 2 zag alleen dát er een rij bestond, logde "al verwerkt, overslaan" en markeerde het
///      bericht als gelezen.
///   3. Netto: de afzender kreeg nooit antwoord en het bericht verdween uit de wachtrij.
/// De guard kijkt daarom naar de eindstatus, niet naar het bestaan van de rij.
/// </summary>
public class EmailIdempotentieTests
{
    private static EmailVerwerkingStand Stand(
        string status, int pogingen = 1, bool antwoordVerstuurd = false)
        => new(VerwerkingId: 42, Status: status, Pogingen: pogingen, AntwoordVerstuurd: antwoordVerstuurd);

    [Fact]
    public void GeenRij_LeidtTotNieuweVerwerking()
    {
        EmailIdempotentie.Bepaal(null).Should().Be(VerwerkingsBesluit.NieuweVerwerking);
    }

    [Fact]
    public void StatusFoutNaVerzendfout_WordtOpnieuwVerwerkt()
    {
        // Precies stap 2 van het faalscenario: vroeger "al verwerkt, overslaan".
        EmailIdempotentie.Bepaal(Stand(nameof(EmailStatus.Fout)))
            .Should().Be(VerwerkingsBesluit.HerhaalVerwerking);
    }

    [Theory]
    [InlineData(nameof(EmailStatus.Ontvangen))]
    [InlineData(nameof(EmailStatus.Geclassificeerd))]
    [InlineData(nameof(EmailStatus.Verwerkt))]
    [InlineData(nameof(EmailStatus.Review))]
    public void NietDefinitieveStatus_WordtOpnieuwVerwerkt(string status)
    {
        // Elke exception ná de INSERT (plannerfout, ontbrekende speelduur, templatefout) laat de rij
        // op een van deze statussen achter. Alle vier moeten opnieuw verwerkt worden.
        EmailIdempotentie.Bepaal(Stand(status))
            .Should().Be(VerwerkingsBesluit.HerhaalVerwerking);
    }

    [Theory]
    [InlineData(nameof(EmailStatus.AntwoordVerstuurd))]
    [InlineData(nameof(EmailStatus.GeenAntwoordNodig))]
    [InlineData(nameof(EmailStatus.BuitenScope))]
    public void DefinitieveStatus_WordtOvergeslagen(string status)
    {
        EmailIdempotentie.Bepaal(Stand(status))
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);
    }

    [Fact]
    public void AntwoordAlVerstuurd_WordtAltijdOvergeslagen_OokBijNietDefinitieveStatus()
    {
        // Harde grens tegen een dubbel antwoord: VerstuurdNaar is gevuld, dus er is aantoonbaar al
        // een antwoord de deur uit. Zelfs als de status daarna op 'Fout' is gezet mag er geen
        // tweede antwoord volgen.
        EmailIdempotentie.Bepaal(Stand(nameof(EmailStatus.Fout), antwoordVerstuurd: true))
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);

        EmailIdempotentie.Bepaal(Stand(nameof(EmailStatus.Verwerkt), pogingen: 9, antwoordVerstuurd: true))
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);
    }

    [Fact]
    public void OnbekendeStatus_WordtOpnieuwVerwerkt_MaarNooitNaEenVerstuurdAntwoord()
    {
        // Een statuswaarde uit een oudere of nieuwere versie mag niet leiden tot stilzwijgend
        // laten liggen. Opnieuw verwerken is veilig zolang VerstuurdNaar leeg is.
        EmailIdempotentie.Bepaal(Stand("IetsOnbekends"))
            .Should().Be(VerwerkingsBesluit.HerhaalVerwerking);

        EmailIdempotentie.Bepaal(Stand("IetsOnbekends", antwoordVerstuurd: true))
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);
    }

    [Fact]
    public void LaatstePogingBinnenLimiet_WordtNogEenKeerVerwerkt()
    {
        EmailIdempotentie.Bepaal(Stand(nameof(EmailStatus.Fout), pogingen: EmailIdempotentie.MaxPogingen - 1))
            .Should().Be(VerwerkingsBesluit.HerhaalVerwerking);
    }

    [Theory]
    [InlineData(EmailIdempotentie.MaxPogingen)]
    [InlineData(EmailIdempotentie.MaxPogingen + 5)]
    public void MaxPogingenBereikt_WordtOpgegeven(int pogingen)
    {
        // Zonder deze grens blijft een structureel falend bericht elke poll terugkomen. De poll pakt
        // de 10 oudste ongelezen berichten, dus tien zulke berichten blokkeren alle nieuwe post én
        // kosten elke poll opnieuw een AI-call.
        EmailIdempotentie.Bepaal(Stand(nameof(EmailStatus.Fout), pogingen))
            .Should().Be(VerwerkingsBesluit.OpgevenNaMaxPogingen);
    }

    [Fact]
    public void AnonimiseerdeRij_BlijftOvergeslagen_ViaDeStatus()
    {
        // planner.sp_CleanupEmailVerwerking zet VerstuurdNaar na 30 dagen op NULL (AVG-retentie).
        // De statuslijst is daarom een tweede, onafhankelijke grens: een oud maar nooit gelezen
        // bericht mag ook ná anonimisering geen tweede antwoord krijgen.
        EmailIdempotentie.Bepaal(
                Stand(nameof(EmailStatus.AntwoordVerstuurd), pogingen: 1, antwoordVerstuurd: false))
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);
    }

    [Fact]
    public void MaxPogingen_IsDrie()
    {
        // Bewuste keuze: genoeg voor tijdelijke fouten, laag genoeg om de wachtrij vrij te houden.
        EmailIdempotentie.MaxPogingen.Should().Be(3);
    }
}
