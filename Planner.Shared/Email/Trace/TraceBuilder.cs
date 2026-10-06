using System.Text;
using System.Text.RegularExpressions;

namespace Planner.Shared.Email.Trace;

/// <summary>
/// Bouwt een <see cref="BeslissingsTrace"/> op (#1568). Alle tekst loopt door <see cref="Saneer"/>:
/// de ene plek die bepaalt wat in een trace mag. De typed methoden accepteren bewust geen mailbody,
/// afzender of vrije tekst; teamschrijfwijzen zijn toegestaan (afgekapt op 80 tekens, zonder
/// control-chars, e-mailadressen en lange cijferreeksen).
/// </summary>
public sealed class TraceBuilder
{
    public const int MaxTekstLengte = 80;
    public const int MaxDetails = 20;
    internal const string Ja = "ja";
    internal const string Nee = "nee";
    internal const string TeamVereistSleutel = "teamVereist";

    private static readonly Regex EmailPatroon = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);
    private static readonly Regex NummerPatroon = new(@"\+?\d(?:[ .\-]?\d){8,}", RegexOptions.Compiled);

    private readonly List<TraceStap> _stappen = new();
    private int _sjabloonIndex = -1;

    /// <summary>Centrale sanitize-functie: control-chars weg, e-mail/nummers gemaskeerd, afgekapt.</summary>
    public static string Saneer(string? waarde, int maxLengte = MaxTekstLengte)
    {
        if (string.IsNullOrEmpty(waarde)) return "";
        var sb = new StringBuilder(waarde.Length);
        foreach (var c in waarde) sb.Append(char.IsControl(c) ? ' ' : c);
        var schoon = EmailPatroon.Replace(sb.ToString(), "[e-mail]");
        schoon = NummerPatroon.Replace(schoon, "[nummer]");
        schoon = Regex.Replace(schoon, @"\s+", " ").Trim();
        return schoon.Length <= maxLengte ? schoon : schoon[..maxLengte];
    }

    public IReadOnlyList<TraceStap> Stappen => _stappen;

    /// <summary>Voegt een stap toe; alle waarden worden gesaneerd en het aantal details begrensd.</summary>
    public TraceBuilder Voeg(string code, string titel, string uitkomst, ZekerheidsNiveau zekerheid,
        IEnumerable<KeyValuePair<string, string?>>? details = null)
    {
        var schoon = new Dictionary<string, string>();
        foreach (var d in details ?? Enumerable.Empty<KeyValuePair<string, string?>>())
        {
            if (schoon.Count >= MaxDetails) break;
            var sleutel = Saneer(d.Key, 40);
            if (sleutel.Length > 0) schoon[sleutel] = Saneer(d.Value);
        }
        _stappen.Add(new TraceStap(Saneer(code, 40), Saneer(titel, 120), Saneer(uitkomst, 120), zekerheid, schoon));
        return this;
    }

    private static KeyValuePair<string, string?> D(string sleutel, string? waarde) => new(sleutel, waarde);
    private static string JaNee(bool b) => b ? Ja : Nee;

    /// <summary>Of dit verzoektype een herkend eigen team nodig heeft om zeker te kunnen zijn.</summary>
    public static bool IsTeamVereist(string type) => type is "BeschikbaarheidCheck" or "HerplanVerzoek";

    /// <summary>Meldt hoeveel gevalideerde leermomenten als few-shot voorbeeld aan de classificatie zijn meegegeven.</summary>
    public TraceBuilder Leermomenten(int aantal)
        => Voeg(TraceCodes.Leermomenten, "Geleerde voorbeelden", aantal == 0 ? "Geen leermomenten meegegeven"
            : $"{aantal} leermoment(en) meegegeven aan de classificatie", ZekerheidsNiveau.Zeker,
            new[] { D("aantal", aantal.ToString()) });

    public TraceBuilder Classificatie(string type, string? team, string? tegenstander,
        int aantalDatums, string? aanvangsTijd)
        => Classificatie(type, !string.IsNullOrWhiteSpace(team), !string.IsNullOrWhiteSpace(tegenstander),
            aantalDatums, !string.IsNullOrWhiteSpace(aanvangsTijd));

    public TraceBuilder Classificatie(string type, bool teamAanwezig, bool tegenstanderAanwezig,
        int aantalDatums, bool tijdAanwezig)
        => Voeg(TraceCodes.Classificatie, "AI-classificatie", Saneer(type), ZekerheidsNiveau.Zeker, new[]
        {
            D("type", type), D(TeamVereistSleutel, JaNee(IsTeamVereist(type))),
            D("teamGenoemd", JaNee(teamAanwezig)), D("tegenstanderGenoemd", JaNee(tegenstanderAanwezig)),
            D("aantalDatums", aantalDatums.ToString()), D("tijdGenoemd", JaNee(tijdAanwezig))
        });

    /// <param name="code"><see cref="TraceCodes.TeamHerkenning"/>, <see cref="TraceCodes.TegenstanderHerkenning"/> of <see cref="TraceCodes.OpponentTeamHerkenning"/>.</param>
    /// <param name="bron">Naam van <c>ResolutionBron</c>; <c>null</c> bij een storing in de resolver.</param>
    public TraceBuilder TeamHerkenning(string code, string? ruweTekst, string? bron, double confidence,
        IEnumerable<string>? kandidaten, string? canoniekeNaam)
    {
        var kandidatenLijst = (kandidaten ?? Enumerable.Empty<string>()).Select(k => Saneer(k)).ToList();
        var titel = code switch
        {
            TraceCodes.TegenstanderHerkenning => "Tegenstander herkend als eigen team?",
            TraceCodes.OpponentTeamHerkenning => "Eigen team uit gevonden wedstrijd",
            _ => "Team herkend"
        };
        var opgelost = !string.IsNullOrWhiteSpace(canoniekeNaam);
        var geenTekst = string.IsNullOrWhiteSpace(ruweTekst);
        var niveau = opgelost ? ZekerheidsNiveau.Zeker
            : bron == "MeerdereKandidaten" ? ZekerheidsNiveau.Onzeker : ZekerheidsNiveau.Mislukt;
        var uitkomst = opgelost ? $"Herkend als {Saneer(canoniekeNaam)}"
            : bron == "MeerdereKandidaten" ? "Meerdere kandidaten, niets gekozen"
            : geenTekst ? "Geen team genoemd"
            : bron is null ? "Resolutie mislukt" : "Niet herkend";
        return Voeg(code, titel, uitkomst, niveau, new[]
        {
            D("ruweTekst", ruweTekst), D("bron", bron), D("confidence", confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
            D("aantalKandidaten", kandidatenLijst.Count.ToString()), D("kandidaten", string.Join(", ", kandidatenLijst)),
            D("canoniekeNaam", canoniekeNaam)
        });
    }

    public TraceBuilder TeamHerkenningOvergeslagen(string reden)
        => Voeg(TraceCodes.TeamHerkenning, "Team herkend", "Overgeslagen", ZekerheidsNiveau.Mislukt, new[] { D("reden", reden) });

    public TraceBuilder TeamWissel(string? nieuwTeam, string? nieuweTegenstander)
        => Voeg(TraceCodes.TeamWissel, "Team en tegenstander verwisseld", "Genoemd team was de tegenstander",
            ZekerheidsNiveau.Zeker, new[] { D("team", nieuwTeam), D("tegenstander", nieuweTegenstander) });

    /// <param name="tak"><c>opponent-op-datum</c>, <c>opponent-andere-datum</c> of <c>niet-gevonden</c>.</param>
    public TraceBuilder OpponentPad(string tak, bool wedstrijdGevonden)
        => Voeg(TraceCodes.OpponentPad, "Wedstrijd zoeken via tegenstander (#1139)",
            wedstrijdGevonden ? "Wedstrijd gevonden" : "Geen wedstrijd gevonden",
            wedstrijdGevonden ? ZekerheidsNiveau.Zeker : ZekerheidsNiveau.Onzeker,
            new[] { D("tak", tak), D("wedstrijdGevonden", JaNee(wedstrijdGevonden)) });

    public TraceBuilder Datum(IReadOnlyCollection<string> datums)
        => Voeg(TraceCodes.Datum, "Datumverwerking", datums.Count switch
            {
                0 => "Geen datum",
                1 => "Eén datum",
                _ => $"{datums.Count} datums"
            },
            datums.Count == 0 ? ZekerheidsNiveau.Onzeker : ZekerheidsNiveau.Zeker,
            new[] { D("aantalDatums", datums.Count.ToString()), D("datums", string.Join(", ", datums)) });

    /// <summary>
    /// Legt de gekozen plannertak en het standaard ingebouwde (generator-)sjabloon vast, afgeleid uit
    /// het plannerresponse. Een database-override vervangt dat sjabloon later via <see cref="SjabloonOverride"/>.
    /// </summary>
    public TraceBuilder Antwoordkeuze(string type, string plannerResponseJson)
    {
        var tak = SjabloonSleutel.PlannerTak(plannerResponseJson);
        Voeg(TraceCodes.Tak, "Gekozen verwerkingstak", tak, ZekerheidsNiveau.Zeker, new[] { D("plannerResponseVlag", tak) });
        var sleutel = SjabloonSleutel.Bepaal(type, plannerResponseJson);
        _sjabloonIndex = _stappen.Count;
        return Voeg(TraceCodes.Sjabloon, "Antwoordsjabloon", $"Ingebouwd sjabloon {sleutel}", ZekerheidsNiveau.Zeker,
            new[] { D("sjabloon", sleutel), D("bron", "generator") });
    }

    /// <summary>Meldt dat een sjabloon uit de database (override) is gebruikt; vervangt het ingebouwde sjabloon.</summary>
    public TraceBuilder SjabloonOverride(string sleutel)
    {
        var index = _sjabloonIndex;
        if (index >= 0) _stappen.RemoveAt(index);
        _sjabloonIndex = -1;
        Voeg(TraceCodes.Sjabloon, "Antwoordsjabloon", $"Databasesjabloon {Saneer(sleutel)}", ZekerheidsNiveau.Zeker,
            new[] { D("sjabloon", sleutel), D("bron", "database-override") });
        return this;
    }

    /// <summary>Sluit af: voegt het eindoordeel toe en levert de trace.</summary>
    public BeslissingsTrace Bouw()
    {
        var oordeel = ZekerheidsBeoordeling.Beoordeel(_stappen);
        var stappen = new List<TraceStap>(_stappen)
        {
            new(TraceCodes.Eindoordeel, "Eindoordeel", oordeel.IsZeker ? "Zeker" : "Onzeker",
                oordeel.IsZeker ? ZekerheidsNiveau.Zeker : ZekerheidsNiveau.Onzeker,
                new Dictionary<string, string> { ["redenen"] = Saneer(string.Join("; ", oordeel.Redenen), 300) })
        };
        return new BeslissingsTrace(stappen, oordeel);
    }
}

/// <summary>Null-veilige hulpmethoden zodat tierbestanden geen eigen trace-vertakkingen nodig hebben.</summary>
public static class TraceBuilderExtensies
{
    /// <summary>Meldt een sjabloon-override en geeft het antwoord ongewijzigd terug (past in een <c>return</c>).</summary>
    public static T MeldOverride<T>(this TraceBuilder? trace, string sleutel, T antwoord)
    {
        trace?.SjabloonOverride(sleutel);
        return antwoord;
    }
}
