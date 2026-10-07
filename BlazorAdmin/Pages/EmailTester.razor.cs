using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorAdmin.Pages;

public partial class EmailTester
{
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private TestEmailRequest request = new()
    {
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries"
    };
    private TestEmailResponse? response;
    private string? error;
    private bool busy;
    private bool gekopieerd;

    private string RuweEmailTekst =>
        $"Van: {request.AfzenderNaam} <{request.Afzender}>\n" +
        $"Onderwerp: {request.Onderwerp}\n\n" +
        $"{request.Body}";

    private IReadOnlyList<TraceVergelijkingRij>? vergelijking;
    private bool heeftGeleerd;

    private string EindoordeelKlasse => TesterEindoordeelWeergave.AlertKlasse(response?.Eindoordeel);

    private string VoorbeeldKop => TesterEindoordeelWeergave.VoorbeeldKop(response?.Eindoordeel);

    private string OpnieuwKnopKlasse => heeftGeleerd ? "btn btn-primary" : "btn btn-outline-primary";

    private async Task TestAsync()
    {
        vergelijking = null;
        heeftGeleerd = false;
        await DraaiAsync();
    }

    /// <summary>
    /// Draait dezelfde invoer opnieuw (#1568 deel C) en zet het vorige resultaat naast het nieuwe. Een mislukte
    /// run laat de vergelijking leeg in plaats van het vorige resultaat te wissen.
    /// </summary>
    private async Task OpnieuwBeoordelenAsync()
    {
        var vorige = response?.Trace;
        var oud = response;
        await DraaiAsync();
        if (response is null) response = oud;
        else vergelijking = TraceVergelijking.Maak(vorige, response.Trace);
        heeftGeleerd = false;
    }

    private Task OnGeleerdAsync()
    {
        heeftGeleerd = true;
        return Task.CompletedTask;
    }

    private async Task DraaiAsync()
    {
        busy = true;
        error = null;
        response = null;

        var r = await Api.TestEmailAsync(request);
        if (r.Success) response = r.Data;
        else error = r.ErrorMessage;

        busy = false;
    }

    private async Task KopieerNaarKlembordAsync(string tekst)
    {
        await JS.InvokeVoidAsync("blazorHelpers.copyToClipboard", tekst);
        gekopieerd = true;
        StateHasChanged();
        await Task.Delay(2000);
        gekopieerd = false;
        StateHasChanged();
    }
}
