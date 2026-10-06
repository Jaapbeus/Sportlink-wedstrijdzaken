using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Speeltijden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private readonly SpeeltijdBewerking _bewerking = new();
    private bool _loading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        _bewerking.Gewijzigd += StateHasChanged;
        await LaadAsync();
    }

    protected override Task OnClubChangedAsync() => LaadAsync();

    private async Task LaadAsync()
    {
        _loading = true;
        _error = null;
        var result = await _bewerking.LaadAsync(Api.GetSpeeltijdenAsync);
        if (!result.Success)
            _error = result.ErrorMessage ?? "Ophalen mislukt";
        _loading = false;
    }

    private void StartNew() => _bewerking.StartNieuw();

    private void Edit(SpeeltijdDto s) => _bewerking.StartBewerken(s);

    private void Annuleer() => _bewerking.Annuleer();

    /// <summary>
    /// Opslaan én de verversing erna lopen in <see cref="SpeeltijdBewerking"/>: die houdt de regel
    /// geblokkeerd tot de verse lijst er is en vangt een mislukte verversing op zonder laadscherm,
    /// zodat een intussen geopend ander formulier blijft staan (#1552).
    /// </summary>
    private Task OpslaanAsync() => _bewerking.OpslaanAsync(
        (model, isNieuw) => isNieuw
            ? Api.CreateSpeeltijdAsync(model)
            : Api.UpdateSpeeltijdAsync(model.Leeftijd, model),
        Api.GetSpeeltijdenAsync);

    private async Task DeleteAsync(string leeftijd)
    {
        var result = await Api.DeleteSpeeltijdAsync(leeftijd);
        if (result.Success)
            await LaadAsync();
        else
            _error = result.ErrorMessage ?? "Verwijderen mislukt";
    }
}
