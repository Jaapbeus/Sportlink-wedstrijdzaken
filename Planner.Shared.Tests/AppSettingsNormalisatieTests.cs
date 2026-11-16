using System.Globalization;
using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

public class AppSettingsNormalisatieTests
{
    [Theory]
    [InlineData("OptimalisatieAanvang", null, null)]
    [InlineData("OptimalisatieAanvang", "", null)]
    [InlineData("optimalisatieaanvang", "  ", null)]
    [InlineData("OPTIMALISATIEAANVANG", "\t", null)]
    [InlineData("OptimalisatieAanvang", "08:00", "08:00")]
    [InlineData("OptimalisatieAanvang", "ongeldig", "ongeldig")]
    [InlineData("Accommodatie", "", "")]
    [InlineData("Accommodatie", "  ", "  ")]
    [InlineData("Accommodatie", null, null)]
    public void AlleenLegeOptimalisatieAanvangWordtNull(string veld, string? waarde, string? verwacht)
    {
        AppSettingsNormalisatie.NormaliseerWaarde(veld, waarde).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("nl-NL")]
    public void AuditAanvangHeeftOpBeideTiersHetzelfdeFormaatAlsNieuweWaarde(string cultuur)
    {
        var vorigeCultuur = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultuur);
            foreach (var tijd in new[] { new TimeOnly(0, 0), new TimeOnly(8, 5), new TimeOnly(23, 59) })
            {
                var nieuw = tijd.ToString("HH:mm", CultureInfo.InvariantCulture);
                AppSettingsNormalisatie.FormatteerAuditWaarde("OptimalisatieAanvang", tijd).Should().Be(nieuw);
                AppSettingsNormalisatie.FormatteerAuditWaarde("optimalisatieaanvang", tijd.ToTimeSpan()).Should().Be(nieuw);
                AppSettingsNormalisatie.FormatteerAuditWaarde("AnderVeld", tijd).Should().Be(tijd.ToString());
                AppSettingsNormalisatie.FormatteerAuditWaarde("AnderVeld", tijd.ToTimeSpan()).Should().Be(tijd.ToTimeSpan().ToString());
            }
        }
        finally { CultureInfo.CurrentCulture = vorigeCultuur; }
    }

    [Fact]
    public void AuditBehoudtNullEnOverigeWaarden()
    {
        AppSettingsNormalisatie.FormatteerAuditWaarde("OptimalisatieAanvang", null).Should().BeNull();
        AppSettingsNormalisatie.FormatteerAuditWaarde("OptimalisatieAanvang", DBNull.Value).Should().BeNull();
        AppSettingsNormalisatie.FormatteerAuditWaarde("AnderVeld", DBNull.Value).Should().BeNull();
        AppSettingsNormalisatie.FormatteerAuditWaarde("Accommodatie", "  ").Should().Be("  ");
        AppSettingsNormalisatie.FormatteerAuditWaarde("SeasonStartMonth", 8).Should().Be("8");
    }
}
