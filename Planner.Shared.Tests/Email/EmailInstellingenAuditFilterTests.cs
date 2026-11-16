using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailInstellingenAuditFilterTests
{
    [Fact]
    public void EenVeldWijzigen_LaatAlleenDatVeldInSchrijfEnAuditlijst()
    {
        var changes = new Dictionary<string, string?> { ["EmailReviewOntvanger"] = "trainer@voorbeeld.nl" };
        var oud = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            new(false, true, null, true, false), true);
        EmailInstellingenAdminCore.BereidVoor(oud, changes, false);
        var opgeslagen = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "False", ["EmailReviewModusActief"] = "True",
            ["EmailReviewOntvanger"] = null
        };

        EmailVerwerkingsInstellingenWijziging.VerwijderOngewijzigdeVelden(changes, opgeslagen);

        changes.Should().ContainSingle().Which.Key.Should().Be("EmailReviewOntvanger");
        var audit = EmailVerwerkingsInstellingenWijziging.AuditWaarden(
            "EmailReviewOntvanger", null, changes["EmailReviewOntvanger"]);
        audit.NieuweWaarde.Should().NotContain("trainer@voorbeeld.nl");
    }

    [Fact]
    public void GelijkeBoolWaarden_TrueFalseEnEenNul_GevenGeenSchijnaudit()
    {
        var changes = new Dictionary<string, string?> { ["EmailVerwerkingIngeschakeld"] = "1" };
        var oud = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            new(true, true, null, true, false), true);
        EmailInstellingenAdminCore.BereidVoor(oud, changes, false);
        var opgeslagen = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "True", ["EmailReviewModusActief"] = "True",
            ["EmailReviewOntvanger"] = null
        };

        EmailVerwerkingsInstellingenWijziging.VerwijderOngewijzigdeVelden(changes, opgeslagen);

        changes.Should().BeEmpty();
    }

    [Fact]
    public void NietGeinitialiseerdeOnveiligeRuweRij_WordtTeruggezetNaarVeiligeStandaard()
    {
        var changes = new Dictionary<string, string?> { ["EmailReviewOntvanger"] = "trainer@voorbeeld.nl" };
        var oud = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            new(true, false, null, false, false), true);
        oud.Status.Should().Be(EmailVerwerkingsInstellingenLeesStatus.NietGeinitialiseerd);
        EmailInstellingenAdminCore.BereidVoor(oud, changes, false);
        var opgeslagen = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "True", ["EmailReviewModusActief"] = "False",
            ["EmailReviewOntvanger"] = null
        };

        EmailVerwerkingsInstellingenWijziging.VerwijderOngewijzigdeVelden(changes, opgeslagen);

        changes.Should().ContainKeys("EmailVerwerkingIngeschakeld", "EmailReviewModusActief", "EmailReviewOntvanger");
        changes["EmailVerwerkingIngeschakeld"].Should().Be("0");
        changes["EmailReviewModusActief"].Should().Be("1");
    }

    [Fact]
    public void BevestigdeReviewUitschakeling_BlijftAuditeerbaarNaFilteren()
    {
        var changes = new Dictionary<string, string?> { ["EmailReviewModusActief"] = "false" };
        var oud = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            new(false, true, null, true, false), true);
        var wijziging = EmailInstellingenAdminCore.BereidVoor(oud, changes, true);
        var opgeslagen = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "False", ["EmailReviewModusActief"] = "True",
            ["EmailReviewOntvanger"] = null
        };

        EmailVerwerkingsInstellingenWijziging.VerwijderOngewijzigdeVelden(changes, opgeslagen);

        changes.Should().ContainSingle().Which.Key.Should().Be("EmailReviewModusActief");
        EmailInstellingenAdminCore.BevestigingAuditen(wijziging!).Should().BeTrue();
    }
}
