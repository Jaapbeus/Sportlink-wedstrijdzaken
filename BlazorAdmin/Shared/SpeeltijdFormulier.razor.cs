using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>
/// Bewerk-/aanmaakformulier voor één speeltijdcategorie. Eén definitie, zodat de pagina het
/// zowel direct onder de bewerkte regel als bovenaan de tabel (nieuwe categorie) kan tonen (#1543).
/// </summary>
public partial class SpeeltijdFormulier
{
    [Parameter, EditorRequired] public SpeeltijdDto Model { get; set; } = default!;
    [Parameter] public bool IsNew { get; set; }
    [Parameter] public string? SaveError { get; set; }
    [Parameter] public EventCallback OnOpslaan { get; set; }
    [Parameter] public EventCallback OnAnnuleer { get; set; }

    private void ZetVoorkeurTijd(string? v) =>
        Model.StandaardVoorkeurTijd = string.IsNullOrWhiteSpace(v) ? null : v;
}
