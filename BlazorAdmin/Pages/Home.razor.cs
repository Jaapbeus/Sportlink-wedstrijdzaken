using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Home : IDisposable
{
    [Inject] private ThemeService ThemeService { get; set; } = default!;

    protected override void OnInitialized()
    {
        ThemeService.OnThemeChanged += OnThemeChanged;
    }

    public void Dispose() => ThemeService.OnThemeChanged -= OnThemeChanged;

    private void OnThemeChanged() => _ = InvokeAsync(StateHasChanged);
}
