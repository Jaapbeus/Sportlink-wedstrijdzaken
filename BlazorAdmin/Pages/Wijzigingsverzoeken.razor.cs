using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>Wijzigingsverzoeken.razor</c> (#996/#1111, code-behind sinds #1122).</summary>
public partial class Wijzigingsverzoeken
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private bool _laden = true;
    private string? _fout;
    private List<SportlinkChangeRequestDto>? _verzoeken;
    private string _filter = "open";
    private readonly Dictionary<string, SportlinkActieStatus> _status = new();
    private readonly Dictionary<string, bool> _afwijzenOpen = new();
    private readonly Dictionary<string, string?> _toelichting = new();

    private SportlinkActieStatus Status(string id)
        => _status.TryGetValue(id, out var s) ? s : _status[id] = new SportlinkActieStatus();

    // Sportlinks statuscodes (SportlinkChangeRequest.RequestStatus): CONFIRM wacht op ons,
    // APPROVED/DENIED zijn afgehandeld, REVOKED is door de indiener ingetrokken.
    private sealed record StatusFilter(string Key, string Label, Func<string?, bool> Match);

    private static readonly StatusFilter[] Filters =
    {
        new("open", "Openstaand", s => IsOpenstaand(s)),
        new("appr", "Goedgekeurd", s => Is(s, "APPROVED")),
        new("deny", "Afgewezen", s => Is(s, "DENIED")),
        new("revk", "Ingetrokken", s => Is(s, "REVOKED")),
        new("all", "Alle", _ => true),
    };

    private StatusFilter HuidigFilter => Filters.FirstOrDefault(f => f.Key == _filter) ?? Filters[0];

    private static bool Is(string? status, string code) => string.Equals(status, code, StringComparison.OrdinalIgnoreCase);
    private static bool IsOpenstaand(string? status) => Is(status, "CONFIRM");

    private static string StatusIcoon(string? status) => status?.ToUpperInvariant() switch
    {
        "CONFIRM" => "bi-exclamation-circle-fill text-warning",
        "APPROVED" => "bi-check-circle-fill text-success",
        "DENIED" => "bi-x-circle-fill text-danger",
        "REVOKED" => "bi-slash-circle text-secondary",
        _ => "bi-question-circle text-secondary",
    };

    private static string StatusLabel(string? status) => status?.ToUpperInvariant() switch
    {
        "CONFIRM" => "Openstaand — wacht op onze beslissing",
        "APPROVED" => "Goedgekeurd",
        "DENIED" => "Afgewezen",
        "REVOKED" => "Ingetrokken door de indiener",
        _ => $"Onbekende status ({status})",
    };

    /// <summary>Compacte weergave van wat de tegenstander vraagt; alleen de velden die afwijken van huidig.</summary>
    private static string Gevraagd(SportlinkChangeRequestDataDto? d)
    {
        if (d == null) return "–";
        var delen = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.RequestedDate) && d.RequestedDate != d.CurrentDate) delen.Add(d.RequestedDate!);
        if (!string.IsNullOrWhiteSpace(d.RequestedStartTime) && d.RequestedStartTime != d.CurrentStartTime) delen.Add(d.RequestedStartTime!);
        var acc = string.Join(" ", new[] { d.RequestedFacilityName, d.RequestedSubFacilityName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var huidigAcc = string.Join(" ", new[] { d.CurrentFacilityName, d.CurrentSubFacilityName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (acc.Length > 0 && acc != huidigAcc) delen.Add(acc);
        if (delen.Count > 0) return string.Join(" · ", delen);

        // Niets afwijkend herkend — toon dan het volledige gevraagde tijdstip zodat er nooit een leeg vak staat.
        var alles = string.Join(" ", new[] { d.RequestedDate, d.RequestedStartTime, acc }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return alles.Length > 0 ? alles : "–";
    }

    protected override Task OnInitializedAsync() => LaadVerzoekenAsync();

    private async Task LaadVerzoekenAsync()
    {
        _laden = true;
        _fout = null;
        StateHasChanged();
        try
        {
            var result = await Api.GetSportlinkChangeRequestsAsync();
            if (result.Success) _verzoeken = result.Data;
            else _fout = result.ErrorMessage ?? "Onbekende fout bij ophalen wijzigingsverzoeken.";
        }
        finally { _laden = false; }
    }

    private async Task HandelAfAsync(SportlinkChangeRequestDto verzoek, bool isApprove)
    {
        if (verzoek.PublicRequestId == null || verzoek.PublicMatchId == null) return;
        var id = verzoek.PublicRequestId;
        var status = Status(id);
        var toelichting = _toelichting.GetValueOrDefault(id);
        if (!isApprove && string.IsNullOrWhiteSpace(toelichting))
        {
            status.Fout("Toelichting is verplicht bij afwijzen.");
            return;
        }

        status.Start();
        StateHasChanged();
        try
        {
            var actie = isApprove ? "APPROVE" : "DENY";
            var r = await Api.PutSportlinkChangeRequestActionAsync(id, verzoek.PublicMatchId, actie, toelichting);
            var geslaagd = status.Verwerk(r.Success, r.ErrorMessage, r.Data,
                isApprove ? "Goedgekeurd in Sportlink Club." : "Afgewezen in Sportlink Club.",
                "Sportlink heeft de actie afgewezen");
            if (geslaagd)
            {
                _afwijzenOpen[id] = false;
                await LaadVerzoekenAsync();
            }
        }
        finally { status.Klaar(); }
    }
}
