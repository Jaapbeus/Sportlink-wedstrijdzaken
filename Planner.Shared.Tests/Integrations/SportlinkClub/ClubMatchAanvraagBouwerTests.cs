using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// #1427: de vertaling van formulierinvoer naar de live bevestigde <c>ClubMatch</c>-body. De
/// context volgt de echte responsvorm van Sportlinks vier aanmaaklijsten; alle waarden zijn fictief.
/// </summary>
public class ClubMatchAanvraagBouwerTests
{
    private static SportlinkClubMatchContext Context(params SportlinkClubTeam[] extraTeams) => new(
        new SportlinkClubMatchDefaults(
            ExternalMatchId: 41, Description: "Oefenwedstrijd", PublicHomeTeamId: "T-STANDAARD",
            SportIdTag: "SOCCER-VE-AL/FRIDAY", IsHomeMatch: true, FacilityId: "F1", SubFacilityId: "F1-1",
            FieldSize: "1.0", FieldOffset: "0", AgeClassCode: "001"),
        new[]
        {
            new SportlinkClubTeam("T354", "35+4", "35+4 - Mannen", "SOCCER-VE-AL", "FRIDAY"),
            new SportlinkClubTeam("T1", "1", "1 - Mannen", "SOCCER-VE-AL", "SATURDAY"),
            new SportlinkClubTeam("TJO10", "JO10-1", "JO10-1 - Jongens", "SOCCER-VE-AL", "SATURDAY"),
            new SportlinkClubTeam("TG1", "G-1", "G-1 - Gemengd", "CLUBSPORT", "STANDARD"),
        }.Concat(extraTeams).ToList(),
        new[]
        {
            new SportlinkClubFacility("F1", "Sportpark Oost", IsDefault: true, new[]
            {
                new SportlinkClubField("F1-1", "veld 1", "veld 1"),
                new SportlinkClubField("F1-OUTDOOR_FIELD-6", "veld 5", "veld 5"),
            }),
        },
        new[]
        {
            new SportlinkClubActivity("SOCCER-VE-AL/FRIDAY", "Veld - Vrijdag"),
            new SportlinkClubActivity("SOCCER-VE-AL/SATURDAY", "Veld - Zaterdag"),
        },
        new[]
        {
            new SportlinkClubAgeClass("001", "Senioren (M)"),
            new SportlinkClubAgeClass("002", "Senioren Vrouwen (V)"),
            new SportlinkClubAgeClass("110", "Onder 10 (M)"),
            new SportlinkClubAgeClass("215", "Onder 15 Meiden (V)"),
        });

    private static ClubMatchInvoer Invoer(string team = "AllStars 35+4", bool vrijeTekst = false,
        string? leeftijd = "1-99", string? veld = "veld 1") =>
        new(new DateTime(2026, 10, 4, 12, 0, 0), 90, team, vrijeTekst, leeftijd, "TEST", veld,
            "Oefenwedstrijd AllStars 35+4 - TEST", "Sportpark Oost");

    [Fact]
    public void Bouw_TeamMetClubprefix_VindtSportlinkTeamEnVultAlleVeldenZoalsSportlinkZelf()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), Context());

        r.Fout.Should().BeNull();
        var a = r.Aanvraag!;
        a.PublicTeamId.Should().Be("T354", "'AllStars 35+4' eindigt op Sportlinks TeamName '35+4'");
        a.HomeTeam.Should().Be("AllStars 35+4");
        a.AwayTeam.Should().Be("TEST");
        a.MatchDate.Should().Be(new DateOnly(2026, 10, 4));
        a.StartTime.Should().Be(new TimeOnly(12, 0));
        a.AgeClassCode.Should().Be("001");
        a.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY");
        a.ExternalMatchId.Should().Be(41);
        a.FacilityId.Should().Be("F1");
        a.SubFacilityId.Should().Be("F1-1");
        a.IsHomeMatch.Should().BeTrue();
        r.Waarschuwingen.Should().BeEmpty();
    }

    [Fact]
    public void Bouw_ExacteTeamnaam_GaatVoorAchtervoegsel()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "JO10-1", leeftijd: "JO10"), Context());

        r.Aanvraag!.PublicTeamId.Should().Be("TJO10");
        r.Aanvraag.AgeClassCode.Should().Be("110");
        r.Aanvraag.SportIdTag.Should().Be("SOCCER-VE-AL/SATURDAY");
    }

    [Fact]
    public void Bouw_MeerdereTeamsMetZelfdeAchtervoegsel_KiestNiets()
    {
        // Regel van de teamresolutie: bij meerdere kandidaten wordt er niets gekozen.
        var context = Context(new SportlinkClubTeam("T354B", "35+4", "35+4 - dubbel", "SOCCER-VE-AL", "FRIDAY"));

        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), context);

        r.Aanvraag.Should().BeNull();
        r.Fout.Should().Contain("niet (eenduidig) gevonden");
    }

    [Fact]
    public void Bouw_VrijeTekst_GebruiktStandaardteamMetWaarschuwing()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "TEST", vrijeTekst: true, leeftijd: null), Context());

        r.Fout.Should().BeNull();
        r.Aanvraag!.PublicTeamId.Should().Be("T-STANDAARD");
        r.Aanvraag.HomeTeam.Should().Be("TEST");
        r.Aanvraag.AgeClassCode.Should().Be("001", "Sportlinks standaard");
        r.Aanvraag.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY", "Sportlinks standaard");
        r.Waarschuwingen.Should().Contain(w => w.Contains("standaardteam"));
    }

    [Fact]
    public void Bouw_DropdownTeamOnbekendBijSportlink_IsFout()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "AllStars Onbekend"), Context());

        r.Aanvraag.Should().BeNull();
        r.Fout.Should().Contain("AllStars Onbekend");
    }

    [Fact]
    public void Bouw_VeldOpNaam_GebruiktSubFacilityIdUitDeLijstNietUitHetNummer()
    {
        // Live: "veld 5" is …-OUTDOOR_FIELD-6 — het nummer is niet af te leiden.
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(veld: "Veld 5 "), Context());

        r.Aanvraag!.SubFacilityId.Should().Be("F1-OUTDOOR_FIELD-6");
    }

    [Fact]
    public void Bouw_OnbekendVeld_IsFout()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(veld: "veld 9"), Context());

        r.Aanvraag.Should().BeNull();
        r.Fout.Should().Contain("veld 9");
    }

    [Fact]
    public void Bouw_GeenVeld_GebruiktStandaardveldMetWaarschuwing()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(veld: null), Context());

        r.Aanvraag!.SubFacilityId.Should().Be("F1-1");
        r.Waarschuwingen.Should().Contain(w => w.Contains("standaardveld"));
    }

    [Fact]
    public void Bouw_AccommodatieNaamWijktAf_ValtTerugOpStandaardAccommodatie()
    {
        var invoer = Invoer() with { Accommodatie = "Een andere naam" };

        var r = ClubMatchAanvraagBouwer.Bouw(invoer, Context());

        r.Aanvraag!.FacilityId.Should().Be("F1", "IsDefault in Sportlinks locatielijst");
    }

    [Fact]
    public void Bouw_TeamZonderPassendeSpelactiviteit_ValtTerugMetWaarschuwing()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "G-1", leeftijd: null), Context());

        r.Aanvraag!.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY");
        r.Waarschuwingen.Should().Contain(w => w.Contains("Spelactiviteit"));
    }

    [Fact]
    public void Bouw_GeenWedstrijdnummerVanSportlink_IsFout()
    {
        var context = Context() with { Defaults = Context().Defaults with { ExternalMatchId = null } };

        ClubMatchAanvraagBouwer.Bouw(Invoer(), context).Fout.Should().Contain("wedstrijdnummer");
    }

    [Theory]
    [InlineData("1-99", "Senioren (M)")]
    [InlineData("VR", "Senioren Vrouwen (V)")]
    [InlineData("JO10", "Onder 10 (M)")]
    [InlineData("MO15", "Onder 15 Meiden (V)")]
    [InlineData("JO15 Meiden", "Onder 15 Meiden (V)")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AgeClassOmschrijving_VertaaltOnzeCategorieNaarSportlinksOmschrijving(string? onze, string? verwacht)
    {
        ClubMatchAanvraagBouwer.AgeClassOmschrijving(onze).Should().Be(verwacht);
    }
}
