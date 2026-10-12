using AwesomeAssertions;
using Planner.Shared.Autorisatie;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Autorisatie;

/// <summary>
/// #1390/#1647: de toegangsmatrix accepteert een <c>RolNaam</c>/<c>FeatureKey</c>-combinatie alleen als beide
/// bekend zijn — anders kan een tikfout stilzwijgend een dode rij aanmaken die nooit door <c>GetMatrixAsync</c>
/// wordt teruggegeven (fail-closed, dus onopgemerkt). Eén test voor beide databasetiers; tot #1647 stond hier
/// per tier een kopie van de validatie-tests.
/// </summary>
public class RolFeatureMatrixCoreTests
{
    [Fact]
    public void Valideer_OnbekendeRol_WordtAfgewezen() =>
        RolFeatureMatrixCore.Valideer("Penningmeester", MenuFeatureKeys.Dashboard).Should().NotBeNull();

    [Fact]
    public void Valideer_AdminAlsRol_WordtAfgewezen() =>
        // 'admin' is bewust geen instelbare rij — admin heeft altijd alles aan (#1341/#1390).
        RolFeatureMatrixCore.Valideer("admin", MenuFeatureKeys.Dashboard).Should().NotBeNull();

    [Fact]
    public void Valideer_OnbekendeFeatureKey_WordtAfgewezen() =>
        RolFeatureMatrixCore.Valideer(RolNamen.Sectiehoofd, "menu.onbestaand").Should().NotBeNull();

    [Fact]
    public void Valideer_OntbrekendeWaarden_WordtAfgewezen() =>
        RolFeatureMatrixCore.Valideer(null, null).Should().NotBeNull();

    [Theory]
    [InlineData(RolNamen.User)]
    [InlineData(RolNamen.Wedstrijdzaken)]
    [InlineData(RolNamen.Sectiehoofd)]
    [InlineData(RolNamen.Ledenadministratie)]
    public void Valideer_GeldigeRolMetMenuFeatureKey_WordtGeaccepteerd(string rolNaam) =>
        RolFeatureMatrixCore.Valideer(rolNaam, MenuFeatureKeys.Teambegeleiding).Should().BeNull();

    [Theory]
    [InlineData(SportlinkRolFeature.Kleedkamers)]
    [InlineData(SportlinkRolFeature.Scheidsrechter)]
    [InlineData(SportlinkRolFeature.Veld)]
    public void Valideer_GeldigeSportlinkFeatureKey_WordtGeaccepteerd(string featureKey) =>
        RolFeatureMatrixCore.Valideer(RolNamen.Wedstrijdzaken, featureKey).Should().BeNull();

    [Fact]
    public void Valideer_NieuweSleutelAppInstellingen_WordtGeaccepteerd() =>
        RolFeatureMatrixCore.Valideer(RolNamen.User, MenuFeatureKeys.InstellingenApp).Should().BeNull();

    [Fact]
    public async Task VerwerkZet_GeldigeBody_SchrijftEenmaalEnGeeftDeCelTerug()
    {
        var geschreven = new List<(string, string, bool)>();

        var (cel, fout) = await RolFeatureMatrixCore.VerwerkZetAsync(
            """{"RolNaam":"user","FeatureKey":"menu.planning","Enabled":true}""",
            (r, k, a) => { geschreven.Add((r, k, a)); return Task.CompletedTask; });

        fout.Should().BeNull();
        cel.Should().Be(new RolFeatureMatrixCore.CelResponse("user", "menu.planning", true));
        geschreven.Should().Equal(("user", "menu.planning", true));
    }

    [Theory]
    [InlineData("""{"RolNaam":"admin","FeatureKey":"menu.planning","Enabled":true}""")]
    [InlineData("""{"RolNaam":"user","FeatureKey":"menu.onbestaand","Enabled":true}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task VerwerkZet_OngeldigeBody_SchrijftNietsEnGeeftEenFout(string json)
    {
        var geschreven = 0;

        var (cel, fout) = await RolFeatureMatrixCore.VerwerkZetAsync(json, (_, _, _) => { geschreven++; return Task.CompletedTask; });

        cel.Should().BeNull();
        fout.Should().NotBeNullOrWhiteSpace();
        geschreven.Should().Be(0);
    }

    [Fact]
    public void NaarResponse_GeeftElkeCelMetDezelfdeWaarden()
    {
        var matrix = new Dictionary<(string RolNaam, string FeatureKey), bool> { [("user", "menu.planning")] = true };

        RolFeatureMatrixCore.NaarResponse(matrix).Should().Equal(new RolFeatureMatrixCore.CelResponse("user", "menu.planning", true));
    }
}
