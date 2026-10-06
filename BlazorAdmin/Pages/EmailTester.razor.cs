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

    private async Task TestAsync()
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
