using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Een keuze in de Velddeel-dropdown: de <c>FieldSize</c>-waarde en het label.</summary>
public sealed record VelddeelKeuze(string Waarde, string Label);

/// <summary>
/// De invoer van het formulier "Wedstrijd aanmaken" (#1437), los van de Razor-pagina zodat de
/// voorinvulling en "Leegmaken" testbaar zijn (er is geen bUnit). De pagina bindt rechtstreeks aan
/// deze eigenschappen; alleen het kiezen van een team heeft een bijwerking: het vult duur,
/// leeftijdscategorie en velddeel voor, die de gebruiker daarna nog kan aanpassen.
/// </summary>
public sealed class OefenwedstrijdFormulierState
{
    /// <summary>Sentinel-waarde van de "Vrije tekst"-optie in de Team-dropdown (#1396).</summary>
    public const string VrijeTekstOptie = "__vrije-tekst__";

    public const string StandaardTijd = "19:00";
    public const int StandaardDuur = 90;
    public const string HeelVeld = "1.0";

    /// <summary>De vier velddelen. AANNAME: alleen "1.0" is live bevestigd bij Sportlink, de rest is geëxtrapoleerd (#1437).</summary>
    public static readonly IReadOnlyList<VelddeelKeuze> Velddelen = new[]
    {
        new VelddeelKeuze(HeelVeld, "Heel veld"),
        new VelddeelKeuze("0.5", "Half veld"),
        new VelddeelKeuze("0.25", "Kwart veld"),
        new VelddeelKeuze("0.125", "Achtste veld"),
    };

    private Dictionary<string, OefenwedstrijdFormulierTeamDto> _teamInfo = new(StringComparer.OrdinalIgnoreCase);
    private string? _teamSelectie;

    public OefenwedstrijdFormulierState() => Leegmaken();

    public DateTime Datum { get; set; }
    public string? Tijd { get; set; }
    public int Duur { get; set; }
    public string? TeamVrijeTekst { get; set; }
    public string? Tegenstander { get; set; }
    public int? VeldNummer { get; set; }
    public string Velddeel { get; set; } = HeelVeld;
    /// <summary>Sportlink-<c>Id</c> van de gekozen leeftijdscategorie; leeg = "— Sportlink-standaard —".</summary>
    public string AgeClassCode { get; set; } = "";
    public string? Omschrijving { get; set; }

    /// <summary>De waarde van de Team-dropdown. Een team kiezen vult duur, leeftijdscategorie en velddeel voor (als bekend).</summary>
    public string? TeamSelectie
    {
        get => _teamSelectie;
        set { _teamSelectie = value; PasTeamToe(); }
    }

    public bool IsVrijeTekst => _teamSelectie == VrijeTekstOptie;

    /// <summary>De daadwerkelijk te gebruiken teamnaam: uit de dropdown, of het vrije-tekstveld.</summary>
    public string? TeamNaam => IsVrijeTekst ? TeamVrijeTekst : _teamSelectie;

    /// <summary>Per team de voorinvulling die de server leverde; vervangt eerdere gegevens.</summary>
    public void ZetTeamGegevens(IEnumerable<OefenwedstrijdFormulierTeamDto> teams)
        => _teamInfo = teams.GroupBy(t => t.TeamNaam, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Alles terug naar de beginstand: vandaag, 19:00, 90 minuten, heel veld en de rest leeg. De geladen teamgegevens blijven.</summary>
    public void Leegmaken()
    {
        Datum = DateTime.Today;
        Tijd = StandaardTijd;
        Duur = StandaardDuur;
        _teamSelectie = null;
        TeamVrijeTekst = null;
        Tegenstander = null;
        VeldNummer = null;
        Velddeel = HeelVeld;
        AgeClassCode = "";
        Omschrijving = null;
    }

    /// <summary>Na een geslaagde aanmaak: de wedstrijdspecifieke velden leeg, datum/tijd/team/veld blijven staan voor de volgende.</summary>
    public void NaGeslaagdeAanmaak()
    {
        Tegenstander = null;
        Omschrijving = null;
    }

    /// <summary>Waar de aangemaakte wedstrijd in Sportlink Club te openen is; de id wordt URL-veilig gemaakt.</summary>
    public static string SportlinkWedstrijdUrl(string publicMatchId)
        => "https://club.sportlink.com/competition-affairs/match-details/" + Uri.EscapeDataString(publicMatchId.Trim());

    private void PasTeamToe()
    {
        if (_teamSelectie == null || IsVrijeTekst || !_teamInfo.TryGetValue(_teamSelectie, out var team)) return;
        if (team.Duur is >= 1 and <= 240) Duur = team.Duur.Value;
        AgeClassCode = team.AgeClassCode ?? "";
        Velddeel = Velddelen.Any(v => v.Waarde == team.Veldafmeting) ? team.Veldafmeting! : HeelVeld;
    }
}
