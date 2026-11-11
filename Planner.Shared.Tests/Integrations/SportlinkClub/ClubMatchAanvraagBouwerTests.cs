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
        a.HomeTeam.Should().Be("35+4", "#1871: een clubteam gaat met Sportlinks eigen TeamName, zoals Sportlinks formulier");
        a.AwayTeam.Should().Be("TEST");
        a.MatchDate.Should().Be(new DateOnly(2026, 10, 4));
        a.StartTime.Should().Be(new TimeOnly(12, 0));
        a.AgeClassCode.Should().Be("001");
        a.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY");
        a.ExternalMatchId.Should().BeNull("het wedstrijdnummer komt uit de eigen teller en wordt pas vlak voor het versturen ingevuld (#1437)");
        a.FieldSize.Should().Be("1.0");
        a.FieldOffset.Should().Be("0");
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
    public void ZoekTeam_BondsnotatieMetClubwoord_VindtLokaleSportlinkNaam()
    {
        // #1737: formulier levert "[club] O14-2" (bond), Sportlink kent "JO14-2" (lokaal).
        var teams = new[] { new SportlinkClubTeam("TJO14", "JO14-2", "JO14-2 - Jongens", "SOCCER-VE-AL", "SATURDAY") };

        ClubMatchAanvraagBouwer.ZoekTeam(teams, "VRC O14-2")!.Id.Should().Be("TJO14");
        ClubMatchAanvraagBouwer.ZoekTeam(teams, "VRC O14-3").Should().BeNull();
    }

    [Fact]
    public void ZoekTeam_BondsnotatieMetMeerdereKandidaten_KiestNiets()
    {
        var teams = new[]
        {
            new SportlinkClubTeam("A", "JO14-2", null, "SOCCER-VE-AL", "SATURDAY"),
            new SportlinkClubTeam("B", "JO14-2", null, "SOCCER-VE-AL", "SATURDAY"),
        };

        ClubMatchAanvraagBouwer.ZoekTeam(teams, "VRC O14-2").Should().BeNull();
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
        r.Aanvraag.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId,
            "#1871: ook bij een vrije thuisteamnaam krijgt de tegenstander een eigen (sentinel-)ID");
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
    public void Bouw_ZonderWedstrijdnummerVanSportlink_IsGeenFoutMeer()
    {
        var context = Context() with { Defaults = Context().Defaults with { ExternalMatchId = null } };

        ClubMatchAanvraagBouwer.Bouw(Invoer(), context).Aanvraag.Should().NotBeNull();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("0.5")]
    [InlineData("0.25")]
    [InlineData("0.125")]
    public void Bouw_Velddeel_GaatAlsFieldSizeMeeMetOffsetNul(string velddeel)
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer() with { Velddeel = velddeel }, Context());

        r.Aanvraag!.FieldSize.Should().Be(velddeel);
        r.Aanvraag.FieldOffset.Should().Be("0");
    }

    [Theory]
    [InlineData("Veld - Zaterdag", "SOCCER-VE-AL/SATURDAY")]
    [InlineData("veld - zaterdag", "SOCCER-VE-AL/SATURDAY")]
    [InlineData("SOCCER-VE-AL/SATURDAY", "SOCCER-VE-AL/SATURDAY")]
    [InlineData("soccer-ve-al/saturday", "SOCCER-VE-AL/SATURDAY")]
    public void Bouw_SpelactiviteitInstelling_WintAltijdVanHetTeam(string instelling, string verwacht)
    {
        // 35+4 speelt zelf op vrijdag; de clubinstelling gaat voor.
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer() with { Spelactiviteit = instelling }, Context());

        r.Aanvraag!.SportIdTag.Should().Be(verwacht);
        r.Waarschuwingen.Should().NotContain(w => w.Contains("Spelactiviteit"));
    }

    [Fact]
    public void Bouw_SpelactiviteitInstellingLeeg_VolgtHetTeam()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer() with { Spelactiviteit = "  " }, Context());

        r.Aanvraag!.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY");
    }

    [Fact]
    public void Bouw_SpelactiviteitInstellingNietGevonden_ValtTerugMetWaarschuwing()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer() with { Spelactiviteit = "Zaal - Zondag" }, Context());

        r.Aanvraag!.SportIdTag.Should().Be("SOCCER-VE-AL/FRIDAY", "het eigen team");
        r.Waarschuwingen.Should().Contain(w => w.Contains("Spelactiviteit") && w.Contains("Zaal - Zondag"));
    }

    [Fact]
    public void Bouw_GekozenLeeftijdscategorie_WintVanDieVanHetTeam()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(leeftijd: "JO10") with { AgeClassCodeOverride = "215" }, Context());

        r.Aanvraag!.AgeClassCode.Should().Be("215");
    }

    [Fact]
    public void ZoekAgeClassId_MapptOnzeCategorieNaarHetSportlinkId()
    {
        var ageClasses = Context().AgeClasses;

        ClubMatchAanvraagBouwer.ZoekAgeClassId("JO10", ageClasses).Should().Be("110");
        ClubMatchAanvraagBouwer.ZoekAgeClassId("JO99", ageClasses).Should().BeNull();
        ClubMatchAanvraagBouwer.ZoekAgeClassId(null, ageClasses).Should().BeNull();
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

    // ── #1871: de tegenstander mag nooit het eigen team-ID krijgen ─────────────────────────────

    private static SportlinkClubMatchContext ContextMetStandaardIds(string? thuisId, string? uitId)
    {
        var c = Context();
        return c with { Defaults = c.Defaults with { PublicHomeTeamId = thuisId, PublicAwayTeamId = uitId } };
    }

    [Fact]
    public void Bouw_ClubteamTegenExterneTegenstander_UitIdIsSentinelEnNooitHetEigenTeam()
    {
        // #1871: vóór de fix ging het eigen T…-ID ook als PublicAwayTeamId mee; Sportlink toonde dan het
        // eigen team als uitteam en negeerde de tegenstandertekst.
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), Context());

        r.Fout.Should().BeNull();
        r.Aanvraag!.PublicTeamId.Should().Be("T354");
        r.Aanvraag.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        r.Aanvraag.PublicAwayTeamId.Should().NotBe(r.Aanvraag.PublicTeamId);
        r.Aanvraag.AwayTeam.Should().Be("TEST");
        r.Waarschuwingen.Should().BeEmpty();
    }

    [Fact]
    public void Bouw_LiveStandaardwaarden_ThuisClubteamUitSentinel()
    {
        // Live vorm (2026-11-11): ClubMatchDefaults geeft voor thuis én uit de sentinel.
        var context = ContextMetStandaardIds(SportlinkClubMatchAanvraag.GeenTeamId, SportlinkClubMatchAanvraag.GeenTeamId);

        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), context);

        r.Aanvraag!.PublicTeamId.Should().Be("T354");
        r.Aanvraag.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        r.Waarschuwingen.Should().BeEmpty();
    }

    [Fact]
    public void Bouw_StandaardUitteamIsEenClubteam_ValtTerugOpSentinelMetWaarschuwing()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "JO10-1", leeftijd: "JO10"), ContextMetStandaardIds(null, "T354"));

        r.Aanvraag!.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId,
            "een clubteam-ID als uitteam zou Sportlink dat team laten tonen in plaats van de tegenstander");
        r.Waarschuwingen.Should().Contain(w => w.Contains("standaard-uitteam"));
    }

    [Fact]
    public void Bouw_StandaardUitteamGelijkAanThuisId_ValtTerugOpSentinel()
    {
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), ContextMetStandaardIds(null, "T354"));

        r.Aanvraag!.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
    }

    [Fact]
    public void Bouw_StandaardUitteamGeenClubteam_VolgtSportlinksStandaard()
    {
        // Zoals Sportlinks eigen formulier: defaults.PublicAwayTeamId || sentinel.
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(), ContextMetStandaardIds(null, "T-EXTERN"));

        r.Aanvraag!.PublicAwayTeamId.Should().Be("T-EXTERN");
        r.Waarschuwingen.Should().BeEmpty();
    }

    [Fact]
    public void Bouw_VrijeTekstZonderStandaardteam_ThuisEnUitSentinelGeenFout()
    {
        // Vóór #1871 was een lege standaard een fout; Sportlinks formulier valt dan terug op de sentinel.
        var r = ClubMatchAanvraagBouwer.Bouw(Invoer(team: "TEST", vrijeTekst: true, leeftijd: null), ContextMetStandaardIds(null, null));

        r.Fout.Should().BeNull();
        r.Aanvraag!.PublicTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        r.Aanvraag.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        r.Aanvraag.HomeTeam.Should().Be("TEST");
    }
}
