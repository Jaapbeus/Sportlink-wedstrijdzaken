namespace BlazorAdmin.Services;

/// <summary>
/// Eenvoudige auth-abstractie. Lokaal: altijd ingelogd als admin (LocalAuthService).
/// Productie: echte Entra ID via MSAL (EntraAuthService).
/// </summary>
public interface IAuthService
{
    bool IsAuthenticated { get; }
    string UserName { get; }
    bool IsAdmin { get; }

    Task LoginAsync();
    Task LogoutAsync();
}
