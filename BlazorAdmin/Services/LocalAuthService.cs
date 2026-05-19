namespace BlazorAdmin.Services;

/// <summary>
/// Lokale ontwikkel-implementatie: altijd ingelogd als admin.
/// In Production wordt deze service VERVANGEN door EntraAuthService.
/// Zie docs/v2-admin-handleiding.md voor productie Entra ID setup.
/// </summary>
public class LocalAuthService : IAuthService
{
    public bool IsAuthenticated => true;
    public string UserName => "LocalDev";
    public bool IsAdmin => true;

    public Task LoginAsync() => Task.CompletedTask;
    public Task LogoutAsync() => Task.CompletedTask;
}
