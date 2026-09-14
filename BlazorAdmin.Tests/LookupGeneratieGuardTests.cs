using BlazorAdmin.Services;
using FluentAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Regressietests voor #1136: een trage, verouderde teamlookup mag het resultaat van een
/// snellere, latere lookup niet meer overschrijven in Teambegeleiding.razor.
/// </summary>
public class LookupGeneratieGuardTests
{
    [Fact]
    public void OutOfOrderVoltooiing_wordt_genegeerd()
    {
        var guard = new LookupGeneratieGuard();

        // Lookup A start (team A), daarna lookup B start (team B) vóórdat A voltooit —
        // exact het scenario uit de bugreproductie.
        var tokenA = guard.Start("JO10-1");
        var tokenB = guard.Start("JO11-1");

        // B voltooit eerst.
        guard.IsActueel(tokenB).Should().BeTrue("B is de nieuwste, nog niet overschreven lookup");

        // A voltooit daarna, out-of-order — dit resultaat hoort niet meer toegepast te worden.
        guard.IsActueel(tokenA).Should().BeFalse("A is inmiddels overschreven door de nieuwere selectie B");

        guard.ActueleSelectie.Should().Be("JO11-1");
    }

    [Fact]
    public void InOrderVoltooiing_met_actuele_selectie_wordt_toegepast()
    {
        var guard = new LookupGeneratieGuard();

        var token = guard.Start("JO10-1");

        // Geen nieuwere Start ertussen — de voltooiing hoort bij de actuele selectie.
        guard.IsActueel(token).Should().BeTrue();
        guard.ActueleSelectie.Should().Be("JO10-1");
    }

    [Fact]
    public void Leeg_geselecteerd_team_telt_ook_als_nieuwe_generatie()
    {
        var guard = new LookupGeneratieGuard();

        var tokenTeam = guard.Start("JO10-1");
        var tokenLeeg = guard.Start(null); // gebruiker kiest "— Kies een team —"

        guard.IsActueel(tokenTeam).Should().BeFalse();
        guard.IsActueel(tokenLeeg).Should().BeTrue();
        guard.ActueleSelectie.Should().BeNull();
    }
}
