using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class SyncLogboek
{
    [Parameter] public SyncStatusDto? Status { get; set; }
}
