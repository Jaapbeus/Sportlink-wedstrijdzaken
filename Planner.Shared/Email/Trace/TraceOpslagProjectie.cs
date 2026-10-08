using System.Text;
using System.Text.RegularExpressions;

namespace Planner.Shared.Email.Trace;

/// <summary>
/// De ene plek die bepaalt wat van een <see cref="BeslissingsTrace"/> PERMANENT wordt bewaard
/// (<c>planner.EmailTrace</c>, #1568, Codex R1-F1). Een allowlist: per stapcode de toegestane
/// detailsleutels, elk met een strikte waardevorm. Alles wat niet op de lijst staat, of niet in
/// de verwachte vorm past, wordt bij opslag weggelaten — dus ook ruwe, door de AI uit de mail
/// gehaalde tekst (teamschrijfwijzen, tegenstander). Daarvoor in de plaats komt een vormkenmerk
/// (<see cref="Vormkenmerk"/>) en de gevalideerde canonieke teamnaam. De niet-herkende tekst zelf
/// leeft uitsluitend in de wachtrij met begrensde retentie (<see cref="OnbekendeTeamTekstExtractie"/>).
/// De e-mailtester toont de volledige trace transiënt; die loopt NIET door deze projectie.
/// </summary>
public static class TraceOpslagProjectie
{
    internal const string Streepje = "—";

    private static readonly Regex JaNee = new("^(ja|nee)$", RegexOptions.Compiled);
    private static readonly Regex Getal = new(@"^\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex Zekerheidswaarde = new(@"^\d(\.\d{1,2})?$", RegexOptions.Compiled);
    private static readonly Regex Sleutel = new(@"^[A-Za-z0-9_.\-]{1,60}$", RegexOptions.Compiled);
    private static readonly Regex Woord = new("^[A-Za-z_]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex Bron = new("^(ExacteAlias|ExacteMatch|MeerdereKandidaten|AiDisambiguatie|Onopgelost)$", RegexOptions.Compiled);
    private static readonly Regex Datums = new(@"^\d{4}-\d{2}-\d{2}(, \d{4}-\d{2}-\d{2}){0,19}$", RegexOptions.Compiled);
    private static readonly Regex VormPatroon = new(@"^(\d{1,3}|>\d{1,3}) tekens: [a-z+\- ]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex CanoniekeNaam = new(@"^[^\r\n]{1,80}$", RegexOptions.Compiled);

    private sealed record Regel(string[] Titels, Regex Uitkomst, IReadOnlyDictionary<string, Regex> Details);

    private static Regex Vast(params string[] teksten)
        => new("^(" + string.Join("|", teksten.Select(Regex.Escape)) + ")$", RegexOptions.Compiled);

    private static readonly Dictionary<string, Regex> Herkenningsdetails = new()
    {
        ["bron"] = Bron, ["confidence"] = Zekerheidswaarde, ["aantalKandidaten"] = Getal,
        ["kandidaten"] = CanoniekeNaam, ["canoniekeNaam"] = CanoniekeNaam, ["vorm"] = VormPatroon,
        ["reden"] = Vast("geen clubcode")
    };

    private static readonly Regex HerkenningUitkomst = Vast(
        "Meerdere kandidaten, niets gekozen", "Geen team genoemd", "Resolutie mislukt", "Niet herkend", "Overgeslagen");

    private static readonly Dictionary<string, Regel> Regels = new()
    {
        [TraceCodes.Classificatie] = new(["AI-classificatie"], Woord, new Dictionary<string, Regex>
        {
            ["type"] = Woord, [TraceBuilder.TeamVereistSleutel] = JaNee, ["teamGenoemd"] = JaNee,
            ["tegenstanderGenoemd"] = JaNee, ["aantalDatums"] = Getal, ["tijdGenoemd"] = JaNee
        }),
        [TraceCodes.Leermomenten] = new(["Geleerde voorbeelden"],
            new(@"^(Geen leermomenten meegegeven|\d{1,4} leermoment\(en\) meegegeven aan de classificatie)$", RegexOptions.Compiled),
            new Dictionary<string, Regex> { ["aantal"] = Getal }),
        [TraceCodes.TeamHerkenning] = new(["Team herkend"], HerkenningUitkomst, Herkenningsdetails),
        [TraceCodes.TegenstanderHerkenning] = new(["Tegenstander herkend als eigen team?"], HerkenningUitkomst, Herkenningsdetails),
        [TraceCodes.OpponentTeamHerkenning] = new(["Eigen team uit gevonden wedstrijd"], HerkenningUitkomst, Herkenningsdetails),
        [TraceCodes.TeamWissel] = new(["Team en tegenstander verwisseld"], Vast("Genoemd team was de tegenstander"),
            new Dictionary<string, Regex> { ["team"] = CanoniekeNaam, ["tegenstanderVorm"] = VormPatroon }),
        [TraceCodes.OpponentPad] = new(["Wedstrijd zoeken via tegenstander (#1139)"],
            Vast("Wedstrijd gevonden", "Geen wedstrijd gevonden"),
            new Dictionary<string, Regex>
            {
                ["tak"] = Vast("opponent-op-datum", "opponent-andere-datum", "niet-gevonden"), ["wedstrijdGevonden"] = JaNee
            }),
        [TraceCodes.Datum] = new(["Datumverwerking"], new(@"^(Geen datum|Eén datum|\d{1,3} datums)$", RegexOptions.Compiled),
            new Dictionary<string, Regex> { ["aantalDatums"] = Getal, ["datums"] = Datums }),
        [TraceCodes.Tak] = new(["Gekozen verwerkingstak"], new("^([A-Za-z_]{1,40}|Niet verwerkt door de planner)$", RegexOptions.Compiled),
            new Dictionary<string, Regex>
            {
                ["plannerResponseVlag"] = Woord, ["reden"] = Vast(EmailTraceOpslag.BuitenScopeReden)
            }),
        [TraceCodes.HerplanUitkomst] = new(["Herplanverzoek: wedstrijd en datum"],
            Vast("Geen wedstrijd gevonden voor team en datum", "Team of datum ontbreekt voor het herplanverzoek", "Wedstrijd gevonden"),
            new Dictionary<string, Regex>
            {
                ["uitkomst"] = Vast("gelukt", "geen-wedstrijd", "onvoldoende-gegevens"), ["wedstrijdGevonden"] = JaNee,
                ["datumAanwezig"] = JaNee, ["sjabloon"] = Sleutel
            }),
        [TraceCodes.Sjabloon] = new(["Antwoordsjabloon"], new(@"^(Ingebouwd sjabloon|Databasesjabloon) [A-Za-z0-9_.\-]{1,60}$", RegexOptions.Compiled),
            new Dictionary<string, Regex> { ["sjabloon"] = Sleutel, ["bron"] = Vast("generator", "database-override") }),
        [TraceCodes.Eindoordeel] = new(["Eindoordeel"], Vast("Zeker", "Onzeker"), new Dictionary<string, Regex>()),
        [TraceCodes.Zekerheidspoort] = new(["Zekerheidspoort"],
            Vast("Tegengehouden: antwoord niet naar de afzender", "Poort staat uit: antwoord toch verstuurd"),
            new Dictionary<string, Regex> { ["poortActief"] = JaNee }),
        [TraceCodes.VerwerkingFout] = new(["Verwerking"], Vast("Mislukt (zie e-mail-log)"), new Dictionary<string, Regex>())
    };

    /// <summary>Detailsleutels die voor deze stapcode op de allowlist staan (leeg bij een onbekende code).</summary>
    public static IReadOnlyCollection<string> ToegestaneSleutels(string code)
        => !Regels.TryGetValue(code, out var r) ? []
            : code is TraceCodes.Eindoordeel or TraceCodes.Zekerheidspoort ? r.Details.Keys.Append("redenen").ToList()
            : r.Details.Keys.ToList();

    /// <summary>Alle stapcodes die opgeslagen mogen worden; een onbekende stap wordt weggelaten.</summary>
    public static IReadOnlyCollection<string> ToegestaneStappen => Regels.Keys;

    public static BeslissingsTrace Projecteer(BeslissingsTrace trace)
    {
        var stappen = trace.Stappen.Where(s => Regels.ContainsKey(s.Code)).Select(ProjecteerStap).ToList();

        // De redenen worden opnieuw afgeleid uit de al geprojecteerde stappen, zodat ze nooit tekst dragen die de projectie wegliet.
        var redenen = ZekerheidsBeoordeling.Beoordeel(stappen).Redenen;
        var samengevat = TraceBuilder.Saneer(string.Join("; ", redenen), 300);
        for (var i = 0; i < stappen.Count; i++)
        {
            if (stappen[i].Code is not (TraceCodes.Eindoordeel or TraceCodes.Zekerheidspoort)) continue;
            var details = new Dictionary<string, string>(stappen[i].Details) { ["redenen"] = samengevat };
            stappen[i] = stappen[i] with { Details = details };
        }
        return new BeslissingsTrace(stappen, new ZekerheidsOordeel(trace.Oordeel.IsZeker, redenen));
    }

    private static TraceStap ProjecteerStap(TraceStap stap)
    {
        var regel = Regels[stap.Code];
        var bron = AfgeleideDetails(stap);
        var details = new Dictionary<string, string>();
        foreach (var (sleutel, patroon) in regel.Details)
            if (bron.TryGetValue(sleutel, out var waarde) && patroon.IsMatch(waarde)) details[sleutel] = waarde;

        var titel = regel.Titels.Contains(stap.Titel) ? stap.Titel : regel.Titels[0];
        return stap with { Titel = titel, Uitkomst = Uitkomst(stap, regel, details), Details = details };
    }

    /// <summary>Details plus de veilige afgeleiden (vormkenmerken) van de ruwe tekst die zelf nooit wordt bewaard.</summary>
    private static Dictionary<string, string> AfgeleideDetails(TraceStap stap)
    {
        var d = new Dictionary<string, string>(stap.Details);
        if (stap.Details.TryGetValue("ruweTekst", out var ruw) && !string.IsNullOrWhiteSpace(ruw)) d["vorm"] = Vormkenmerk(ruw);
        if (stap.Code == TraceCodes.TeamWissel && stap.Details.TryGetValue("tegenstander", out var t) && !string.IsNullOrWhiteSpace(t))
            d["tegenstanderVorm"] = Vormkenmerk(t);
        return d;
    }

    private static string Uitkomst(TraceStap stap, Regel regel, Dictionary<string, string> details)
    {
        const string herkend = "Herkend als ";
        if (stap.Code is TraceCodes.TeamHerkenning or TraceCodes.TegenstanderHerkenning or TraceCodes.OpponentTeamHerkenning
            && stap.Uitkomst.StartsWith(herkend, StringComparison.Ordinal))
            return details.TryGetValue("canoniekeNaam", out var naam) ? herkend + naam : "Herkend";
        return regel.Uitkomst.IsMatch(stap.Uitkomst) ? stap.Uitkomst : Streepje;
    }

    /// <summary>
    /// Vormkenmerk van een tekst zonder de tekst zelf: lengte en soorten tekens, bijvoorbeeld
    /// <c>6 tekens: letters+cijfers+streepje</c>. Genoeg om te zien waarom iets niet herkend werd.
    /// </summary>
    public static string Vormkenmerk(string tekst)
    {
        var soorten = new List<string>();
        void Voeg(bool aanwezig, string naam) { if (aanwezig) soorten.Add(naam); }
        Voeg(tekst.Any(char.IsLetter), "letters");
        Voeg(tekst.Any(char.IsDigit), "cijfers");
        Voeg(tekst.Any(char.IsWhiteSpace), "spaties");
        Voeg(tekst.Contains('-'), "streepje");
        Voeg(tekst.Contains('/'), "schuine-streep");
        Voeg(tekst.Contains('.'), "punt");
        Voeg(tekst.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not ('-' or '/' or '.')), "overig");
        var lengte = tekst.Trim().Length;
        var lengteTekst = lengte > 100 ? ">100" : lengte.ToString();
        return new StringBuilder().Append(lengteTekst).Append(" tekens: ").Append(string.Join("+", soorten)).ToString();
    }
}
