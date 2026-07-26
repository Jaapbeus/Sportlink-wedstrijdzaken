using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Tests voor de pure helperlogica van de auto-planner (#578).
///
/// Deze helpers bepalen hoe een team uit Sportlink wordt vertaald naar een leeftijdscategorie,
/// in welke volgorde categorieën op een speeldag worden ingedeeld, en op welk standaardtijdstip
/// ze uitkomen als er geen voorkeurstijd is. Ze zijn puur (geen database, geen tijd, geen I/O)
/// en daarmee direct testbaar — in tegenstelling tot de omliggende services, die nog rechtstreeks
/// op statische data-access leunen.
///
/// De seniorenmapping hieronder is de regressiegrens van #591: "Heren" moet op de bestaande
/// Speeltijden-sleutel <c>1-99</c> uitkomen en "Dames"/"Vrouwen" op <c>VR</c>. Kwam die mapping
/// niet uit, dan kreeg elke seniorenwedstrijd de status "onbekend-team" in Optimaliseer/Auto-plan
/// (zie ook #581, bevinding 2).
/// </summary>
public class AutoPlanHelperTests
{
    // ── Leeftijdscategorie afleiden uit de teamnaam ──

    [Theory]
    [InlineData("AllStars Heren 1", "1-99")]
    [InlineData("AllStars Dames 1", "VR")]
    [InlineData("AllStars Vrouwen 2", "VR")]
    public void ExtractLeeftijd_SeniorenTeams_MappenOpBestaandeSpeeltijdenSleutels(
        string teamNaam, string verwacht)
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam(teamNaam).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("AllStars JO17-1", "JO17")]
    [InlineData("AllStars MO13-2", "MO13")]
    [InlineData("AllStars JO9-4", "JO9")]
    public void ExtractLeeftijd_JeugdTeams_StrippenHetElftalnummer(string teamNaam, string verwacht)
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam(teamNaam).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("AllStars HEREN 1", "1-99")]
    [InlineData("AllStars heren 1", "1-99")]
    [InlineData("AllStars Dames 1", "VR")]
    public void ExtractLeeftijd_SeniorenMappingIsHoofdletterOngevoelig(
        string teamNaam, string verwacht)
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam(teamNaam).Should().Be(verwacht);
    }

    /// <summary>
    /// Alleen de seniorencategorieën worden naar een vaste sleutel herschreven. Jeugdcategorieën
    /// worden ongewijzigd doorgegeven, dus met de schrijfwijze die Sportlink aanlevert. Dat is
    /// veilig omdat de Speeltijden-lookup een <c>StringComparer.OrdinalIgnoreCase</c>-dictionary
    /// is (zie <c>PlannerSettingsRepository.GetSpeeltijdenLookupAsync</c>) — zou die ooit
    /// hoofdlettergevoelig worden, dan valt deze aanname om.
    /// </summary>
    [Fact]
    public void ExtractLeeftijd_JeugdBehoudtDeSchrijfwijzeVanSportlink()
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam("AllStars jo17-1").Should().Be("jo17");
        AutoPlanService.ExtractLeeftijdFromTeamNaam("AllStars JO17-1").Should().Be("JO17");
    }

    /// <summary>
    /// De teamnaam wordt verwacht in Sportlink-formaat "&lt;club&gt; &lt;categorie&gt; &lt;nummer&gt;" —
    /// het tweede woord is de categorie. Een naam zónder clubprefix ("Heren 1") levert dus het
    /// elftalnummer op in plaats van de categorie. Dit is geen bug maar wel een aanname die
    /// stilzwijgend omvalt als Sportlink het naamformaat wijzigt; dit test legt hem vast.
    /// </summary>
    [Fact]
    public void ExtractLeeftijd_VerwachtDeClubnaamAlsEersteWoord()
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam("Heren 1").Should().Be("1");
        AutoPlanService.ExtractLeeftijdFromTeamNaam("AllStars Heren 1").Should().Be("1-99");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Eennaamzonderspatie")]
    public void ExtractLeeftijd_OnbruikbareInvoer_GeeftNull(string? teamNaam)
    {
        AutoPlanService.ExtractLeeftijdFromTeamNaam(teamNaam).Should().BeNull();
    }

    // ── Sorteervolgorde van categorieën ──

    [Fact]
    public void SortOrder_JeugdLooptOplopendMetDeLeeftijd()
    {
        var jo9 = AutoPlanService.GetLeeftijdSortOrder("JO9");
        var jo13 = AutoPlanService.GetLeeftijdSortOrder("JO13");
        var jo19 = AutoPlanService.GetLeeftijdSortOrder("JO19");

        jo9.Should().BeLessThan(jo13);
        jo13.Should().BeLessThan(jo19);
    }

    [Fact]
    public void SortOrder_MeisjesKomenNaDeJongens()
    {
        AutoPlanService.GetLeeftijdSortOrder("MO13")
            .Should().BeGreaterThan(AutoPlanService.GetLeeftijdSortOrder("JO19"));
    }

    [Fact]
    public void SortOrder_OnbekendeCategorie_KomtAchteraan()
    {
        var onbekend = AutoPlanService.GetLeeftijdSortOrder("ietsonbekends");

        onbekend.Should().BeGreaterThan(AutoPlanService.GetLeeftijdSortOrder("JO19"));
        onbekend.Should().BeGreaterThan(AutoPlanService.GetLeeftijdSortOrder("VR"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SortOrder_LegeCategorie_KomtHelemaalAchteraan(string? leeftijd)
    {
        AutoPlanService.GetLeeftijdSortOrder(leeftijd).Should().Be(99);
    }

    // ── Standaard aanvangstijd per categorie ──

    [Fact]
    public void DefaultTijd_JongereTeamsSpelenVroegerDanOudere()
    {
        var jo9 = AutoPlanService.GetDefaultTimeSortKey("JO9");
        var jo15 = AutoPlanService.GetDefaultTimeSortKey("JO15");
        var senioren = AutoPlanService.GetDefaultTimeSortKey("1-99");

        jo9.Should().BeLessThan(jo15);
        jo15.Should().BeLessThan(senioren);
    }

    [Fact]
    public void DefaultTijd_IsEenGeldigTijdstipInMinutenSindsMiddernacht()
    {
        // 540 = 09:00, 780 = 13:00 — alle uitkomsten moeten binnen een speelbare dag vallen.
        foreach (var leeftijd in new[] { "JO9", "JO13", "JO17", "1-99", "VR", null })
        {
            var key = AutoPlanService.GetDefaultTimeSortKey(leeftijd);
            key.Should().BeInRange(0, 24 * 60);
        }
    }

    // ── Veldnaam-normalisatie ──

    [Theory]
    [InlineData("Veld 3", "veld 3")]
    [InlineData("  VELD 3  ", "veld 3")]
    [InlineData("veld  3", "veld 3")]
    public void NormaliseerVeld_MaaktVergelijkbaarOngeachtOpmaak(string invoer, string verwacht)
    {
        AutoPlanService.NormaliseerVeld(invoer).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormaliseerVeld_LegeInvoer_GeeftLegeString(string? invoer)
    {
        AutoPlanService.NormaliseerVeld(invoer).Should().BeEmpty();
    }

    // ── Sportlink-veldstring opbouwen ──

    [Fact]
    public void BuildVeldString_ZonderSubpositie_GeeftAlleenDeVeldnaam()
    {
        AutoPlanService.BuildSportlinkVeldString("veld 3", "").Should().Be("veld 3");
    }

    [Fact]
    public void BuildVeldString_MetSubpositie_PlaktDieErachter()
    {
        AutoPlanService.BuildSportlinkVeldString("veld 3", "A").Should().Be("veld 3 A");
    }

    [Fact]
    public void BuildVeldString_TrimtOverbodigeSpaties()
    {
        AutoPlanService.BuildSportlinkVeldString("  veld 3  ", "B").Should().Be("veld 3 B");
    }
}
