using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Speeltijden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<SpeeltijdDto> _items = new();
    private SpeeltijdDto? _editing;
    private bool _isNew;
    private bool _loading = true;
    private string? _error;
    private string? _saveError;

    protected override async Task OnInitializedAsync()
    {
        await LaadAsync();
    }

    protected override Task OnClubChangedAsync() => LaadAsync();

    private async Task LaadAsync()
    {
        _loading = true;
        _error = null;
        var result = await Api.GetSpeeltijdenAsync();
        if (result.Success)
            _items = result.Data ?? new();
        else
            _error = result.ErrorMessage ?? "Ophalen mislukt";
        _loading = false;
    }

    private void StartNew()
    {
        _editing = new SpeeltijdDto();
        _isNew = true;
        _saveError = null;
    }

    private void Edit(SpeeltijdDto s)
    {
        _editing = new SpeeltijdDto
        {
            Leeftijd = s.Leeftijd,
            Veldafmeting = s.Veldafmeting,
            WedstrijdTotaal = s.WedstrijdTotaal,
            WedstrijdHelft = s.WedstrijdHelft,
            WedstrijdRust = s.WedstrijdRust,
            StandaardVoorkeurTijd = s.StandaardVoorkeurTijd
        };
        _isNew = false;
        _saveError = null;
    }

    private void Annuleer()
    {
        _editing = null;
        _saveError = null;
    }

    private async Task OpslaanAsync()
    {
        if (_editing == null) return;
        _saveError = null;
        ApiResult<object> result;
        if (_isNew)
            result = await Api.CreateSpeeltijdAsync(_editing);
        else
            result = await Api.UpdateSpeeltijdAsync(_editing.Leeftijd, _editing);

        if (result.Success)
        {
            _editing = null;
            await LaadAsync();
        }
        else
        {
            _saveError = result.ErrorMessage ?? "Opslaan mislukt";
        }
    }

    private async Task DeleteAsync(string leeftijd)
    {
        var result = await Api.DeleteSpeeltijdAsync(leeftijd);
        if (result.Success)
            await LaadAsync();
        else
            _error = result.ErrorMessage ?? "Verwijderen mislukt";
    }
}
