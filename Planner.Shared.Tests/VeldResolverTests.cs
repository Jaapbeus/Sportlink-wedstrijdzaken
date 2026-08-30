using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Regressietests voor de tier-agnostische veldresolutie (#819) — dezelfde gevallen als de
/// oorspronkelijke #707/#719-fix, nu tegen de verhuisde implementatie.
/// </summary>
public class VeldResolverTests
{
    private static readonly (string? VeldNaam, int VeldNummer)[] Velden =
    [
        ("Veld 1", 1),
        ("Veld 10", 10),
        ("Hoofdveld", 2),
    ];

    [Fact]
    public void ExacteVeldnaam_MatchtZonderSubpositie()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("Veld 1", Velden);
        veldNummer.Should().Be(1);
        subpositie.Should().BeNull();
    }

    [Fact]
    public void VeldnaamMetSubpositie_MatchtEnSplitst()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("Veld 1 A", Velden);
        veldNummer.Should().Be(1);
        subpositie.Should().Be("A");
    }

    /// <summary>De regressie die #719 vaststelde: "veld 10" mag nooit op "veld 1" matchen.</summary>
    [Fact]
    public void Veld10_MatchtNietOpVeld1()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("Veld 10", Velden);
        veldNummer.Should().Be(10);
        subpositie.Should().BeNull();
    }

    [Fact]
    public void Veld10MetSubpositie_MatchtOpVeld10NietOpVeld1()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("Veld 10 B", Velden);
        veldNummer.Should().Be(10);
        subpositie.Should().Be("B");
    }

    /// <summary>Een veldnaam langer dan zes tekens ("hoofdveld") mag niet wegvallen (#719).</summary>
    [Fact]
    public void LangeVeldnaam_MatchtVolledig()
    {
        var (veldNummer, _) = VeldResolver.Resolve("Hoofdveld", Velden);
        veldNummer.Should().Be(2);
    }

    [Fact]
    public void OnbekendVeld_GeeftNulTerug()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("Veld 99", Velden);
        veldNummer.Should().Be(0);
        subpositie.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LegeOfOntbrekendeVeldstring_GeeftNulTerug(string? veld)
    {
        var (veldNummer, _) = VeldResolver.Resolve(veld, Velden);
        veldNummer.Should().Be(0);
    }

    [Fact]
    public void Matching_IsHoofdletterongevoelig()
    {
        var (veldNummer, subpositie) = VeldResolver.Resolve("veld 1 a", Velden);
        veldNummer.Should().Be(1);
        subpositie.Should().Be("A");
    }

    [Fact]
    public void DubbeleSpaties_WordenGenormaliseerd()
    {
        var (veldNummer, _) = VeldResolver.Resolve("Veld  1", Velden);
        veldNummer.Should().Be(1);
    }
}
