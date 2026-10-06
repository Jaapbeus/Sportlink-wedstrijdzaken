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

        var teamVereist = lijst.Any(s => s.Code == TraceCodes.Classificatie
            && s.Details.TryGetValue(TraceBuilder.TeamVereistSleutel, out var v) && v == TraceBuilder.Ja);
        var teamOpgelost = lijst.Any(s => TeamStappen.Contains(s.Code) && s.Zekerheid == ZekerheidsNiveau.Zeker);
        var wedstrijdViaTegenstander = lijst.Any(s => s.Code == TraceCodes.OpponentPad
            && s.Details.TryGetValue("wedstrijdGevonden", out var g) && g == TraceBuilder.Ja
            && s.Details.TryGetValue("tak", out var t) && t == "opponent-op-datum");

        if (teamVereist && !teamOpgelost && !wedstrijdViaTegenstander)
        {
            var meerdere = lijst.Any(s => TeamStappen.Contains(s.Code)
                && s.Details.TryGetValue("bron", out var b) && b == "MeerdereKandidaten");
            redenen.Add(meerdere
                ? "Meerdere teams komen in aanmerking; er is niets gekozen"
                : "Het eigen team is niet herkend");
        }

        foreach (var s in lijst.Where(s => s.Code == TraceCodes.OpponentPad
            && s.Details.TryGetValue("wedstrijdGevonden", out var g) && g == TraceBuilder.Nee))
            redenen.Add("Via de tegenstander is geen wedstrijd gevonden");

        foreach (var s in lijst.Where(s => s.Code == TraceCodes.Sjabloon
            && s.Details.TryGetValue("sjabloon", out var sj) && (sj == "teamOnbekend" || sj == "datumOnbekend")))
            redenen.Add(s.Details["sjabloon"] == "teamOnbekend"
                ? "Het antwoord vraagt de afzender om het team"
                : "Het antwoord vraagt de afzender om de datum");

        foreach (var s in lijst.Where(s => s.Zekerheid == ZekerheidsNiveau.Mislukt && !TeamStappen.Contains(s.Code)))
            redenen.Add($"{s.Titel}: {s.Uitkomst}");

        var uniek = redenen.Distinct().ToList();
        return new ZekerheidsOordeel(uniek.Count == 0, uniek);
    }
}
