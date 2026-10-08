using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class EmailLogLijst
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<EmailLogDto> Items { get; set; } = Array.Empty<EmailLogDto>();

    private int? _openId;
    private bool _laden;
    private string? _fout;
    private EmailTraceDto? _trace;

    private string TraceKop => _trace is null
        ? ""
        : $"Aangemaakt {_trace.Aangemaakt.ToLocalTime():dd-MM-yyyy HH:mm} · versie {_trace.AppVersie}"
          + (string.IsNullOrEmpty(_trace.SjabloonSleutel) ? "" : $" · sjabloon {_trace.SjabloonSleutel}");

    private async Task WisselTraceAsync(int id)
    {
        if (_openId == id)
        {
            _openId = null;
            return;
        }

        _openId = id;
        _trace = null;
        _fout = null;
        _laden = true;

        var r = await Api.GetEmailTraceAsync(id);
        // Gebruiker kan intussen een andere regel hebben geopend: alleen het laatste antwoord telt.
        if (_openId != id) return;

        if (r.Success) _trace = r.Data;
        else _fout = r.ErrorMessage;
        _laden = false;
    }
}
