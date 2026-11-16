using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailInstellingenImportAuditTests
{
    [Fact]
    public void Projecteer_GeeftGewijzigdeSchakelaarsCanonicalEnLaatOngewijzigdeWeg()
    {
        var huidig = new EmailVerwerkingsInstellingen(true, true, null, false, false);

        var velden = EmailInstellingenImportAudit.Projecteer(huidig, verwerking: false, review: true, ontvanger: null);

        velden.Should().ContainSingle().Which.Should().Be(
            new EmailInstellingenImportAuditVeld("EmailVerwerkingIngeschakeld", "1", "0"));
    }

    [Fact]
    public void Projecteer_LaatBeideOngewijzigdeSchakelaarsWeg()
    {
        var huidig = new EmailVerwerkingsInstellingen(false, true, null, false, false);

        var velden = EmailInstellingenImportAudit.Projecteer(huidig, verwerking: false, review: true, ontvanger: null);

        velden.Should().BeEmpty();
    }

    [Fact]
    public void Projecteer_VergelijktOntvangerRuwEnRedigeertPasVoorAudit()
    {
        const string nieuwAdres = "trainer@voorbeeld.nl";
        var huidig = new EmailVerwerkingsInstellingen(false, true, null, false, false);

        var ongewijzigd = EmailInstellingenImportAudit.Projecteer(huidig, false, true, null);
        var gewijzigd = EmailInstellingenImportAudit.Projecteer(huidig, false, true, nieuwAdres);

        ongewijzigd.Should().BeEmpty();
        gewijzigd.Should().ContainSingle();
        var audit = EmailVerwerkingsInstellingenWijziging.AuditWaarden(
            gewijzigd[0].Veld, gewijzigd[0].OudeWaarde, gewijzigd[0].NieuweWaarde);
        audit.OudeWaarde.Should().Be("[niet ingesteld]");
        audit.NieuweWaarde.Should().Be("[ingesteld]");
        audit.NieuweWaarde.Should().NotContain(nieuwAdres);
    }
}
