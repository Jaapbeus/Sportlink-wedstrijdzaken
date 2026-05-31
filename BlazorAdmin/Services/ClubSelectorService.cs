using Microsoft.JSInterop;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Bewaart de geselecteerde club-code en club-naam van de beheerder.
/// Slaat de keuze op in localStorage zodat hij na een pagina-refresh bewaard blijft.
/// </summary>
public class ClubSelectorService
{
    private readonly IJSRuntime _js;
    private string? _clubCode;
    private string? _clubName;
    private const string StorageKey = "selectedClubCode";

    public event Action? OnChange;

    public ClubSelectorService(IJSRuntime js)
    {
        _js = js;
    }

    public string? SelectedClubCode => _clubCode;
    public string? SelectedClubName => _clubName;

    public async Task InitializeAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored))
                _clubCode = stored;
        }
        catch
        {
            // localStorage niet beschikbaar (pre-render) — stilzwijgend negeren
        }
    }

    /// <summary>
    /// Selecteert een club. ClubName wordt opgeslagen zodat NavMenu altijd de juiste naam toont.
    /// </summary>
    public async Task SelectClubAsync(string clubCode, string? clubName = null)
    {
        _clubCode = clubCode;
        _clubName = clubName;
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, clubCode);
        }
        catch { }
        OnChange?.Invoke();
    }
}
