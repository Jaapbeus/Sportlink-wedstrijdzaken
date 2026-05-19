using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Services;

/// <summary>
/// Productie-implementatie van IAuthService via Entra ID + MSAL.
/// Lokale dev gebruikt LocalAuthService (altijd admin, geen login nodig).
///
/// Zie docs/v2-admin-handleiding.md, sectie "Entra ID app-registratie" voor:
///  - Azure Portal → Entra ID → App registrations → New registration
///  - Redirect URI: https://&lt;swa-name&gt;.azurestaticapps.net/.auth/login/aad/callback
///  - API permissions + client secret
///  - Rollen: 'admin' en 'user'
/// </summary>
public class EntraAuthService : IAuthService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly NavigationManager _navManager;

    private bool _isAuthenticated;
    private string _userName = "";
    private bool _isAdmin;

    public EntraAuthService(
        AuthenticationStateProvider authStateProvider,
        NavigationManager navManager)
    {
        _authStateProvider = authStateProvider;
        _navManager = navManager;
        _ = RefreshAsync();
    }

    public bool IsAuthenticated => _isAuthenticated;
    public string UserName => _userName;
    public bool IsAdmin => _isAdmin;

    public Task LoginAsync()
    {
        _navManager.NavigateToLogin("authentication/login");
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        // .NET 10: SignOutSessionStateManager is deprecated; gebruik NavigateToLogout
        _navManager.NavigateToLogout("authentication/logout");
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        var state = await _authStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = state.User.Identity?.IsAuthenticated ?? false;
        _userName = state.User.Identity?.Name ?? "";
        // Admin-rol komt uit de "roles" claim in het ID-token (rolnaam in Entra: "admin")
        _isAdmin = state.User.IsInRole("admin");
    }
}
