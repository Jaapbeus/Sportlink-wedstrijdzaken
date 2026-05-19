using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace BlazorAdmin.Services;

// Lokale ontwikkeling: AuthenticationState geeft altijd een ingelogde admin terug.
// Productie: MSAL (WebAssemblyAuthenticationStateProvider) via AddMsalAuthentication.
//
// LET OP: roleType moet "roles" zijn (Entra-conventie), anders werkt IsInRole("admin") niet.
// Standaard zoekt ClaimsIdentity naar ClaimTypes.Role; Entra gebruikt "roles" als claim-type.
public class AlwaysAuthenticatedStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState _state = new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "LocalDev"), new Claim("roles", "admin")],
            authenticationType: "LocalDev",
            nameType: ClaimTypes.Name,
            roleType: "roles")));

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(_state);
}
