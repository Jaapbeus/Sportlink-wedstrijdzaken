using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace BlazorAdmin.Services;

// Lokale ontwikkeling: AuthenticationState geeft altijd een ingelogde admin terug.
// Productie: MSAL (WebAssemblyAuthenticationStateProvider) via AddMsalAuthentication.
public class AlwaysAuthenticatedStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState _state = new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "LocalDev"), new Claim("roles", "admin")],
            authenticationType: "LocalDev")));

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(_state);
}
