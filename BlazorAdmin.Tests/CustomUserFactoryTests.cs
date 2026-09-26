using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// De stille variant van een laag-4-regressie (#1277).
/// </summary>
/// <remarks>
/// <para>
/// <c>AuthGateTests</c> bewijst dat de gate de juiste beslissing neemt <i>gegeven</i> een principal
/// met rollen. Dat is de helft van het verhaal. De andere helft is de manier waarop laag 4 al eens
/// is omgevallen zónder dat er iets zichtbaar veranderde: Blazor WASM cast een
/// <c>"roles": ["admin"]</c>-JSON-array naar één claim met de hele JSON-string als waarde
/// (<c>'["admin"]'</c>), waarna <c>IsInRole("admin")</c> <b>false</b> teruggeeft terwijl de rol
/// gewoon in het token staat. <see cref="CustomUserFactory"/> bestaat om dat uit te pakken.
/// </para>
/// <para>
/// Een grep op <c>App.razor</c> vangt dit niet: de check staat er dan nog, hij geeft alleen altijd
/// <c>false</c>. Vandaar deze tests op de factory zelf.
/// </para>
/// </remarks>
public class CustomUserFactoryTests
{
    /// <summary>De factory raakt de accessor niet aan in het pad dat hier getest wordt.</summary>
    private sealed class AccessorStub : IAccessTokenProviderAccessor
    {
        public IAccessTokenProvider TokenProvider => throw new NotSupportedException();
    }

    private static RemoteAuthenticationUserOptions Opties() => new()
    {
        // Exact wat Program.cs instelt: options.UserOptions.RoleClaim = "roles".
        RoleClaim = "roles",
        NameClaim = "name",
        // Zonder AuthenticationType bouwt de basisfactory een ANONIEME identity, en dan slaat
        // CustomUserFactory zijn uitpakwerk over (de !identity.IsAuthenticated-tak). In productie
        // vult MSAL dit; in een test moet het expliciet, anders test je de verkeerde tak.
        AuthenticationType = "TestEntra",
    };

    private static RemoteUserAccount Account(string rollenJson)
    {
        var account = new RemoteUserAccount
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["name"]  = "Testgebruiker",
                ["roles"] = JsonDocument.Parse(rollenJson).RootElement.Clone(),
            },
        };
        return account;
    }

    private static async Task<ClaimsPrincipal> MaakPrincipalAsync(string rollenJson)
    {
        var factory = new CustomUserFactory(new AccessorStub());
        return await factory.CreateUserAsync(Account(rollenJson), Opties());
    }

    [Fact]
    public async Task Een_JSON_array_met_een_rol_maakt_IsInRole_waar()
    {
        var user = await MaakPrincipalAsync("""["admin"]""");

        user.IsInRole("admin").Should().BeTrue(
            "zonder het uitpakken staat de hele JSON-string als één claimwaarde en faalt IsInRole stilzwijgend");
        AuthGate.Bepaal("https://example.invalid/", user).Should().Be(AppState.Authenticated);
    }

    [Fact]
    public async Task Meerdere_rollen_worden_losse_claims()
    {
        var user = await MaakPrincipalAsync("""["admin","Wedstrijdzaken"]""");

        user.IsInRole("admin").Should().BeTrue();
        user.IsInRole("Wedstrijdzaken").Should().BeTrue();
        user.FindAll("roles").Select(c => c.Value)
            .Should().BeEquivalentTo(["admin", "Wedstrijdzaken"]);
    }

    [Fact]
    public async Task De_ongesplitste_JSON_string_blijft_niet_achter_als_claimwaarde()
    {
        var user = await MaakPrincipalAsync("""["admin"]""");

        // Dit is de regressie zelf: bleef '["admin"]' als claimwaarde staan, dan is IsInRole("admin")
        // false terwijl er wél een rolclaim is — de vorm waarin laag 4 stil omvalt.
        user.FindAll("roles").Select(c => c.Value)
            .Should().NotContain(v => v.Contains('[') || v.Contains('"'));
    }

    [Fact]
    public async Task Een_enkele_rol_als_string_werkt_ook()
    {
        // Entra levert bij precies één rol soms een kale string in plaats van een array.
        var user = await MaakPrincipalAsync("\"user\"");

        user.IsInRole("user").Should().BeTrue();
        AuthGate.Bepaal("https://example.invalid/", user).Should().Be(AppState.Authenticated);
    }

    [Fact]
    public async Task Zonder_rolclaim_geeft_de_gate_geen_toegang()
    {
        var factory = new CustomUserFactory(new AccessorStub());
        var account = new RemoteUserAccount
        {
            AdditionalProperties = new Dictionary<string, object> { ["name"] = "Testgebruiker" },
        };

        var user = await factory.CreateUserAsync(account, Opties());

        user.IsInRole("admin").Should().BeFalse();
        user.IsInRole("user").Should().BeFalse();
        AuthGate.Bepaal("https://example.invalid/", user).Should().Be(AppState.AccessDenied);
    }
}
