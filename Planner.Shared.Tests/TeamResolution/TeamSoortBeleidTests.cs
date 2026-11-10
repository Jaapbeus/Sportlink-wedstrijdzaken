using AwesomeAssertions;
using Planner.Shared.TeamResolution;
using Xunit;

namespace Planner.Shared.Tests.TeamResolution;

/// <summary>
/// Gevallen van <see cref="TeamSoortBeleid"/> (#1785): een canoniek team dat in Sportlink alleen
/// als lokaal voorkomt, is standaard inactief. Beide databasetiers gebruiken deze ene regel.
/// Teamsoorten per groep staan als '|'-gescheiden tekst in de testdata.
/// </summary>
public class TeamSoortBeleidTests
{
    [Theory]
    [InlineData("lokaal")]
    [InlineData("lokaal|lokaal")]
    [InlineData("LOKAAL| Lokaal ")]
    public void IsActief_AlleenLokaal_IsInactief(string soorten)
        => TeamSoortBeleid.IsActief(soorten.Split('|')).Should().BeFalse();

    [Theory]
    [InlineData("bond")]
    [InlineData("lokaal|bond")]
    [InlineData("bond|lokaal")]
    public void IsActief_MetMinstensEenBondsrij_IsActief(string soorten)
        => TeamSoortBeleid.IsActief(soorten.Split('|')).Should().BeTrue();

    [Fact]
    public void IsActief_AndereTeamsoorten_BlijvenActief()
    {
        // De democlub levert 'jeugd'/'senioren'; alleen expliciet 'lokaal' telt.
        TeamSoortBeleid.IsActief(["jeugd"]).Should().BeTrue();
        TeamSoortBeleid.IsActief(["senioren", "lokaal"]).Should().BeTrue();
    }

    [Fact]
    public void IsActief_OntbrekendeTeamsoort_TeltNietAlsLokaal()
    {
        TeamSoortBeleid.IsActief([null]).Should().BeTrue();
        TeamSoortBeleid.IsActief(["lokaal", null]).Should().BeTrue();
        TeamSoortBeleid.IsActief([]).Should().BeTrue();
    }

    [Fact]
    public void KiesCanoniek_BondsrijWintEnBepaaltActief()
    {
        var (gekozen, actief) = TeamSoortBeleid.KiesCanoniek(
            new[] { ("JO10-1", "lokaal"), ("[club] O10-1", "bond") }, r => r.Item2);

        gekozen.Item1.Should().Be("[club] O10-1", "de bondsnotatie heeft voorkeur als weergavenaam");
        actief.Should().BeTrue();
    }

    [Fact]
    public void KiesCanoniek_AlleenLokaal_EersteRijEnInactief()
    {
        var (gekozen, actief) = TeamSoortBeleid.KiesCanoniek(
            new[] { ("JO14-1", "lokaal") }, r => r.Item2);

        gekozen.Item1.Should().Be("JO14-1");
        actief.Should().BeFalse("een alleen-lokaal team is standaard inactief (#1785)");
    }

    [Theory]
    [InlineData("bond", true)]
    [InlineData(" BOND ", true)]
    [InlineData("lokaal", false)]
    [InlineData(null, false)]
    public void IsBond_HerkentBondsteamOngeachtSchrijfwijze(string? soort, bool verwacht)
        => TeamSoortBeleid.IsBond(soort).Should().Be(verwacht);
}
