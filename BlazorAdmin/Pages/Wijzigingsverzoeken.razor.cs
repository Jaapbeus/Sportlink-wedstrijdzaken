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

    // Sportlinks vier statusgroepen (#1439), afgeleid op de server uit ChangeRequestStatus
    // (SportlinkChangeRequestStatusGroep): OPEN, ACCEPTED, DENIED, REVOKED. UNKNOWN krijgt alleen
    // een tab als er zulke items zijn.
    private sealed record StatusFilter(string Key, string Label, Func<SportlinkChangeRequestDto, bool> Match);

    private static readonly StatusFilter[] BasisFilters =
    {
        new("open", "Openstaand", v => Groep(v) == "OPEN"),
        new("acc", "Akkoord", v => Groep(v) == "ACCEPTED"),
        new("deny", "Afgewezen", v => Groep(v) == "DENIED"),
        new("revk", "Ingetrokken", v => Groep(v) == "REVOKED"),
    };

    private static readonly StatusFilter OnbekendFilter = new("unk", "Onbekend", v => Groep(v) == "UNKNOWN");
    private static readonly StatusFilter AlleFilter = new("all", "Alle", _ => true);

    private IEnumerable<StatusFilter> Filters
    {
        get
        {
            foreach (var f in BasisFilters) yield return f;
            if (_verzoeken?.Any(OnbekendFilter.Match) == true) yield return OnbekendFilter;
            yield return AlleFilter;
        }
    }

    private StatusFilter HuidigFilter => Filters.FirstOrDefault(f => f.Key == _filter) ?? BasisFilters[0];

    private static string Groep(SportlinkChangeRequestDto v) => v.StatusGroep ?? "UNKNOWN";

    /// <summary>Inkomend; een ontbrekende IsIncomingRequest (null) valt bewust onder Inkomend.</summary>
    private static bool IsInkomend(SportlinkChangeRequestDto v) => v.IsIncomingRequest != false;

    /// <summary>Goedkeuren/afwijzen kan alleen op een openstaand, inkomend verzoek.</summary>
    private static bool KanBeslissen(SportlinkChangeRequestDto v)
        => Groep(v) == "OPEN" && v.IsIncomingRequest == true;

    private static string StatusIcoon(SportlinkChangeRequestDto v) => Groep(v) switch
    {
        "OPEN" => "bi-exclamation-circle-fill text-warning",
        "ACCEPTED" => "bi-check-circle-fill text-success",
        "DENIED" => "bi-x-circle-fill text-danger",
        "REVOKED" => "bi-slash-circle text-secondary",
        _ => "bi-question-circle text-secondary",
    };

    private static string StatusLabel(SportlinkChangeRequestDto v) => Groep(v) switch
    {
        "OPEN" => "Openstaand",
        "ACCEPTED" => "Akkoord",
        "DENIED" => "Afgewezen",
        "REVOKED" => "Ingetrokken",
        _ => $"Onbekende status ({v.RequestStatus})",
    };

    private static string? StatusSubtekst(SportlinkChangeRequestDto v) => v.RequestStatus?.ToUpperInvariant() switch
    {
        "CONFIRM_UNION" => "wacht op de bond",
        "CONFIRM_HOME" => "wacht op thuisclub",
        "CONFIRM_AWAY" => "wacht op uitclub",
        _ => null,
    };

    /// <summary>Compacte weergave van wat de tegenstander vraagt; alleen de velden die afwijken van huidig.</summary>
    /// <summary>#1464: Sportlink-terugval; leeg of ontbrekend wordt een streepje.</summary>
    private static string LeegAlsStreepje(string? waarde) => string.IsNullOrWhiteSpace(waarde) ? "–" : waarde;

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
