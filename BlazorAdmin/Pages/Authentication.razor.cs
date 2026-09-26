using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace BlazorAdmin.Pages;

public partial class Authentication
{
    [Inject] private NavigationManager NavManager { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;

    [Parameter] public string? Action { get; set; }

    private string? PostLogoutUrl => Config["PostLogoutRedirectUrl"];
    private bool _redirectScheduled;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Na een succesvolle logout-callback en als er een PostLogoutRedirectUrl is
        // geconfigureerd → na korte UX-pauze redirecten naar de clubwebsite.
        // URL komt uit appsettings.Production.json zodat dit per club configureerbaar is
        // (geen hardcoded club-strings in code — zie CLAUDE.md).
        if (firstRender
            && !_redirectScheduled
            && string.Equals(Action, "logout-callback", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(PostLogoutUrl))
        {
            _redirectScheduled = true;
            await Task.Delay(1500);
            NavManager.NavigateTo(PostLogoutUrl, forceLoad: true);
        }
    }
}
