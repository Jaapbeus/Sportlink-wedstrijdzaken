using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>RolFeatureInstellingen.razor</c> (#1341, epic #1338).</summary>
public partial class RolFeatureInstellingen : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private const string KleedkamersKey = "sportlink.kleedkamers";
    private const string ScheidsrechterKey = "sportlink.scheidsrechter";
    private const string VeldKey = "sportlink.veld";

    private Dictionary<string, bool> _instellingen = new();
    private string? _errorMessage;
    private string? _successMessage;
    private bool _laden = true;

    protected override async Task OnInitializedAsync() => await LaadAsync();

    protected override Task OnClubChangedAsync() => LaadAsync();

    private async Task LaadAsync()
    {
        _laden = true;
        _errorMessage = null;
        _successMessage = null;
        StateHasChanged();

        var r = await Api.GetRolFeatureInstellingenAsync();
        _instellingen = r.Success
            ? (r.Data ?? new()).ToDictionary(i => i.FeatureKey, i => i.Enabled)
            : new();
        if (!r.Success) _errorMessage = r.ErrorMessage;
        _laden = false;
    }

    private bool IsAan(string featureKey) => _instellingen.TryGetValue(featureKey, out var aan) && aan;

    private async Task ZetAsync(string featureKey, bool enabled)
    {
        _successMessage = null;
        _errorMessage = null;

        var r = await Api.ZetRolFeatureInstellingAsync(featureKey, enabled);
        if (r.Success)
        {
            _instellingen[featureKey] = enabled;
            _successMessage = "Instelling opgeslagen.";
        }
        else
        {
            _errorMessage = r.ErrorMessage;
        }
    }
}
