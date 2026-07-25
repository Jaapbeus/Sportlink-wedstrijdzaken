using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Tests voor de ClubCode-scope (#573, #580, #578).
///
/// De veiligheidseigenschap die hier vastligt: een query komt nooit ongescoped uit de
/// resolver. Een lege of ontbrekende ClubCode schakelt het filter effectief uit en zou
/// club-overstijgende data toelaten — dat moet luid falen, niet stil doorgaan.
/// </summary>
public class ClubScopeTests
{
    [Fact]
    public void Resolve_ExpliciteClubCode_WordtOvergenomen()
    {
        ClubScope.Resolve("ALLSTARS").Should().Be("ALLSTARS");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ZonderClubCode_FaaltExpliciet_AlsInstellingOntbreekt(string? clubCode)
    {
        // In de testcontext zijn dbo.AppSettings niet geladen, dus er is geen primaire club.
        // Verwacht gedrag: exception i.p.v. een lege string die het ClubCode-filter uitschakelt.
        var act = () => ClubScope.Resolve(clubCode);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*clubCode*");
    }

    [Fact]
    public void HisFilter_IsNullTolerantOpDePrimaireClub()
    {
        // his.matches.ClubCode is nullable (migratie 001). Niet-gestempelde rijen horen bij de
        // primaire club; zonder die tolerantie zouden legacy-wedstrijden uit de bezetting vallen.
        var filter = ClubScope.HisFilter("m");

        filter.Should().Be("ISNULL(m.[ClubCode], @primaireClubCode) = @clubCode");
    }

    [Fact]
    public void HisFilter_GebruiktDeMeegegevenAlias()
    {
        ClubScope.HisFilter("t").Should().StartWith("ISNULL(t.[ClubCode]");
    }

    [Fact]
    public void ParameterNamen_ZijnStabiel()
    {
        // De SQL-strings in de repositories interpoleren deze constanten; wijzigen breekt queries.
        ClubScope.ClubCodeParam.Should().Be("@clubCode");
        ClubScope.PrimaryClubCodeParam.Should().Be("@primaireClubCode");
    }
}
