using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class VeldEditor
{
    [Parameter, EditorRequired] public VeldDto Model { get; set; } = default!;
    [Parameter] public bool IsNew { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    private Task SaveAsync() => OnSave.InvokeAsync();
}
