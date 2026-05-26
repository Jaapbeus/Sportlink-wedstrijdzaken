using BlazorAdmin.Models;
using Microsoft.JSInterop;

namespace BlazorAdmin.Services;

/// <summary>
/// Laadt het club-thema vanuit de API en past CSS-variabelen + favicon toe via JSInterop. v2 — #325/#339.
/// </summary>
public class ThemeService
{
    private readonly AdminApiClient _api;
    private readonly IJSRuntime _js;

    public string? LogoUrl    { get; private set; }
    public string? FaviconUrl { get; private set; }

    public event Action? OnThemeChanged;

    public ThemeService(AdminApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    public async Task LoadAndApplyAsync()
    {
        var result = await _api.GetThemeAsync();
        if (result.Success && result.Data != null)
            await ApplyAsync(result.Data);
    }

    public async Task ApplyAsync(ThemeDto theme)
    {
        LogoUrl    = theme.LogoUrl;
        FaviconUrl = theme.FaviconUrl;

        try
        {
            await _js.InvokeVoidAsync("themeHelper.apply",
                theme.Primary,
                theme.Secondary,
                theme.Accent,
                theme.TextOnPrimary);

            if (!string.IsNullOrWhiteSpace(theme.FaviconUrl))
                await _js.InvokeVoidAsync("themeHelper.setFavicon", theme.FaviconUrl);
        }
        catch
        {
            // JSInterop kan falen als WASM nog niet volledig geladen is — stilzwijgend negeren
        }

        OnThemeChanged?.Invoke();
    }
}
