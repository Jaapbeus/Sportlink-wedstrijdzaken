using FluentAssertions;
using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1116: het oefenwedstrijd-formulier stuurt alleen teamnaam/tegenstander/veld; de server leidt de
/// Sportlink-velden af. Deze tests dekken de pure vertaalstappen — de databasekoppeling
/// (<c>SportlinkClubMatchRepository</c>) en de Sportlink-aanroep zelf blijven buiten beeld (die
/// laatste is bovendien code-gelockt, zie <c>SportlinkClubClientTests</c>).
/// </summary>
public class SportlinkClubMatchFunctionTests
{
    private static SportlinkClubMatchFunction.OefenwedstrijdAanmakenDto GeldigeInvoer() => new()
    {
        MatchDateTime = new DateTime(2026, 9, 20, 19, 30, 0),
        Duration = 90,
        TeamNaam = "JO10-1",
        Tegenstander = "SV Voorbeeld JO10-2",
        VeldNummer = 3
    };

    [Fact]
    public void Valideer_GeldigeInvoer_GeeftNull()
    {
        SportlinkClubMatchFunction.Valideer(GeldigeInvoer()).Should().BeNull();
    }

    [Fact]
    public void Valideer_LegeBody_VerplichtMatchDateTime()
    {
        SportlinkClubMatchFunction.Valideer(null).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Valideer_ZonderTeam_WordtAfgewezen(string? teamNaam)
    {
        var dto = GeldigeInvoer();
        dto.TeamNaam = teamNaam;

        SportlinkClubMatchFunction.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Valideer_ZonderTegenstander_WordtAfgewezen()
    {
        var dto = GeldigeInvoer();
        dto.Tegenstander = " ";

        SportlinkClubMatchFunction.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(241)]
    public void Valideer_OnrealistischeDuur_WordtAfgewezen(int duur)
    {
        var dto = GeldigeInvoer();
        dto.Duration = duur;

        SportlinkClubMatchFunction.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Valideer_ZonderDuur_ValtTerugOpDefault()
    {
        var dto = GeldigeInvoer();
        dto.Duration = null;

        SportlinkClubMatchFunction.Valideer(dto).Should().BeNull("Duration is optioneel; de functie vult 90 in");
    }

    [Fact]
    public void BouwOmschrijving_EigenTekst_WintAltijd()
    {
        SportlinkClubMatchFunction.BouwOmschrijving("  Oefenpot met scheids  ", "JO10-1", "SV Voorbeeld", "veld 3")
            .Should().Be("Oefenpot met scheids");
    }

    [Fact]
    public void BouwOmschrijving_ZonderEigenTekst_NoemtTeamTegenstanderEnVeld()
    {
        // Zolang FieldId niet wordt meegestuurd (dat pad loopt pas ná aanmaken via #993) is dit
        // de enige plek waar het gekozen veld in Sportlink Club leesbaar is.
        SportlinkClubMatchFunction.BouwOmschrijving(null, "JO10-1", " SV Voorbeeld JO10-2 ", "veld 3")
            .Should().Be("Oefenwedstrijd JO10-1 - SV Voorbeeld JO10-2 (veld 3)");
    }

    [Fact]
    public void BouwOmschrijving_ZonderVeld_LaatHaakjesWeg()
    {
        SportlinkClubMatchFunction.BouwOmschrijving("", "JO10-1", "SV Voorbeeld", null)
            .Should().Be("Oefenwedstrijd JO10-1 - SV Voorbeeld");
    }

    private static readonly SportlinkPickListItem[] Locaties =
    {
        new("F1", "Sportpark Noord"),
        new("F2", "Sportpark Zuid"),
        new("F3", "  Sportpark Oost  "),
        new(null, "Zonder id"),
        new("F5", null),
    };

    [Theory]
    [InlineData("Sportpark Noord", "F1")]
    [InlineData("sportpark noord", "F1")]
    [InlineData("  Sportpark Oost", "F3")]
    public void ZoekFacilityId_ExacteNaam_HoofdletterEnSpatieOngevoelig(string accommodatie, string verwacht)
    {
        SportlinkClubMatchFunction.ZoekFacilityId(Locaties, accommodatie).Should().Be(verwacht);
    }

    [Fact]
    public void ZoekFacilityId_UniekeGedeeltelijkeMatch_WordtGeaccepteerd()
    {
        // De instelling bevat vaak een langere naam ("Sportpark Zuid, Veldstraat 1") dan de picklist.
        SportlinkClubMatchFunction.ZoekFacilityId(Locaties, "Sportpark Zuid, Veldstraat 1").Should().Be("F2");
    }

    [Fact]
    public void ZoekFacilityId_MeerdereGedeeltelijkeMatches_GeeftNull()
    {
        // "Sportpark" past op drie locaties — liever leeg dan de verkeerde accommodatie.
        SportlinkClubMatchFunction.ZoekFacilityId(Locaties, "Sportpark").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Onbekend terrein")]
    public void ZoekFacilityId_GeenMatchOfLegeInstelling_GeeftNull(string? accommodatie)
    {
        SportlinkClubMatchFunction.ZoekFacilityId(Locaties, accommodatie).Should().BeNull();
    }

    [Fact]
    public void ZoekFacilityId_ItemsZonderIdOfNaam_WordenOvergeslagen()
    {
        SportlinkClubMatchFunction.ZoekFacilityId(Locaties, "Zonder id").Should().BeNull();
    }
}
