using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>Adreslijst met inline acties; de pagina beheert club, bevestiging en API-aanroepen.</summary>
public partial class UitgeslotenEmailTabel
{
    [Parameter, EditorRequired] public IReadOnlyList<UitgeslotenEmailAdresDto> Adressen { get; set; } = default!;
    [Parameter, EditorRequired] public Func<int, bool> BevestigingOpen { get; set; } = default!;
    [Parameter] public EventCallback<int> OnVraag { get; set; }
    [Parameter] public EventCallback<int> OnBevestig { get; set; }
    [Parameter] public EventCallback OnAnnuleer { get; set; }
}
