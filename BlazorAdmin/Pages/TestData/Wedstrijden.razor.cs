using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages.TestData;

public partial class Wedstrijden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    // ── Model ──

    private class WedstrijdRij
    {
        public string  BkMatches      { get; } = $"ALLSTARS-{Guid.NewGuid():N}"[..28];
        public string? Datum          { get; set; }
        public string? Aanvangstijd   { get; set; }
        public string? ThuisTeam      { get; set; }
        public string? UitTeam        { get; set; }
        public string? VeldNaam       { get; set; }
        public string? VeldSubpositie { get; set; }
        public string? Soort          { get; set; } = "Oefenwedstrijd";

        public bool    IsSaving     { get; set; }
        public bool    IsOpgeslagen { get; set; }
        public bool    HeeftFout    { get; set; }
        public string? SaveError    { get; set; }

        public AllstarsWedstrijdDto ToDto() => new()
        {
            BkMatches      = BkMatches,
            Datum          = Datum,
            Aanvangstijd   = Aanvangstijd,
            ThuisTeam      = ThuisTeam,
            UitTeam        = UitTeam,
            VeldNaam       = VeldNaam,
            VeldSubpositie = VeldSubpositie,
            Soort          = Soort,
        };

        public static WedstrijdRij VanDto(AllstarsWedstrijdDto dto)
        {
            var rij = new WedstrijdRij(dto.BkMatches);
            rij.Datum          = dto.Datum;
            rij.Aanvangstijd   = dto.Aanvangstijd;
            rij.ThuisTeam      = dto.ThuisTeam;
            rij.UitTeam        = dto.UitTeam;
            rij.VeldNaam       = dto.VeldNaam;
            rij.VeldSubpositie = dto.VeldSubpositie;
            rij.Soort          = dto.Soort ?? "Oefenwedstrijd";
            rij.IsOpgeslagen   = true;
            return rij;
        }

        public WedstrijdRij() { }
        private WedstrijdRij(string bk) { BkMatches = bk; }
    }

    // ── State ──

    private List<WedstrijdRij>  _rijen       = new();
    private List<string>        _teams       = new();
    private List<VeldDto>       _velden      = new();
    private List<SpeeltijdDto>  _speeltijden = new();
    private bool    _loading = true;
    private bool    _bezig;
    private string? _error;

    private string _globalDatum        = DateTime.Today.ToString("yyyy-MM-dd");
    private string _globalSoort        = "Oefenwedstrijd";
    private string _globalTegenstander = "FC Onbekend";
    private string _globalStarttijd    = "10:00";
    private string _globalVeldNaam     = "";

    // Filter
    private string _filterVan = "";
    private string _filterTot = "";

    // Verplaats datum
    private string  _verplaatsVan      = "";
    private string  _verplaatsNaar     = "";
    private string? _verplaatsResultaat;
    private string? _verplaatsFout;

    // Sortering — standaard datum aflopend
    private string _sortKolom    = "datum";
    private bool   _sortAflopend = true;

    private static readonly string[] SoortOpties =
        ["Competitie", "Oefenwedstrijd", "Toernooi", "Vriendschappelijk"];

    // ── Velddeel opties op basis van Veldafmeting uit Speeltijden ──

    private IReadOnlyList<string>? GetVelddeelopties(string? teamnaam)
    {
        if (string.IsNullOrEmpty(teamnaam)) return null;
        var spatie1 = teamnaam.IndexOf(' ');
        if (spatie1 < 0) return null;
        var rest    = teamnaam[(spatie1 + 1)..];
        var spatie2 = rest.IndexOf(' ');
        var leeftijd = spatie2 >= 0 ? rest[..spatie2] : rest;
        leeftijd = leeftijd switch
        {
            "Heren" or "Heer" => "1-99",
            "Dames" or "Vrouwen" => "VR",
            _ => leeftijd
        };
        var s = _speeltijden.FirstOrDefault(x => x.Leeftijd == leeftijd);
        if (s == null || s.Veldafmeting >= 1.00m) return null;
        return (int)Math.Round(1m / s.Veldafmeting) switch
        {
            2 => ["A",  "B"],
            4 => ["A1", "A2", "B1", "B2"],
            3 => ["A",  "B",  "C"],
            _ => null
        };
    }

    // ── Computed ──

    private bool FilterActief =>
        !string.IsNullOrEmpty(_filterVan) || !string.IsNullOrEmpty(_filterTot);

    private bool VerplaatsKnopActief =>
        !string.IsNullOrEmpty(_verplaatsVan) &&
        !string.IsNullOrEmpty(_verplaatsNaar) &&
        _verplaatsVan != _verplaatsNaar;

    private List<WedstrijdRij> GesorteerdeEnGefilterde
    {
        get
        {
            var q = _rijen.AsEnumerable();

            if (!string.IsNullOrEmpty(_filterVan))
                q = q.Where(r => string.Compare(r.Datum, _filterVan, StringComparison.Ordinal) >= 0);
            if (!string.IsNullOrEmpty(_filterTot))
                q = q.Where(r => string.Compare(r.Datum, _filterTot, StringComparison.Ordinal) <= 0);

            q = (_sortKolom, _sortAflopend) switch
            {
                ("datum",        true)  => q.OrderByDescending(r => r.Datum),
                ("datum",        false) => q.OrderBy(r => r.Datum),
                ("thuisteam",    true)  => q.OrderByDescending(r => r.ThuisTeam),
                ("thuisteam",    false) => q.OrderBy(r => r.ThuisTeam),
                ("uitteam",      true)  => q.OrderByDescending(r => r.UitTeam),
                ("uitteam",      false) => q.OrderBy(r => r.UitTeam),
                ("aanvangstijd", true)  => q.OrderByDescending(r => r.Aanvangstijd),
                ("aanvangstijd", false) => q.OrderBy(r => r.Aanvangstijd),
                _                      => q.OrderByDescending(r => r.Datum),
            };

            return q.ToList();
        }
    }

    private RenderFragment SortIcon(string kolom) => builder =>
    {
        if (_sortKolom != kolom) return;
        builder.AddContent(0, _sortAflopend ? " ▼" : " ▲");
    };

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        await ClubSelector.InitializeAsync();
        await LaadAsync();
    }

    private async Task LaadAsync()
    {
        _loading = true;
        _error   = null;

        var tWedstrijden = Api.GetAllstarsWedstrijdenAsync();
        var tTeams       = Api.GetAllstarsTeamsAsync();
        var tVelden      = Api.GetVeldenAsync();
        var tSpeeltijden = Api.GetSpeeltijdenAsync();
        await Task.WhenAll(tWedstrijden, tTeams, tVelden, tSpeeltijden);

        var wedstrijden = await tWedstrijden;
        var teams       = await tTeams;
        var velden      = await tVelden;
        var speeltijden = await tSpeeltijden;

        if (wedstrijden.Success) _rijen       = wedstrijden.Data?.Select(WedstrijdRij.VanDto).ToList() ?? new();
        if (teams.Success)       _teams       = teams.Data       ?? new();
        if (velden.Success)      _velden      = velden.Data      ?? new();
        if (speeltijden.Success) _speeltijden = speeltijden.Data ?? new();

        if (!teams.Success) _error = $"Teams ophalen mislukt: {teams.ErrorMessage}";

        _loading = false;
    }

    // ── Sorteren ──

    private void SorteerOp(string kolom)
    {
        if (_sortKolom == kolom)
            _sortAflopend = !_sortAflopend;
        else
        {
            _sortKolom    = kolom;
            _sortAflopend = true;
        }
    }

    // ── Filter ──

    private void WisFilter()
    {
        _filterVan = "";
        _filterTot = "";
    }

    // ── Verplaats datum ──

    private async Task VerplaatsDatumAsync()
    {
        if (!VerplaatsKnopActief) return;
        _bezig = true;
        _verplaatsResultaat = null;
        _verplaatsFout      = null;
        StateHasChanged();

        var result = await Api.VerplaatsAllstarsDatumAsync(_verplaatsVan, _verplaatsNaar);

        if (result.Success)
        {
            var count = result.Data?.AantalVerplaatst ?? 0;
            _verplaatsResultaat = $"{count} wedstrijd{(count == 1 ? "" : "en")} verplaatst naar {_verplaatsNaar}";
            foreach (var rij in _rijen.Where(r => r.Datum == _verplaatsVan))
                rij.Datum = _verplaatsNaar;
        }
        else
        {
            _verplaatsFout = result.ErrorMessage ?? "Verplaatsen mislukt";
        }

        _bezig = false;
    }

    // ── Cel-events ──

    private void OnThuisTeamChanged(WedstrijdRij rij, string? waarde)
    {
        rij.ThuisTeam = waarde;
        if (string.IsNullOrEmpty(rij.Aanvangstijd))
            rij.Aanvangstijd = _globalStarttijd;

        // Auto-fill tegenstander met suffix van het thuisteam
        if (!string.IsNullOrEmpty(waarde))
        {
            var suffix = waarde.Contains(' ')
                ? waarde[(waarde.IndexOf(' ') + 1)..]
                : waarde;
            if (string.IsNullOrEmpty(rij.UitTeam) || rij.UitTeam == _globalTegenstander)
            {
                var basis = string.IsNullOrWhiteSpace(_globalTegenstander) ? "FC Onbekend" : _globalTegenstander;
                rij.UitTeam = $"{basis} {suffix}";
            }
        }

        _ = AutoSaveAsync(rij);
    }

    private async Task AutoSaveAsync(WedstrijdRij rij)
    {
        if (string.IsNullOrWhiteSpace(rij.ThuisTeam) && string.IsNullOrWhiteSpace(rij.Datum))
            return;

        rij.IsSaving  = true;
        rij.HeeftFout = false;
        rij.SaveError = null;
        StateHasChanged();

        var result = await Api.UpsertAllstarsWedstrijdAsync(rij.ToDto());

        rij.IsSaving     = false;
        rij.IsOpgeslagen = result.Success;
        rij.HeeftFout    = !result.Success;
        rij.SaveError    = result.Success ? null : result.ErrorMessage;
        StateHasChanged();
    }

    // ── Knoppen ──

    private void VoegLegeRijToe()
    {
        var rij = new WedstrijdRij
        {
            Datum        = _globalDatum,
            Soort        = _globalSoort,
            UitTeam      = string.IsNullOrWhiteSpace(_globalTegenstander) ? null : _globalTegenstander,
            Aanvangstijd = _globalStarttijd,
            VeldNaam     = string.IsNullOrEmpty(_globalVeldNaam) ? null : _globalVeldNaam,
        };
        _rijen.Add(rij);
    }

    private async Task VoegAlleTeamsToeAsync()
    {
        _bezig = true;
        var nieuweRijen = _teams.Select(team =>
        {
            var suffix = team.Contains(' ') ? team[(team.IndexOf(' ') + 1)..] : team;
            return new WedstrijdRij
            {
                Datum        = _globalDatum,
                ThuisTeam    = team,
                UitTeam      = string.IsNullOrWhiteSpace(_globalTegenstander)
                    ? $"FC Onbekend {suffix}"
                    : $"{_globalTegenstander} {suffix}",
                Soort        = _globalSoort,
                Aanvangstijd = _globalStarttijd,
                VeldNaam     = string.IsNullOrEmpty(_globalVeldNaam) ? null : _globalVeldNaam,
            };
        }).ToList();

        _rijen.AddRange(nieuweRijen);
        StateHasChanged();

        foreach (var rij in nieuweRijen)
            await AutoSaveAsync(rij);

        _bezig = false;
    }

    private async Task VerwijderRijAsync(WedstrijdRij rij)
    {
        if (rij.IsOpgeslagen)
            await Api.DeleteAllstarsWedstrijdAsync(rij.BkMatches);
        _rijen.Remove(rij);
    }

    private async Task VerwijderGefilterdeAsync()
    {
        if (!FilterActief) return;
        _bezig = true;
        var van = string.IsNullOrEmpty(_filterVan) ? null : _filterVan;
        var tot = string.IsNullOrEmpty(_filterTot) ? null : _filterTot;
        await Api.DeleteAlleAllstarsWedstrijdenAsync(van, tot);
        _rijen.RemoveAll(r =>
            (van == null || string.Compare(r.Datum, van, StringComparison.Ordinal) >= 0) &&
            (tot == null || string.Compare(r.Datum, tot, StringComparison.Ordinal) <= 0));
        _bezig = false;
    }

    // ── Fill-down per kolom ──

    private void VulKolom(string kolom)
    {
        string? bronWaarde = kolom switch
        {
            "datum"        => _rijen.Select(r => r.Datum).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            "aanvangstijd" => _rijen.Select(r => r.Aanvangstijd).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            _              => null,
        };
        if (bronWaarde == null) return;

        foreach (var rij in _rijen)
        {
            var leeg = kolom switch
            {
                "datum"        => string.IsNullOrEmpty(rij.Datum),
                "aanvangstijd" => string.IsNullOrEmpty(rij.Aanvangstijd),
                _              => false,
            };
            if (!leeg) continue;
            if (kolom == "datum")        rij.Datum        = bronWaarde;
            if (kolom == "aanvangstijd") rij.Aanvangstijd = bronWaarde;
            _ = AutoSaveAsync(rij);
        }
    }

    // ── Helpers ──

    private static string RijKlasse(WedstrijdRij rij) =>
        rij.HeeftFout    ? "table-danger"  :
        rij.IsOpgeslagen ? ""              :
                           "table-warning";
}
