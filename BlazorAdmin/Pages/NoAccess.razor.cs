using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace BlazorAdmin.Pages;

public partial class NoAccess
{
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    [Inject] private NavigationManager NavManager { get; set; } = default!;

    private string _userName = "";

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            var state = await AuthState;
            _userName = state.User.Identity?.Name ?? "";
        }
    }

    private void LogoutAsync()
    {
        NavManager.NavigateToLogout("authentication/logout");
    }
}
