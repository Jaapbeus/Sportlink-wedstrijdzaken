using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Speeltijden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<SpeeltijdDto> _items = new();
    private readonly SpeeltijdBewerking _bewerking = new();
    private bool _loading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        await LaadAsync();
    }

    protected override Task OnClubChangedAsync() => LaadAsync();

    /// <param name="toonLaden">
    /// Na opslaan <c>false</c>: de tabel blijft staan, zodat een intussen geopend ander formulier
    /// (en wat daarin getypt is) niet verdwijnt (#1552).
    /// </param>
    private async Task LaadAsync(bool toonLaden = true)
    {
        if (toonLaden) _loading = true;
        _error = null;
        var result = await Api.GetSpeeltijdenAsync();
        if (result.Success)
            _items = result.Data ?? new();
        else
            _error = result.ErrorMessage ?? "Ophalen mislukt";
        _loading = false;
    }

    private void StartNew() => _bewerking.StartNieuw();

    private void Edit(SpeeltijdDto s) => _bewerking.StartBewerken(s);

    private void Annuleer() => _bewerking.Annuleer();

    private async Task OpslaanAsync()
    {
        var opgeslagen = await _bewerking.OpslaanAsync((model, isNieuw) => isNieuw
            ? Api.CreateSpeeltijdAsync(model)
            : Api.UpdateSpeeltijdAsync(model.Leeftijd, model));
        if (opgeslagen)
            await LaadAsync(toonLaden: false);
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
