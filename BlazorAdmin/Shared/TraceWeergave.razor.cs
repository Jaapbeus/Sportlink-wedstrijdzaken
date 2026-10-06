using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class TraceWeergave
{
    /// <summary>De te tonen trace; <c>null</c> of leeg toont niets.</summary>
    [Parameter] public BeslissingsTraceDto? Trace { get; set; }

    /// <summary>
    /// <c>true</c> voor een proefrun (e-mailtester: "zou worden verstuurd"); <c>false</c> voor een
    /// werkelijk verwerkt bericht uit het e-maillog (vaststelling in de verleden tijd).
    /// </summary>
    [Parameter] public bool Proefrun { get; set; }

    private bool TraceIsZeker => Trace?.Oordeel?.IsZeker ?? false;

    private string ZekerheidSamenvatting => (Proefrun, TraceIsZeker) switch
    {
        (true, true) => "Zou automatisch verstuurd worden",
        (true, false) => "Zou in review gaan",
        (false, true) => "Eindoordeel: zeker",
        (false, false) => "Eindoordeel: onzeker, handmatige controle nodig"
    };

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

    /// <summary>Details als "sleutel: waarde"-regel; lege waarden vallen weg.</summary>
    private static string StapDetails(TraceStapDto stap)
        => string.Join(" · ", stap.Details
            .Where(d => !string.IsNullOrWhiteSpace(d.Value))
            .Select(d => $"{d.Key}: {d.Value}"));
}
