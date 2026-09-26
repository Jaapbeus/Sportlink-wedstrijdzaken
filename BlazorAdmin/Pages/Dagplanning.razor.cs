using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>Dagplanning.razor</c> (#1122): planning, Gantt, veldbezetting en de
/// Sportlink-kolom. Het Sportlink-paneel per wedstrijd is <see cref="Shared.SportlinkMatchPanel"/>.</summary>
public partial class Dagplanning : IDisposable
{
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ClubSelectorService ClubSelector { get; set; } = default!;

    private const int KleineAfwijkingDrempelMinuten = 15;

    private DateTime _datumDt;
    private int _bufferMinuten = 15;
    private bool _bezig;
    private string? _errorMessage;
    private string _filter = "alles";
    private string _visTab = "optimaal";
    private AutoPlanResponseDto? _plan;

    // Directe veldbezetting (#566)
    private List<VeldbezettingItemDto> _veldbezetting = new();
    private bool _veldbezettingBezig;
    private string? _veldbezettingError;

    // Hover-correlatie tussen tijdlijnblok en tabelregel (#1315): dezelfde WedstrijdCode licht in
    // beide op, zodat een wedstrijd uit de lijst visueel terug te vinden is in de tijdlijn erboven.
    private long? _veldbezettingHoverCode;

    // Sportlink-kolom (#989/#991): alleen uitklap-status en deep-link blijven hier; het paneel zelf
    // is het component SportlinkMatchPanel (#1122) met één instantie per uitgeklapte rij.
    private bool _sportlinkExtensionEnabled;
    private readonly HashSet<long> _sportlinkUitgeklapt = new();
    private readonly Dictionary<long, SportlinkActieStatus> _deeplinks = new();

    private SportlinkActieStatus Deeplink(long wedstrijdCode)
        => _deeplinks.TryGetValue(wedstrijdCode, out var s) ? s : _deeplinks[wedstrijdCode] = new SportlinkActieStatus();

    private void ToggleSportlinkPaneel(long wedstrijdCode)
    {
        if (!_sportlinkUitgeklapt.Remove(wedstrijdCode)) _sportlinkUitgeklapt.Add(wedstrijdCode);
    }

    // #989: haalt alleen het PublicMatchId op (lichtgewicht endpoint) en opent meteen een nieuw tabblad.
    private async Task OpenInSportlinkAsync(long wedstrijdCode)
    {
        var status = Deeplink(wedstrijdCode);
        status.Start();
        try
        {
            var result = await Api.GetSportlinkPublicMatchIdAsync(wedstrijdCode.ToString());
            if (!result.Success || string.IsNullOrWhiteSpace(result.Data?.PublicMatchId))
            {
                status.Fout(result.ErrorMessage ?? "PublicMatchId niet gevonden.");
                return;
            }
            var url = $"https://club.sportlink.com/competition-affairs/match-details/{Uri.EscapeDataString(result.Data.PublicMatchId)}";
            await JS.InvokeVoidAsync("blazorHelpers.openInNewTab", url);
        }
        finally { status.Klaar(); }
    }

    // HTML-export van de berekende planning (voorheen onderdeel van de klassieke flow, #666)
    private string? _kopieerStatus;

    // Volgt de gekozen tab: de export toont dezelfde planning als de tijdlijn erboven.
    private string? HuidigeExportHtml =>
        _plan == null ? null : (_visTab == "optimaal" ? _plan.OptimaleHtml : _plan.HuidigeHtml);

    // De gegenereerde HTML bevat een klik-interactie-script. In het iframe hieronder staat bewust
    // geen 'allow-scripts' (XSS-verdediging, #603), en e-mailclients strippen scripts toch — het
    // script wordt daar dus altijd geblokkeerd en levert alleen een console-fout op. Voor de
    // voorbeeldweergave en de e-mailversie halen we het eruit; de download houdt het wél, want in
    // een los geopend HTML-bestand werkt de interactie normaal.
    private string? ExportHtmlZonderScript
    {
        get
        {
            var html = HuidigeExportHtml;
            if (string.IsNullOrEmpty(html)) return html;
            // Geen static Regex-veld en geen RegexOptions.Compiled: dat faalt in Blazor WebAssembly
            // (NullReferenceException bij het renderen, geen buildfout — alleen zichtbaar in de browser).
            return System.Text.RegularExpressions.Regex.Replace(
                html, @"<script\b[^>]*>.*?</script>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }

    // Toepassen feedback
    private string? _toepassenMelding;
    private bool _toepassenFout;

    private string DatumStr => _datumDt.ToString("yyyy-MM-dd");
    private bool IsAllstars => ClubSelector.SelectedClubCode == "ALLSTARS";

    protected override void OnInitialized()
    {
        _datumDt = VolgendZaterdag().ToDateTime(TimeOnly.MinValue);
        ClubSelector.OnChange += OnClubChanged;
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadVeldbezettingAsync();

        // #989: geen Sportlink-kolom/-knoppen tonen als de extension uit staat (DoD).
        var settings = await Api.GetSettingsAsync();
        _sportlinkExtensionEnabled = settings.Success && settings.Data?.SportlinkExtensionEnabled == true;

        // #1334: automatisch een plan laden, zodat de wedstrijdenlijst (incl. de Sportlink-kolom
        // met de bewerkacties) meteen zichtbaar is — vóór deze fix moest een gebruiker altijd eerst
        // handmatig op "Optimaliseer" klikken voordat er ook maar één wedstrijd te zien of te
        // bewerken was. De knop blijft bestaan voor een expliciete herberekening (bijv. na het
        // wijzigen van de buffer-instelling, waar geen andere trigger voor is).
        await AutoPlanAsync();
    }

    private async Task OnDatumChanged()
    {
        _plan = null;
        _errorMessage = null;
        _toepassenMelding = null;
        await LoadVeldbezettingAsync();
        await AutoPlanAsync();
    }

    private async Task LoadVeldbezettingAsync()
    {
        _veldbezettingBezig = true;
        _veldbezettingError = null;
        try
        {
            var result = await Api.GetVeldbezettingAsync(DatumStr);
            if (result.Success)
                _veldbezetting = result.Data ?? new();
            else
            {
                _veldbezetting = new();
                _veldbezettingError = result.ErrorMessage ?? "Ophalen veldbezetting mislukt.";
            }
        }
        finally { _veldbezettingBezig = false; }
    }

    private void OnClubChanged() => InvokeAsync(async () =>
    {
        _plan = null;
        _errorMessage = null;
        _toepassenMelding = null;
        await LoadVeldbezettingAsync();
        await AutoPlanAsync();
        StateHasChanged();
    });

    public void Dispose() => ClubSelector.OnChange -= OnClubChanged;

    private static DateOnly VolgendZaterdag()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        int dagenTotZaterdag = ((int)DayOfWeek.Saturday - (int)vandaag.DayOfWeek + 7) % 7;
        if (dagenTotZaterdag == 0) dagenTotZaterdag = 7;
        return vandaag.AddDays(dagenTotZaterdag);
    }

    private async Task AutoPlanAsync()
    {
        _errorMessage = null;
        _toepassenMelding = null;
        _bezig = true;
        try
        {
            var req = new AutoPlanRequestDto { Datum = DatumStr, BufferMinuten = _bufferMinuten };
            var result = await Api.AutoPlanAsync(req);
            if (result.Success)
            {
                _plan = result.Data;
                _filter = "alles";
                _visTab = "optimaal";
            }
            else
                _errorMessage = result.ErrorMessage ?? "Onbekende fout bij auto-planning.";
        }
        finally { _bezig = false; }
    }

    private async Task ToepassenAsync()
    {
        _toepassenMelding = null;
        _toepassenFout = false;
        _bezig = true;
        try
        {
            var req = new AutoPlanToepassenRequestDto { Datum = DatumStr, BufferMinuten = _bufferMinuten };
            var result = await Api.AutoPlanToepassenAsync(req);
            if (result.Success && result.Data != null)
            {
                var d = result.Data;
                _toepassenMelding = d.Mislukt == 0
                    ? $"{d.Bijgewerkt} wedstrijd(en) bijgewerkt in testdata."
                    : $"{d.Bijgewerkt} bijgewerkt, {d.Mislukt} mislukt: {string.Join("; ", d.Fouten)}";
                _toepassenFout = d.Mislukt > 0;
            }
            else
            {
                _toepassenMelding = result.ErrorMessage ?? "Toepassen mislukt.";
                _toepassenFout = true;
            }
        }
        finally { _bezig = false; }
    }

    private IEnumerable<AutoPlanWedstrijdItemDto> GefilterdeLijst()
    {
        if (_plan == null) return Enumerable.Empty<AutoPlanWedstrijdItemDto>();
        return _filter switch
        {
            "wijzigingen" => _plan.Wedstrijden.Where(w => w.Status is "nieuw-slot" or "wijziging"),
            "probleem"    => _plan.Wedstrijden.Where(w => w.Status == "niet-inplanbaar"),
            _             => _plan.Wedstrijden
        };
    }

    // ── Voorkeurstijd-weergave (#666) ──

    private static string VoorkeurBadgeClass(string voorkeurStatus) => voorkeurStatus switch
    {
        "op-tijd"          => "bg-success",
        "kleine-afwijking" => "bg-warning text-dark",
        "grote-afwijking"  => "bg-danger",
        _                  => "bg-light text-dark border"
    };

    private static string VoorkeurAfwijkingTekst(int? afwijking)
    {
        if (!afwijking.HasValue || afwijking.Value == 0) return "";
        var teken = afwijking.Value > 0 ? "+" : "−";
        return $" ({teken}{Math.Abs(afwijking.Value)} min)";
    }

    private static string VoorkeurBronLabel(string? bron) => bron switch
    {
        "regel"    => "regel",
        "team"     => "team",
        "leeftijd" => "standaard",
        _          => ""
    };

    private static string VoorkeurTitel(AutoPlanWedstrijdItemDto item)
    {
        var bron = item.VoorkeurBron switch
        {
            "regel"    => "uit een teamregel (voorkeursveld met tijd)",
            "team"     => "eigen voorkeurstijd van het team",
            "leeftijd" => "standaardtijd van de leeftijdscategorie",
            _          => "onbekende bron"
        };
        string afwijking;
        if (!item.VoorkeurAfwijkingMinuten.HasValue)
        {
            afwijking = "afwijking onbekend";
        }
        else
        {
            int m = item.VoorkeurAfwijkingMinuten.Value;
            afwijking = m == 0 ? "exact op de voorkeurstijd"
                      : m > 0  ? $"{m} minuten later dan gewenst"
                               : $"{Math.Abs(m)} minuten eerder dan gewenst";
        }
        return $"Voorkeurstijd {item.VoorkeurTijd} — {bron}. Ingepland: {afwijking}.";
    }

    private static string RowClass(string status) => status switch
    {
        "ongewijzigd"     => "",
        "nieuw-slot"      => "table-warning",
        "wijziging"       => "table-primary",
        "niet-inplanbaar" => "table-danger",
        "onbekend-team"   => "",   // neutraal — geen rode achtergrond (#487)
        _                 => ""
    };

    // ── HTML-export van de berekende planning ──
    // Sinds #666 komt de HTML uit de auto-plan-response zelf (HuidigeHtml / OptimaleHtml). Er is dus
    // geen extra API-call meer nodig; het losse optimaliseer-endpoint is vervallen.

    private async Task KopieerEmailHtmlAsync()
    {
        var html = ExportHtmlZonderScript;
        if (string.IsNullOrEmpty(html)) return;
        await JS.InvokeVoidAsync("blazorHelpers.copyToClipboard", html);
        _kopieerStatus = "Gekopieerd!";
        _ = Task.Delay(2500).ContinueWith(_ => { _kopieerStatus = null; InvokeAsync(StateHasChanged); });
    }

    private async Task DownloadHtmlAsync()
    {
        var html = HuidigeExportHtml;
        if (string.IsNullOrEmpty(html)) return;
        await JS.InvokeVoidAsync("blazorHelpers.downloadHtml", $"dagplanning-{DatumStr}.html", html);
    }

    // ── Gantt helpers ──

    // Wedstrijd meegegeven zodat een sleepactie in de tijdlijn de onderliggende regel kan bijwerken
    // (#666). Null op de "Huidige situatie"-tab: die weergave is de stand uit Sportlink en niet te
    // bewerken — alleen de berekende planning mag met de hand worden aangepast.
    private record GanttItem(string VeldNaam, string? SubPos, TimeOnly Aanvang, TimeOnly Einde,
        decimal Fractie, string Label, string Status, int DuurMinuten,
        string? VoorkeurTijd, int? VoorkeurAfwijking, AutoPlanWedstrijdItemDto? Bron = null,
        long? WedstrijdCode = null);

    private List<GanttItem> BouwGanttItems(bool isOptimaal)
    {
        if (_plan == null) return new();
        var items = new List<GanttItem>();
        foreach (var w in _plan.Wedstrijden.Where(w => w.DuurMinuten > 0))
        {
            if (isOptimaal)
            {
                if (w.OptimaalTijd == null || w.OptimaalVeldNaam == null) continue;
                if (!TimeOnly.TryParse(w.OptimaalTijd, out var t)) continue;
                var sub = GanttExtractSubPos(w.OptimaalVeld);
                items.Add(new GanttItem(w.OptimaalVeldNaam, sub, t, t.AddMinutes(w.DuurMinuten),
                    w.Veldafmeting, GanttMatchLabel(w.Wedstrijd, w.TeamNaam), w.Status, w.DuurMinuten,
                    w.VoorkeurTijd, w.VoorkeurAfwijkingMinuten, w, w.WedstrijdCode));
            }
            else
            {
                if (!w.HeeftVeld || !w.HeeftTijd) continue;
                if (!TimeOnly.TryParse(w.HuidigeTijd!, out var t)) continue;
                var (veldBase, sub) = GanttSplitVeld(w.HuidigeVeld!);
                items.Add(new GanttItem(veldBase, sub, t, t.AddMinutes(w.DuurMinuten),
                    w.Veldafmeting, GanttMatchLabel(w.Wedstrijd, w.TeamNaam), "ongewijzigd", w.DuurMinuten,
                    null, null, WedstrijdCode: w.WedstrijdCode));
            }
        }
        return items;
    }

    // ── Handmatig verslepen in de tijdlijn (#666) ──
    // De wedstrijdsecretaris kan een blok naar een andere tijd of een ander veld slepen. De tijd snapt
    // op 5 minuten (voetbalconventie, gelijk aan de planner zelf). De tabel en de samenvatting hieronder
    // lezen dezelfde objecten, dus die lopen automatisch mee.

    private AutoPlanWedstrijdItemDto? _sleepItem;
    private double _sleepGrijpOffsetPx;
    private readonly HashSet<AutoPlanWedstrijdItemDto> _handmatigAangepast = new();
    private List<string> _conflicten = new();

    private void SleepStart(GanttItem gi, DragEventArgs e)
    {
        if (gi.Bron == null) return;
        _sleepItem = gi.Bron;
        // Waar in het blok is gepakt — anders verspringt het blok naar de cursor bij het neerzetten.
        _sleepGrijpOffsetPx = e.OffsetX;
    }

    private async Task SleepDrop(string veldNaam, int rijIndex, int startMinuut, int totaalMinuten, DragEventArgs e)
    {
        var item = _sleepItem;
        _sleepItem = null;
        if (item == null || _plan == null) return;

        // Het element kan tijdens een snelle re-render al verdwenen zijn (#597).
        ElementRect? rect;
        try
        {
            rect = await JS.InvokeAsync<ElementRect?>("blazorHelpers.getElementRect", $"gantt-rij-{rijIndex}");
        }
        catch (JSException)
        {
            return;
        }
        if (rect == null || rect.Width <= 0) return;

        double blokLinksPx = e.ClientX - rect.Left - _sleepGrijpOffsetPx;
        double minutenVanaf = blokLinksPx / rect.Width * totaalMinuten;
        int nieuweMinuut = startMinuut + (int)Math.Round(minutenVanaf / 5.0) * 5;
        nieuweMinuut = Math.Clamp(nieuweMinuut, 0, 24 * 60 - item.DuurMinuten);

        // Zelfde grens als GanttUurLabel: een drop op het uiterste einde van de as (1440) valt buiten TimeOnly.
        var nieuweTijd = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(nieuweMinuut, 0, 1439)));
        item.OptimaalTijd = nieuweTijd.ToString("HH:mm");

        // Veld overnemen van de rij waarop is neergezet.
        var doelVeld = _plan.Wedstrijden
            .Where(w => w.OptimaalVeldNaam == veldNaam && w.OptimaalVeldNummer.HasValue)
            .Select(w => w.OptimaalVeldNummer)
            .FirstOrDefault();
        item.OptimaalVeldNaam = veldNaam;
        if (doelVeld.HasValue) item.OptimaalVeldNummer = doelVeld;

        // Verticale droppositie bepaalt de kwartbaan van het veld. Een rij is vier banen hoog:
        // A1, A2, B1, B2. Zo kan een kwartveldwedstrijd bewust op A2 gezet worden in plaats van
        // altijd op de eerste vrije plek te belanden.
        int baan = 0;
        if (rect.Height > 0)
        {
            double relY = (e.ClientY - rect.Top) / rect.Height;
            baan = Math.Clamp((int)(relY * 4), 0, 3);
        }
        item.OptimaalVeld = BouwVeldString(veldNaam, item.Veldafmeting, baan);

        HerberekenNaSleep(item);
        _handmatigAangepast.Add(item);
    }

    // Veldnaam + subpositie op basis van de veldafmeting en de aangewezen baan.
    // Een heel veld krijgt geen subpositie; een half veld hoort op A (banen 0-1) of B (banen 2-3);
    // een kwart veld gaat naar de aangewezen baan zelf.
    private static string BouwVeldString(string veldNaam, decimal veldafmeting, int baan) =>
        veldafmeting switch
        {
            <= 0.26m => $"{veldNaam} {BaanLabels[baan]}",
            <= 0.51m => $"{veldNaam} {(baan <= 1 ? "A" : "B")}",
            _        => veldNaam
        };

    private static readonly string[] BaanLabels = ["A1", "A2", "B1", "B2"];

    // Welke kwartbanen bezet een wedstrijd met deze subpositie? Leeg/onbekend = heel veld.
    // Dezelfde indeling als de planner server-side gebruikt.
    private static bool[] BanenVanSubpositie(string? subpositie)
    {
        var b = new bool[4];
        switch ((subpositie ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "A1": b[0] = true; break;
            case "A2": b[1] = true; break;
            case "B1": b[2] = true; break;
            case "B2": b[3] = true; break;
            case "A":  b[0] = b[1] = true; break;
            case "B":  b[2] = b[3] = true; break;
            default:   b[0] = b[1] = b[2] = b[3] = true; break;
        }
        return b;
    }

    // Status, voorkeursafwijking en de samenvatting opnieuw bepalen — dezelfde regels als de server,
    // zodat een handmatige zet net zo eerlijk wordt beoordeeld als een berekende.
    private void HerberekenNaSleep(AutoPlanWedstrijdItemDto item)
    {
        if (_plan == null) return;

        bool tijdWijzigt = item.HuidigeTijd?.Trim() != item.OptimaalTijd;
        bool veldWijzigt = NormaliseerVeld(item.HuidigeVeld) != NormaliseerVeld(item.OptimaalVeld);
        item.Status = (!item.HeeftVeld || !item.HeeftTijd) ? "nieuw-slot"
                    : (tijdWijzigt || veldWijzigt) ? "wijziging" : "ongewijzigd";

        if (!string.IsNullOrWhiteSpace(item.VoorkeurTijd)
            && TimeOnly.TryParse(item.VoorkeurTijd, out var vt)
            && TimeOnly.TryParse(item.OptimaalTijd, out var nt))
        {
            item.VoorkeurAfwijkingMinuten = (int)(nt.ToTimeSpan() - vt.ToTimeSpan()).TotalMinutes;
            int abs = Math.Abs(item.VoorkeurAfwijkingMinuten.Value);
            item.VoorkeurStatus = abs == 0 ? "op-tijd" : abs <= KleineAfwijkingDrempelMinuten ? "kleine-afwijking" : "grote-afwijking";
        }

        if (item.VoorkeurVeldNummer.HasValue)
            item.VoorkeurVeldToegepast = item.OptimaalVeldNummer == item.VoorkeurVeldNummer.Value;

        _plan.TeWijzigen = _plan.Wedstrijden.Count(i => i.Status is "nieuw-slot" or "wijziging");
        var eindes = _plan.Wedstrijden
            .Where(i => i.OptimaalTijd != null && i.DuurMinuten > 0 && TimeOnly.TryParse(i.OptimaalTijd, out _))
            .Select(i => TimeOnly.Parse(i.OptimaalTijd!).AddMinutes(i.DuurMinuten)).ToList();
        _plan.GeschatteEindTijd = eindes.Count > 0 ? eindes.Max().ToString("HH:mm") : null;

        ControleerConflicten();
    }

    // Overlap- en buffercontrole per veld, zodat een handmatige zet niet stil een onmogelijke
    // planning oplevert. Zelfde regel als de server: gelijktijdig mag als de veldfracties samen
    // binnen één veld blijven; achter elkaar vraagt de ingestelde buffer.
    private void ControleerConflicten()
    {
        _conflicten = new List<string>();
        if (_plan == null) return;

        var perVeld = _plan.Wedstrijden
            .Where(w => w.OptimaalTijd != null && w.OptimaalVeldNaam != null && w.DuurMinuten > 0
                        && TimeOnly.TryParse(w.OptimaalTijd, out _))
            .GroupBy(w => w.OptimaalVeldNaam!);

        foreach (var veld in perVeld)
        {
            var lijst = veld.OrderBy(w => TimeOnly.Parse(w.OptimaalTijd!)).ToList();
            for (int i = 0; i < lijst.Count; i++)
            {
                var a = lijst[i];
                var aStart = TimeOnly.Parse(a.OptimaalTijd!);
                var aEind = aStart.AddMinutes(a.DuurMinuten);
                for (int j = i + 1; j < lijst.Count; j++)
                {
                    var b = lijst[j];
                    var bStart = TimeOnly.Parse(b.OptimaalTijd!);
                    var bEind = bStart.AddMinutes(b.DuurMinuten);

                    bool overlapt = aStart < bEind && aEind > bStart;
                    if (overlapt)
                    {
                        // Op banen vergelijken, niet op de som van de fracties: een half veld op A plus
                        // een kwart veld telt op tot 0,75 — numeriek prima — maar botst wél als dat kwart
                        // op A1 of A2 staat. De veldhelften zijn wat er fysiek bezet is.
                        var baanA = BanenVanSubpositie(GanttExtractSubPos(a.OptimaalVeld));
                        var baanB = BanenVanSubpositie(GanttExtractSubPos(b.OptimaalVeld));
                        bool botst = false;
                        for (int k = 0; k < 4; k++) if (baanA[k] && baanB[k]) botst = true;
                        if (botst)
                            _conflicten.Add($"{veld.Key}: {a.TeamNaam} en {b.TeamNaam} staan op hetzelfde veldgedeelte op dezelfde tijd.");
                    }
                    else
                    {
                        int gat = (int)(bStart.ToTimeSpan() - aEind.ToTimeSpan()).TotalMinutes;
                        if (gat >= 0 && gat < _bufferMinuten)
                            _conflicten.Add($"{veld.Key}: tussen {a.TeamNaam} en {b.TeamNaam} zit {gat} min, minder dan de ingestelde buffer van {_bufferMinuten} min.");
                    }
                }
            }
        }

        // #939: dezelfde controle als hierboven, maar per TEAM in plaats van per veld — een team kan
        // niet op twee velden tegelijk staan, ongeacht of er op elk van die velden zelf nog ruimte
        // was. Zonder deze doorsnede kon een handmatige sleepactie een team dubbel boeken zonder
        // enige waarschuwing, terwijl FieldScheduler datzelfde scenario server-side al weigert.
        var perTeam = _plan.Wedstrijden
            .Where(w => w.OptimaalTijd != null && w.DuurMinuten > 0 && TimeOnly.TryParse(w.OptimaalTijd, out _)
                        && !string.IsNullOrWhiteSpace(w.TeamNaam))
            .GroupBy(w => w.TeamNaam);

        foreach (var team in perTeam)
        {
            var lijst = team.OrderBy(w => TimeOnly.Parse(w.OptimaalTijd!)).ToList();
            for (int i = 0; i < lijst.Count; i++)
            {
                var a = lijst[i];
                var aStart = TimeOnly.Parse(a.OptimaalTijd!);
                var aEind = aStart.AddMinutes(a.DuurMinuten);
                for (int j = i + 1; j < lijst.Count; j++)
                {
                    var b = lijst[j];
                    var bStart = TimeOnly.Parse(b.OptimaalTijd!);
                    var bEind = bStart.AddMinutes(b.DuurMinuten);

                    bool overlapt = aStart < bEind && aEind > bStart;
                    if (overlapt)
                    {
                        _conflicten.Add($"{team.Key}: staat tegelijk ingepland op {a.OptimaalVeldNaam} en {b.OptimaalVeldNaam} om {aStart:HH\\:mm}.");
                        continue;
                    }
                    int gat = (int)(bStart.ToTimeSpan() - aEind.ToTimeSpan()).TotalMinutes;
                    if (gat >= 0 && gat < _bufferMinuten)
                        _conflicten.Add($"{team.Key}: tussen de wedstrijd op {a.OptimaalVeldNaam} en die op {b.OptimaalVeldNaam} zit {gat} min, minder dan de ingestelde buffer van {_bufferMinuten} min.");
                }
            }
        }
    }

    private static string NormaliseerVeld(string? veld) =>
        string.IsNullOrWhiteSpace(veld) ? "" : veld.Trim().ToLowerInvariant().Replace("  ", " ");

    private sealed class ElementRect
    {
        public double Left { get; set; }
        public double Width { get; set; }
        public double Top { get; set; }
        public double Height { get; set; }
    }

    // Gantt-blokken voor de directe veldbezetting-weergave (#566) — geen optimalisatie,
    // alleen wat er al gepland staat. Hergebruikt dezelfde GanttItem/helpers als de
    // Optimaliseer-Gantt hierboven.
    private List<GanttItem> BouwVeldbezettingGanttItems()
    {
        var items = new List<GanttItem>();
        foreach (var w in _veldbezetting.Where(w => w.DuurMinuten > 0))
        {
            if (string.IsNullOrWhiteSpace(w.Veld) || string.IsNullOrWhiteSpace(w.AanvangsTijd)) continue;
            if (!TimeOnly.TryParse(w.AanvangsTijd, out var t)) continue;
            var (veldBase, sub) = GanttSplitVeld(w.Veld);
            items.Add(new GanttItem(veldBase, sub, t, t.AddMinutes(w.DuurMinuten),
                w.Veldafmeting, GanttMatchLabel(w.Wedstrijd, w.TeamNaam), "ongewijzigd", w.DuurMinuten,
                null, null, WedstrijdCode: w.WedstrijdCode));
        }
        return items;
    }

    private static string GanttMatchLabel(string? wedstrijd, string teamNaam)
        => !string.IsNullOrWhiteSpace(wedstrijd) ? wedstrijd : teamNaam;

    // Alleen échte subposities van een gedeeld veld ("Kunstgras 1 A2") worden afgesplitst — precies de
    // waarden die de planner uitdeelt en die GanttTop kan positioneren.
    //
    // De oude check ("laatste deel is maximaal 2 tekens en alfanumeriek") herkende óók een gewoon
    // veldnummer als subpositie: "Kunstgras 1" werd ("Kunstgras", "1"). Gevolg was tweeledig — alle
    // velden kregen dezelfde basisnaam en vielen samen op één tijdlijnrij (#665), en bij het verslepen
    // naar een ander veld werd dat nummer als subpositie aan de nieuwe veldnaam geplakt ("Gras 1").
    private static readonly string[] SubPosLabels = ["A", "A1", "A2", "B", "B1", "B2"];

    private static string? GanttExtractSubPos(string? veld)
    {
        if (string.IsNullOrWhiteSpace(veld)) return null;
        var parts = veld.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var last = parts[^1];
        return SubPosLabels.Contains(last, StringComparer.OrdinalIgnoreCase) ? last.ToUpperInvariant() : null;
    }

    private static (string Base, string? SubPos) GanttSplitVeld(string veld)
    {
        var sub = GanttExtractSubPos(veld);
        if (sub == null) return (veld.Trim(), null);
        var idx = veld.LastIndexOf(' ');
        return idx > 0 ? (veld[..idx].Trim(), sub) : (veld.Trim(), null);
    }

    // Converteert een getal naar CSS-percentagestring (bijv. 42.5% → "42.50")
    private static string GanttPct(int deel, int totaal)
        => totaal > 0
            ? ((double)deel / totaal * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : "0.00";

    /// <summary>
    /// Horizontale uitlijning van een tijdlabel op de tijdas (#689).
    ///
    /// <para>
    /// Alle labels stonden op <c>translateX(-50%)</c>, dus gecentreerd op hun uurlijn. Voor het
    /// eerste en het laatste label valt daarmee de helft buiten de container: links vóór 0% en rechts
    /// ná 100%. Die paar pixels overschrijding zijn precies waarom er op een breed scherm nog een
    /// horizontale scrollbalk verscheen — de container was 14 pixels breder dan hij hoefde te zijn,
    /// terwijl de inhoud ruim paste.
    /// </para>
    ///
    /// <para>
    /// Het eerste label lijnt daarom links uit, het laatste rechts, en alles ertussen blijft
    /// gecentreerd. Geen enkel label wordt afgekapt en de scrollbalk is weg.
    /// </para>
    /// </summary>
    /// <summary>Uur-label op de Gantt-as. Bewust geen <c>TimeOnly.FromTimeSpan</c>: bij een wedstrijd die
    /// ná 23:00 eindigt loopt de as tot 1440 minuten, en dat is voor <c>TimeOnly</c> buiten bereik
    /// (ArgumentOutOfRangeException, #1107 bevinding 3). Stond tweemaal gedupliceerd in de markup.</summary>
    private static string GanttUurLabel(int minuut) => $"{minuut / 60:00}:{minuut % 60:00}";

    private static string GanttLabelTransform(int uur, int startMinuut, int eindMinuut)
        => uur <= startMinuut ? "translateX(0)"
         : uur >= eindMinuut  ? "translateX(-100%)"
         : "translateX(-50%)";

    // Top + hoogte van een Gantt-balk (#1194) staan sinds deze fix in BlazorAdmin/Services/GanttLayout.cs
    // — beide uit dezelfde subpositie-suffix, met een defensieve clamp. Losgetrokken zodat die
    // kernlogica unit-testbaar is zonder deze code-behind op te tuigen; zie die klasse voor de
    // volledige toelichting op de bug die dit oploste.

    private static string GanttTooltip(GanttItem gi)
    {
        var sb = gi.Label
            + "\n" + gi.Aanvang.ToString("HH:mm") + " – " + gi.Einde.ToString("HH:mm")
            + " (" + gi.DuurMinuten + " min)";
        if (gi.VoorkeurTijd != null)
        {
            sb += "\nVoorkeur: " + gi.VoorkeurTijd;
            if (gi.VoorkeurAfwijking == 0)
                sb += " ✓ op voorkeurstijd";
            else if (gi.VoorkeurAfwijking.HasValue)
                sb += " (" + (gi.VoorkeurAfwijking > 0 ? "+" : "") + gi.VoorkeurAfwijking + " min)";
        }
        return sb;
    }

    // Kleur van de voorkeurstijd-indicatorbalk bovenaan een Gantt-blok.
    // Leeg = geen voorkeur geconfigureerd → geen balk.
    private static string GanttVoorkeurBalkKleur(int? afwijking, string? voorkeurTijd)
    {
        if (voorkeurTijd == null || !afwijking.HasValue) return string.Empty;
        int abs = Math.Abs(afwijking.Value);
        if (abs == 0) return "#22c55e";    // Exact op voorkeurstijd → groen
        if (abs <= KleineAfwijkingDrempelMinuten) return "#f59e0b";   // Binnen 15 minuten → amber
        return "#ef4444";                  // Meer dan 15 min afwijking → rood
    }

    private static string GanttKleur(string status) => status switch
    {
        "wijziging"   => "#0d6efd",
        "nieuw-slot"  => "#d97706",
        "ongewijzigd" => "#198754",
        _ => "#6c757d"
    };

    private static string GanttLabel(string label)
    {
        // Toon alleen de teamnamen, strip " - FC Onbekend JO7 1" achtervoegsel als het lang is
        var idx = label.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0 && label.Length > 30) return label[..idx];
        return label;
    }
}
