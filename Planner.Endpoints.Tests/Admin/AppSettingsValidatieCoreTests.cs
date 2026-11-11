using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

/// <summary>#1459: de gedeelde validatie van <c>PUT beheer/settings</c> (voorheen per tier gekopieerd).</summary>
public class AppSettingsValidatieCoreTests
{
    private static IActionResult? Valideer(string veld, string? waarde) =>
        AppSettingsValidatieCore.Valideer(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [veld] = waarde });

    private static void IsFout(IActionResult? r, string bevat) =>
        r.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!.Should().Contain(bevat);

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ja", false)]
    public void PdfExportIngeschakeld_AlleenExpliciete_AanUitWaarde(string? waarde, bool geldig)
    {
        var uitkomst = Valideer("PdfExportIngeschakeld", waarde);

        if (geldig) uitkomst.Should().BeNull();
        else IsFout(uitkomst, "PdfExportIngeschakeld");
    }

    [Theory]
    [InlineData("0 0 4 * * *", true)]
    [InlineData("* * * * * *", true)]
    [InlineData("0 4 * * *", false)]
    [InlineData("0 0 0 31 2 *", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void FetchSchedule_VerwachtUitvoerbareZesVeldenCron(string? cron, bool geldig)
    {
        var uitkomst = Valideer("FetchSchedule", cron);

        if (geldig) uitkomst.Should().BeNull();
        else IsFout(uitkomst, "FetchSchedule");
    }

    [Fact]
    public void FetchSchedule_WeigertWaardeBovenKolombreedteZonderInvoerTeTonen()
    {
        var teLang = "0 0 4 * * *".PadRight(AppSettingsValidatieCore.MaxFetchScheduleLengte + 1);
        var fout = Valideer("FetchSchedule", teLang);

        IsFout(fout, "maximaal 50 tekens");
        fout.Should().BeOfType<BadRequestObjectResult>()
            .Subject.Value!.ToString()!.Should().NotContain(teLang);
    }

    [Fact]
    public void FetchSchedule_OntbrekendVeldBlijftOngewijzigd()
        => AppSettingsValidatieCore.Valideer(new Dictionary<string, string?>()).Should().BeNull();

    [Theory]
    [InlineData("West", true)]
    [InlineData("", true)]
    [InlineData("west", false)]
    [InlineData("Mars", false)]
    public void KnvbStandaardRegio_AlleenBekendeRegio(string regio, bool geldig)
    {
        var uitkomst = Valideer("KnvbStandaardRegio", regio);

        if (geldig) uitkomst.Should().BeNull();
        else IsFout(uitkomst, "KnvbStandaardRegio");
    }

    [Fact]
    public void Spelactiviteit_MaximaalHonderdTekens()
    {
        Valideer("SportlinkSpelactiviteit", new string('x', 100)).Should().BeNull();
        IsFout(Valideer("SportlinkSpelactiviteit", new string('x', 101)), "Spelactiviteit");
    }

    [Fact]
    public void VeldnaamIsHoofdletterOngevoelig_AlsDeTierDatZoAanlevert()
        => IsFout(Valideer("pdfexportingeschakeld", "ja"), "PdfExportIngeschakeld");

    [Theory]
    [InlineData("1", true)]
    [InlineData("7", true)]
    [InlineData("12", true)]
    [InlineData("0", false)]
    [InlineData("13", false)]
    [InlineData("juli", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SeasonStartMonth_AlleenMaandnummerEenTotTwaalf(string? waarde, bool geldig)
    {
        var uitkomst = Valideer("SeasonStartMonth", waarde);

        if (geldig) uitkomst.Should().BeNull();
        else IsFout(uitkomst, "SeasonStartMonth");
    }
}
