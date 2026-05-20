using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;

namespace BlazorAdmin.Services;

/// <summary>
/// Custom user factory die de Entra ID 'roles' claim correct uitpakt.
///
/// PROBLEEM:
/// De Blazor WebAssembly authenticatie-stack cast role claims uit een JSON-array
/// (zoals Entra die levert: <c>"roles": ["admin"]</c>) naar één claim met de
/// hele JSON-string als value (bv. <c>'["admin"]'</c>). Daardoor faalt
/// <see cref="ClaimsPrincipal.IsInRole"/> ook al staat de rol in het token.
///
/// OPLOSSING:
/// Deze factory leest de role-claim uit <c>account.AdditionalProperties</c>
/// (de raw JSON-vorm), splitst hem in losse claims per rol, en vervangt de
/// originele JSON-string claim.
///
/// Bron: https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/troubleshoot-rabc-issues-webassembly-auth-apps
/// Registreren in Program.cs:  .AddAccountClaimsPrincipalFactory&lt;CustomUserFactory&gt;()
/// </summary>
public class CustomUserFactory : AccountClaimsPrincipalFactory<RemoteUserAccount>
{
    public CustomUserFactory(IAccessTokenProviderAccessor accessor)
        : base(accessor)
    {
    }

    public override async ValueTask<ClaimsPrincipal> CreateUserAsync(
        RemoteUserAccount account,
        RemoteAuthenticationUserOptions options)
    {
        var user = await base.CreateUserAsync(account, options);

        if (user.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return user;

        var roleClaimType = options.RoleClaim ?? "roles";

        // Verwijder de bestaande role-claim(s) — die hebben mogelijk de JSON-string als value.
        var existing = identity.FindAll(roleClaimType).ToArray();
        foreach (var claim in existing)
        {
            identity.RemoveClaim(claim);
        }

        // Lees de raw role-data uit AdditionalProperties (zoals door MSAL geleverd).
        if (account.AdditionalProperties.TryGetValue(roleClaimType, out var rolesValue) &&
            rolesValue is JsonElement rolesElement)
        {
            switch (rolesElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var role in rolesElement.EnumerateArray())
                    {
                        var v = role.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            identity.AddClaim(new Claim(roleClaimType, v));
                    }
                    break;
                case JsonValueKind.String:
                    var single = rolesElement.GetString();
                    if (!string.IsNullOrWhiteSpace(single))
                        identity.AddClaim(new Claim(roleClaimType, single));
                    break;
            }
        }

        return user;
    }
}
