using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class EmailVerwerkingInstellingen
{
    [Parameter, EditorRequired] public AppSettingsDto Settings { get; set; } = default!;
    [Parameter] public bool ToonBevestiging { get; set; }
    [Parameter] public EventCallback Bevestig { get; set; }
    [Parameter] public EventCallback Annuleer { get; set; }
}
