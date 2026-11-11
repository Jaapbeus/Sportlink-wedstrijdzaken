namespace BlazorAdmin.Models;

// ── Dagplanning ──
// De DTO's voor het losse "klassiek optimaliseren"-pad (OptimaliseerRequestDto/ResponseDto,
// OptimalisatieSuggestieDto, VeldCapaciteitDto) zijn vervallen bij #666, samen met dat endpoint.
// Er is nu één optimalisatie: auto-plan.

// ── Auto-plan (#380) ──

public class AutoPlanRequestDto
{
    public string Datum { get; set; } = "";
    public int? BufferMinuten { get; set; }
}

/// <summary>#1460: body van <c>POST planner/auto-plan/deel</c> — de planning zoals getoond (geen persoonsgegevens).</summary>
public class AutoPlanDeelRequestDto
{
    public string Datum { get; set; } = "";
    public string Tab { get; set; } = "huidig";
    public List<AutoPlanDeelRegelDto> Wedstrijden { get; set; } = new();
}

public class AutoPlanDeelRegelDto
{
    public string TeamNaam { get; set; } = "";
    public string Wedstrijd { get; set; } = "";
    public string? Competitiesoort { get; set; }
    public string? Tijd { get; set; }
    public string? Veld { get; set; }
}

public class AutoPlanResponseDto
{
    public string Datum { get; set; } = "";
    public int TotaalWedstrijden { get; set; }
    public int ZonderVeld { get; set; }
    public int ZonderTijd { get; set; }
    public int TeWijzigen { get; set; }
    public int NietInplanbaar { get; set; }
    public string? GeschatteEindTijd { get; set; }
    public List<AutoPlanWedstrijdItemDto> Wedstrijden { get; set; } = new();
    public string HuidigeHtml { get; set; } = "";
    public string OptimaleHtml { get; set; } = "";
}

// Lichtgewicht "wat staat er nu gepland"-weergave (#566) — zonder optimalisatie-berekening.
public class VeldbezettingItemDto
{
    public long? WedstrijdCode { get; set; }
    public string Wedstrijd { get; set; } = "";
    public string TeamNaam { get; set; } = "";
    public string? Uitteam { get; set; }
    public string? AanvangsTijd { get; set; }
    public string? Veld { get; set; }
    public string? Competitiesoort { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public int DuurMinuten { get; set; }
    public decimal Veldafmeting { get; set; }
    /// <summary>De andere kant van de wedstrijd ten opzichte van <see cref="TeamNaam"/> (#1582).</summary>
    public string? Tegenstander { get; set; }
    /// <summary>Onze eigen regel die de Sportlink-veldplanner niet kent: een afwijking (#1582).</summary>
    public bool NietInSportlink { get; set; }
    /// <summary>"Sportlink" als de regel uit de veldplanner komt (#1582).</summary>
    public string? Bron { get; set; }
}

/// <summary>Of de Sportlink-veldplanner voor deze dag echt is geraadpleegd (#1774). Bij <c>Geraadpleegd = false</c>
/// toont Planning een rode waarschuwing: een lege of onvolledige dag mag niet als "vrij" lezen.</summary>
public class SportlinkVeldplannerStatusDto
{
    public bool Geraadpleegd { get; set; }
    /// <summary>Reden-code, o.a. Ok, Democlub, KoppelingVerlopen, NetwerkFout (#1774).</summary>
    public string Reden { get; set; } = "";
    public string? Toelichting { get; set; }
    /// <summary>UTC-tijdstip van de raadpleging; alleen gevuld als <see cref="Geraadpleegd"/> waar is.</summary>
    public DateTime? GeraadpleegdOpUtc { get; set; }
    /// <summary>De Sportlink-koppeling moet via de beheerpagina worden hersteld.</summary>
    public bool KoppelingHerstelNodig { get; set; }
}

/// <summary>Antwoord van <c>GET api/planner/veldbezetting</c> (#1774): de wedstrijdregels plus de Sportlink-status.</summary>
public class VeldbezettingAntwoordDto
{
    public SportlinkVeldplannerStatusDto? Sportlink { get; set; }
    public List<VeldbezettingItemDto> Wedstrijden { get; set; } = new();
}

public class AutoPlanToepassenResponseDto
{
    public int Bijgewerkt { get; set; }
    public int Mislukt { get; set; }
    public List<string> Fouten { get; set; } = new();
}
