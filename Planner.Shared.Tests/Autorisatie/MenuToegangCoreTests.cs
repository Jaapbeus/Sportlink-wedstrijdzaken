using AwesomeAssertions;
using Xunit;
using Planner.Shared.Autorisatie;

namespace Planner.Shared.Tests.Autorisatie;

/// <summary>#1647: welke menu-sleutels een aanroeper mag zien volgens de toegangsmatrix.</summary>
public class MenuToegangCoreTests
{
    private static Task<IReadOnlyDictionary<(string RolNaam, string FeatureKey), bool>> Matrix(
        params (string Rol, string Key, bool Aan)[] cellen) =>
        Task.FromResult<IReadOnlyDictionary<(string, string), bool>>(
            cellen.ToDictionary(c => (c.Rol, c.Key), c => c.Aan));

    [Fact]
    public async Task Admin_KrijgtAlleSleutels_ZonderDeMatrixTeRaadplegen()
    {
        var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
            true, _ => false, () => throw new InvalidOperationException("matrix mag niet gelezen worden"));

        keys.Should().BeEquivalentTo(MenuFeatureKeys.Alle);
    }

    [Fact]
    public async Task NietAdmin_ZonderEnigeInstelbareRol_KrijgtNiets()
    {
        var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
            false, _ => false, () => throw new InvalidOperationException("geen rol, dus geen matrix nodig"));

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task User_KrijgtAlleenDeSleutelsDieAanStaan()
    {
        var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
            false, r => r == RolNamen.User,
            () => Matrix(
                (RolNamen.User, MenuFeatureKeys.Planning, true),
                (RolNamen.User, MenuFeatureKeys.Dashboard, false),
                (RolNamen.Wedstrijdzaken, MenuFeatureKeys.Teambegeleiding, true)));

        keys.Should().BeEquivalentTo(new[] { MenuFeatureKeys.Planning });
    }

    [Fact]
    public async Task MeerdereRollen_GeeftDeUnie()
    {
        var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
            false, r => r is RolNamen.User or RolNamen.Wedstrijdzaken,
            () => Matrix(
                (RolNamen.User, MenuFeatureKeys.Planning, true),
                (RolNamen.Wedstrijdzaken, MenuFeatureKeys.InstellingenSpeeltijden, true),
                (RolNamen.Sectiehoofd, MenuFeatureKeys.Teambegeleiding, true)));

        keys.Should().BeEquivalentTo(new[] { MenuFeatureKeys.Planning, MenuFeatureKeys.InstellingenSpeeltijden });
    }

    [Fact]
    public async Task DebugSleutels_WordenNooitAanEenNietAdminGegeven_AlStaatDeMatrixAan()
    {
        var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
            false, r => r == RolNamen.User,
            () => Matrix(
                (RolNamen.User, MenuFeatureKeys.EmailTester, true),
                (RolNamen.User, MenuFeatureKeys.Leermomenten, true)));

        keys.Should().BeEmpty();
    }

    [Fact]
    public void StandaardAan_BevatAlleenBestaandeSleutels_ZonderDebugOfBeheergroepen()
    {
        MenuFeatureKeys.Alle.Should().Contain(MenuFeatureKeys.StandaardAan);
        MenuFeatureKeys.StandaardAan.Should().NotContain(MenuFeatureKeys.AlleenAdmin);
        MenuFeatureKeys.StandaardAan.Should().NotContain(k => k.StartsWith("menu.instellingen."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bepaal_GeeftDeExtensievlagMee_OokVoorEenNietAdmin(bool extensie)
    {
        var toegang = await MenuToegangCore.BepaalAsync(
            false, r => r == RolNamen.User,
            () => Matrix((RolNamen.User, MenuFeatureKeys.WedstrijdAanmaken, true)),
            () => Task.FromResult(extensie));

        toegang.FeatureKeys.Should().Equal(MenuFeatureKeys.WedstrijdAanmaken);
        toegang.SportlinkExtensieActief.Should().Be(extensie);
    }

    [Fact]
    public async Task Bepaal_Admin_KrijgtAlleSleutelsEnDeVlag()
    {
        var toegang = await MenuToegangCore.BepaalAsync(
            true, _ => false, () => throw new InvalidOperationException(), () => Task.FromResult(true));

        toegang.FeatureKeys.Should().BeEquivalentTo(MenuFeatureKeys.Alle);
        toegang.SportlinkExtensieActief.Should().BeTrue();
    }
}
