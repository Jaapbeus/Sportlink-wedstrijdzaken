using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>#1809: verharding van de e-mailverwerking vóór review mode uit gaat (synthetische invoer).</summary>
public class EmailVerhardingTests
{
    [Theory]
    [InlineData("jan@example.com", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a@example.com, b@example.com", false)]
    [InlineData("a@example.com;b@example.com", false)]
    [InlineData("Jan <a@example.com>", false)]
    [InlineData("a@example.com\r\nBcc: b@example.com", false)]
    [InlineData("noreply@example.com", false)]
    [InlineData("Mailer-Daemon@example.com", false)]
    [InlineData("a@localhost", false)]
    [InlineData("geen-adres", false)]
    public void AfzenderControle_AlleenEenEnkelGeldigMensAdres(string? afzender, bool verwacht)
        => AfzenderControle.IsGeschikt(afzender).Should().Be(verwacht);

    [Theory]
    [InlineData("Jan", "Jan")]
    [InlineData("<script>x</script> Piet", "")]
    [InlineData("http://evil.example", "")]
    public void Voornaam_VrijeTekstUitWeergavenaam_WordtGeweigerd(string naam, string verwacht)
        => AntwoordTekst.Voornaam(naam).Should().Be(verwacht);

    [Fact]
    public void Waarschuwing_VerwijdertLinksAdressenEnMarkup()
    {
        var schoon = AiVeldSanering.Waarschuwing("Let op: <b>klik</b> https://evil.example of mail a@example.com\r\nnu");
        schoon.Should().NotContain("://").And.NotContain("@").And.NotContain("<").And.NotContain("\n");
        schoon.Should().Contain("Let op:");
    }

    [Fact]
    public void Waarschuwing_BegrenstDeLengte()
        => AiVeldSanering.Waarschuwing(new string('x', 2000)).Length.Should().BeLessThanOrEqualTo(AiVeldSanering.MaxWaarschuwingLengte + 1);

    [Fact]
    public void SaneerWaarschuwingen_BijApiUitvoer_BegrenstEnHoudtAlleenVeiligeBetekenis()
    {
        var waarschuwingen = new List<string> { "Controleer dit https://example.com", "trainer@voorbeeld.nl" };

        AiVeldSanering.SaneerWaarschuwingen(waarschuwingen);

        waarschuwingen.Should().ContainSingle().Which.Should().Be("Controleer dit");
    }

    [Theory]
    [InlineData("Jan de Vries", "Jan de Vries")]
    [InlineData("Jan\r\nBcc: x", null)]
    [InlineData("trainer@voorbeeld.nl", null)]
    public void AfzenderNaamWeergave_AccepteertAlleenEenKortePlatteNaam(string naam, string? verwacht)
        => AiVeldSanering.AfzenderNaamWeergave(naam).Should().Be(verwacht);

    [Theory]
    [InlineData("Beschikbaarheid", "Beschikbaarheid")]
    [InlineData("Beschikbaarheid\r\nBcc: test", null)]
    [InlineData("https://example.com", null)]
    [InlineData("", null)]
    public void OnderwerpWeergave_AccepteertAlleenEenBegrensdePlatteRegel(string onderwerp, string? verwacht)
        => AiVeldSanering.OnderwerpWeergave(onderwerp).Should().Be(verwacht);

    [Fact]
    public void BouwClassificatieUserPrompt_StuurtGeenAdressenOfNummersNaarDeProvider()
    {
        var synthetischNummer = string.Concat("Bel ", "06 ", "1234", "5678");
        var prompt = BerichtAiKern.BouwClassificatieUserPrompt(
            "trainer@voorbeeld.nl", "Mail mij op trainer@voorbeeld.nl", synthetischNummer + " of trainer@voorbeeld.nl", "m1");
        prompt.Should().NotContain("trainer@").And.NotContain("voorbeeld.nl").And.NotContain(string.Concat("1234", "5678"));
        prompt.Should().Contain("[e-mail]").And.Contain("[nummer]");
    }

    [Fact]
    public void TeamContactDoorstuurBericht_SaneertKopEnReplyToEnBegrenstInhoud()
    {
        var bericht = TeamContactDoorstuurBericht.Bouw(
            "JO13-2", "Jan\r\nBcc: test", "a@voorbeeld.nl", new string('x', 9000), NullLogger.Instance);

        bericht.Onderwerp.Should().Be("[JO13-2] vraag van afzender");
        bericht.Inhoud.Should().Contain("Vraag van: afzender").And.NotContain("Bcc:");
        bericht.Inhoud.Length.Should().BeLessThan(9000);
        bericht.ReplyTo.Should().Be("a@voorbeeld.nl");
    }

    [Theory]
    [InlineData("trainer@voorbeeld.nl", true)]
    [InlineData("noreply@allstars-fc.test", false)]
    [InlineData("ongeldig", false)]
    public void TeamContactDoorstuurBericht_BelooftDirectAntwoordenAlleenMetVeiligeReplyTo(
        string afzender, bool kanDirectAntwoorden)
    {
        var bericht = TeamContactDoorstuurBericht.Bouw(
            "JO13-2", "Jan de Vries", afzender, "Synthetische vraag", NullLogger.Instance);

        (bericht.ReplyTo is not null).Should().Be(kanDirectAntwoorden);
        bericht.Inhoud.Contains("uw antwoord gaat naar de vraagsteller").Should().Be(kanDirectAntwoorden);
        bericht.Inhoud.Contains("kies handmatig het juiste contactkanaal").Should().Be(!kanDirectAntwoorden);
    }
}
