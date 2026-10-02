using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>#1437: wedstrijdnummer uit de eigen teller, velddeel, leeftijdscategorie-keuze en het formulier-endpoint. Alle waarden zijn fictief.</summary>
public class ClubMatchEndpointCoreTests
{
    private static SportlinkClubMatchContext Context() => new(
        new SportlinkClubMatchDefaults(41, "Oefenwedstrijd", "T-STANDAARD", "SOCCER-VE-AL/FRIDAY", true, "F1", "F1-1", "1.0", "0", "001"),
        new[] { new SportlinkClubTeam("TJO10", "JO10-1", "JO10-1 - Jongens", "SOCCER-VE-AL", "SATURDAY") },
        new[] { new SportlinkClubFacility("F1", "Sportpark Oost", true, new[] { new SportlinkClubField("F1-1", "veld 1", "veld 1") }) },
        new[] { new SportlinkClubActivity("SOCCER-VE-AL/SATURDAY", "Veld - Zaterdag") },
        new[] { new SportlinkClubAgeClass("001", "Senioren (M)"), new SportlinkClubAgeClass("110", "Onder 10 (M)") });

    private static ISportlinkClubClient Client(SportlinkClubCallStatus status = SportlinkClubCallStatus.Ok)
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchContextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkClubMatchContext>(
                status, status == SportlinkClubCallStatus.Ok ? Context() : null, null, null));
        return mock.Object;
    }

    private static OefenwedstrijdAanmakenDto Dto() => new()
    {
        MatchDateTime = new DateTime(2026, 10, 2, 19, 0, 0),
        Duration = 60,
        TeamNaam = "ALLSTARS JO10-1",
        Tegenstander = "SV Voorbeeld",
    };

    private static ClubMatchTierGegevens Gegevens() => new("JO10-1", "JO10", null, "Sportpark Oost", null);

    [Fact]
    public async Task Bouw_GebruiktHetGereserveerdeVolgnummerAlsWedstrijdnummer()
    {
        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(), "rol", Dto(), Gegevens(),
            datum => Task.FromResult<int?>(datum == new DateOnly(2026, 10, 2) ? 7 : null), NullLogger.Instance);

        fout.Should().BeNull();
        aanvraag!.ExternalMatchId.Should().Be(26100207L);
    }

    [Fact]
    public async Task Bouw_DagVol_Geeft400MetNederlandseMelding()
    {
        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(), "rol", Dto(), Gegevens(),
            _ => Task.FromResult<int?>(null), NullLogger.Instance);

        aanvraag.Should().BeNull();
        fout.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("wedstrijdnummers");
    }

    [Fact]
    public async Task Bouw_ReserveertGeenNummerAlsDeBouwMislukt()
    {
        var reserveringen = 0;
        var dto = Dto();
        dto.TeamNaam = "Onbekend team";

        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(), "rol", dto, Gegevens() with { TeamNaam = "Onbekend team" },
            _ => { reserveringen++; return Task.FromResult<int?>(1); }, NullLogger.Instance);

        aanvraag.Should().BeNull();
        fout.Should().BeOfType<BadRequestObjectResult>();
        reserveringen.Should().Be(0, "een mislukte aanvraag mag geen wedstrijdnummer verbruiken");
    }

    [Fact]
    public async Task Bouw_SportlinkOnbereikbaar_ReserveertGeenNummer()
    {
        var reserveringen = 0;

        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(SportlinkClubCallStatus.RolNietGekoppeld), "rol", Dto(), Gegevens(),
            _ => { reserveringen++; return Task.FromResult<int?>(1); }, NullLogger.Instance);

        aanvraag.Should().BeNull();
        fout.Should().NotBeNull();
        reserveringen.Should().Be(0);
    }

    [Fact]
    public async Task Bouw_VelddeelEnGekozenLeeftijdscategorie_GaanMee()
    {
        var dto = Dto();
        dto.Velddeel = "0.25";
        dto.AgeClassCode = "001";

        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(), "rol", dto, Gegevens(), _ => Task.FromResult<int?>(1), NullLogger.Instance);

        fout.Should().BeNull();
        aanvraag!.FieldSize.Should().Be("0.25");
        aanvraag.FieldOffset.Should().Be("0");
        aanvraag.AgeClassCode.Should().Be("001", "de gekozen categorie wint van JO10 → 110");
    }

    [Fact]
    public async Task Bouw_LeeftijdscategorieDieNietInSportlinksLijstStaat_Geeft400()
    {
        var dto = Dto();
        dto.AgeClassCode = "999";

        var (aanvraag, fout, _) = await BouwAanvraagAsync(Client(), "rol", dto, Gegevens(), _ => Task.FromResult<int?>(1), NullLogger.Instance);

        aanvraag.Should().BeNull();
        fout.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Bouw_SpelactiviteitUitDeClubinstelling_Wint()
    {
        var (aanvraag, _, _) = await BouwAanvraagAsync(Client(), "rol", Dto(), Gegevens() with { Spelactiviteit = "veld - zaterdag" },
            _ => Task.FromResult<int?>(1), NullLogger.Instance);

        aanvraag!.SportIdTag.Should().Be("SOCCER-VE-AL/SATURDAY");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("0.5")]
    [InlineData("0.25")]
    [InlineData("0.125")]
    public void Valideer_AccepteertAlleenDeVierVelddelen(string? velddeel)
    {
        var dto = Dto();
        dto.Velddeel = velddeel;

        Valideer(dto).Should().BeNull();
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.75")]
    [InlineData("half")]
    public void Valideer_WeigertOnbekendVelddeel(string velddeel)
    {
        var dto = Dto();
        dto.Velddeel = velddeel;

        Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Formulier_VultPerTeamLeeftijdscategorieDuurEnVelddeel()
    {
        var teams = new[]
        {
            new ClubMatchFormulierTeamInvoer("JO10-1", "JO10"),
            new ClubMatchFormulierTeamInvoer("ALLSTARS 35+4", "1-99"),
            new ClubMatchFormulierTeamInvoer("Zonder categorie", null),
        };
        var speeltijden = new[]
        {
            new ClubMatchSpeeltijdInvoer("JO10", 0.5m, 60),
            new ClubMatchSpeeltijdInvoer("1-99", 1.0m, 90),
        };

        var formulier = await BouwFormulierAsync(Client(), "rol", teams, speeltijden, NullLogger.Instance);

        formulier.SportlinkBeschikbaar.Should().BeTrue();
        formulier.AgeClasses.Should().HaveCount(2);
        formulier.Teams.Should().Equal(new[]
        {
            new ClubMatchFormulierTeam("JO10-1", "JO10", "110", 60, "0.5"),
            new ClubMatchFormulierTeam("ALLSTARS 35+4", "1-99", "001", 90, "1.0"),
            new ClubMatchFormulierTeam("Zonder categorie", null, null, null, null),
        });
    }

    [Fact]
    public async Task Formulier_MeidenCategorieWordtGenormaliseerdVoorDeSpeeltijden()
    {
        var teams = new[] { new ClubMatchFormulierTeamInvoer("MO15-1", "JO15 Meiden") };
        var speeltijden = new[] { new ClubMatchSpeeltijdInvoer("MO15", 1.0m, 70) };

        var formulier = await BouwFormulierAsync(Client(), "rol", teams, speeltijden, NullLogger.Instance);

        formulier.Teams.Single().Duur.Should().Be(70);
    }

    [Fact]
    public async Task Formulier_SportlinkOnbereikbaar_GeeftTeamgegevensMetLegeLijstEnVlag()
    {
        var teams = new[] { new ClubMatchFormulierTeamInvoer("JO10-1", "JO10") };
        var speeltijden = new[] { new ClubMatchSpeeltijdInvoer("JO10", 0.5m, 60) };

        var formulier = await BouwFormulierAsync(Client(SportlinkClubCallStatus.RolNietGekoppeld), "rol", teams, speeltijden, NullLogger.Instance);

        formulier.SportlinkBeschikbaar.Should().BeFalse();
        formulier.AgeClasses.Should().BeEmpty();
        formulier.Teams.Should().ContainSingle().Which.Should().Be(new ClubMatchFormulierTeam("JO10-1", "JO10", null, 60, "0.5"));
    }

    [Fact]
    public async Task Formulier_ZonderClient_WerktOokZonderSportlink()
    {
        var formulier = await BouwFormulierAsync(null, "rol", new[] { new ClubMatchFormulierTeamInvoer("JO10-1", "JO10") },
            Array.Empty<ClubMatchSpeeltijdInvoer>(), NullLogger.Instance);

        formulier.SportlinkBeschikbaar.Should().BeFalse();
        formulier.Teams.Should().HaveCount(1);
    }
}
