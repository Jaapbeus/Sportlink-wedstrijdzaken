namespace Planner.Shared.Email.Trace;

/// <summary>Eindoordeel over een trace: kan dit antwoord zonder menselijke review de deur uit?</summary>
public sealed record ZekerheidsOordeel(bool IsZeker, IReadOnlyList<string> Redenen);

/// <summary>
/// Pure beoordeling van een reeks trace-stappen (#1568). Bedoeld als zekerheidspoort (deel D):
/// "onzeker" betekent dat een mens het antwoord moet zien vóór verzending. Conservatief: bij
/// twijfel onzeker.
/// </summary>
public static class ZekerheidsBeoordeling
{
    private static readonly string[] TeamStappen =
    {
        TraceCodes.TeamHerkenning, TraceCodes.TegenstanderHerkenning, TraceCodes.OpponentTeamHerkenning
    };

    public static ZekerheidsOordeel Beoordeel(IEnumerable<TraceStap> stappen)
    {
        var lijst = stappen.Where(s => s.Code is not (TraceCodes.Eindoordeel or TraceCodes.Zekerheidspoort)).ToList();
        var redenen = new List<string>();
        redenen.AddRange(TeamRedenen(lijst));
        redenen.AddRange(OpponentRedenen(lijst));
        redenen.AddRange(HerplanRedenen(lijst));
        redenen.AddRange(SjabloonRedenen(lijst));
        redenen.AddRange(MislukteStapRedenen(lijst));

        var uniek = redenen.Distinct().ToList();
        return new ZekerheidsOordeel(uniek.Count == 0, uniek);
    }

    private static bool HeeftDetail(TraceStap s, string sleutel, string waarde)
        => s.Details.TryGetValue(sleutel, out var v) && v == waarde;

    private static IEnumerable<string> TeamRedenen(List<TraceStap> lijst)
    {
        var teamVereist = lijst.Any(s => s.Code == TraceCodes.Classificatie && HeeftDetail(s, TraceBuilder.TeamVereistSleutel, TraceBuilder.Ja));
        var teamOpgelost = lijst.Any(s => TeamStappen.Contains(s.Code) && s.Zekerheid == ZekerheidsNiveau.Zeker);
        var wedstrijdViaTegenstander = lijst.Any(s => s.Code == TraceCodes.OpponentPad
            && HeeftDetail(s, "wedstrijdGevonden", TraceBuilder.Ja) && HeeftDetail(s, "tak", "opponent-op-datum"));

        if (!teamVereist || teamOpgelost || wedstrijdViaTegenstander) yield break;

        var meerdere = lijst.Any(s => TeamStappen.Contains(s.Code) && HeeftDetail(s, "bron", "MeerdereKandidaten"));
        yield return meerdere
            ? "Meerdere teams komen in aanmerking; er is niets gekozen"
            : "Het eigen team is niet herkend";
    }

    private static IEnumerable<string> OpponentRedenen(List<TraceStap> lijst)
        => lijst.Where(s => s.Code == TraceCodes.OpponentPad && HeeftDetail(s, "wedstrijdGevonden", TraceBuilder.Nee))
            .Select(_ => "Via de tegenstander is geen wedstrijd gevonden");

    /// <summary>Een herplanverzoek zonder gevonden wedstrijd of zonder team/datum is geen bruikbare uitkomst (review M3).</summary>
    private static IEnumerable<string> HerplanRedenen(List<TraceStap> lijst)
        => lijst.Where(s => s.Code == TraceCodes.HerplanUitkomst
                && s.Details.TryGetValue("uitkomst", out var u) && u != "gelukt")
            .Select(s => s.Details["uitkomst"] == "geen-wedstrijd"
                ? "Voor het herplanverzoek is geen wedstrijd gevonden"
                : "Het herplanverzoek mist team of datum");

    /// <summary>
    /// Een antwoord dat de afzender om het team of de datum vraagt gaat ter review. De trace noemt het ingebouwde
    /// antwoord bij de naam van de plannervlag (<c>teamOnbekend</c>), een databasesjabloon bij zijn sleutel
    /// (<c>team_onbekend</c>, #1780); beide tellen, anders ontsnapt het antwoord zodra de club een eigen tekst instelt.
    /// </summary>
    private static IEnumerable<string> SjabloonRedenen(List<TraceStap> lijst)
    {
        foreach (var s in lijst.Where(s => s.Code == TraceCodes.Sjabloon))
        {
            if (HeeftDetail(s, "sjabloon", "teamOnbekend") || HeeftDetail(s, "sjabloon", AntwoordSjabloonSleutels.TeamOnbekend))
                yield return "Het antwoord vraagt de afzender om het team";
            else if (HeeftDetail(s, "sjabloon", "datumOnbekend") || HeeftDetail(s, "sjabloon", AntwoordSjabloonSleutels.DatumOnbekend))
                yield return "Het antwoord vraagt de afzender om de datum";
        }
    }

    private static IEnumerable<string> MislukteStapRedenen(List<TraceStap> lijst)
        => lijst.Where(s => s.Zekerheid == ZekerheidsNiveau.Mislukt && !TeamStappen.Contains(s.Code))
            .Select(s => $"{s.Titel}: {s.Uitkomst}");
}
