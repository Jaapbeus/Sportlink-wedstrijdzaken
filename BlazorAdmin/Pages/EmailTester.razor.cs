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

    private bool TraceIsZeker => response?.Trace?.Oordeel?.IsZeker ?? false;

    private string ZekerheidSamenvatting => TraceIsZeker
        ? "Zou automatisch verstuurd worden"
        : "Zou in review gaan";

    private string ZekerheidAlertKlasse => TraceIsZeker ? "alert alert-success" : "alert alert-warning";

    private static string StapKlasse(TraceStapDto stap) => stap.Zekerheid switch
    {
        "Mislukt" => "list-group-item d-flex list-group-item-danger",
        "Onzeker" => "list-group-item d-flex list-group-item-warning",
        _ => "list-group-item d-flex"
    };

    private static string ZekerheidBadgeKlasse(TraceStapDto stap) => stap.Zekerheid switch
    {
        "Mislukt" => "bg-danger",
        "Onzeker" => "bg-warning text-dark",
        _ => "bg-success"
    };

    /// <summary>Details als "sleutel: waarde"-regel; lege waarden en de al getoonde stapcode vallen weg.</summary>
    private static string StapDetails(TraceStapDto stap)
        => string.Join(" · ", stap.Details
            .Where(d => !string.IsNullOrWhiteSpace(d.Value))
            .Select(d => $"{d.Key}: {d.Value}"));

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
