using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailInstellingenAuditFilterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VergrendeldeRuweLezing_FiltersAlleenOngewijzigdeMailveldenEnBehoudtOudeWaarden(bool rijAanwezig)
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("EmailVerwerkingIngeschakeld", typeof(bool));
        table.Columns.Add("EmailReviewModusActief", typeof(bool));
        table.Columns.Add("EmailReviewOntvanger", typeof(string));
        if (rijAanwezig) table.Rows.Add(false, true, "trainer@voorbeeld.nl");
        using var reader = table.CreateDataReader();
        var changes = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "false",
            ["EmailReviewModusActief"] = "false",
            ["EmailReviewOntvanger"] = "trainer@voorbeeld.nl"
        };

        var opgeslagen = await EmailInstellingenAdminCore.LeesVoorWijzigingAsync(reader, changes);

        if (rijAanwezig)
        {
            changes.Keys.Should().ContainSingle().Which.Should().Be("EmailReviewModusActief");
            opgeslagen["EmailReviewModusActief"].Should().Be("1");
            changes["EmailReviewModusActief"].Should().Be("0");
            opgeslagen["EmailReviewOntvanger"].Should().Be("trainer@voorbeeld.nl");
        }
        else
        {
            opgeslagen.Should().BeEmpty();
            changes.Should().HaveCount(3);
        }
    }

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
        opgeslagen["EmailReviewModusActief"].Should().Be("1");
        changes["EmailReviewModusActief"].Should().Be("0");
        EmailInstellingenAdminCore.BevestigingAuditen(wijziging!).Should().BeTrue();
    }
}
