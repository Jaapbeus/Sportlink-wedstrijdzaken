namespace Planner.Shared;

// Buffer- en capaciteitshulpen van CanFitMatch (#1625): uit FieldScheduler.cs gehaald zodat de
// controle in kleine benoemde stappen leest. Pure functies, geen gedragswijziging.
public static partial class PlannerShared
{
    /// <summary>
    /// Effectieve buffer (minuten) voor en na een wedstrijd: de standaardbuffer, verruimd door de
    /// <c>BufferVoor</c>/<c>BufferNa</c>-teamregels met een waarde. Een kleinere regel verkleint nooit.
    /// </summary>
    private static (int Voor, int Na) BufferVoorEnNa(IEnumerable<TeamRegel> teamRegels)
    {
        int voor = StandardBufferMinutes;
        int na   = StandardBufferMinutes;
        foreach (var rule in teamRegels)
        {
            if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                voor = Math.Max(voor, rule.WaardeMinuten.Value);
            if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                na = Math.Max(na, rule.WaardeMinuten.Value);
        }
        return (voor, na);
    }

    /// <summary>
    /// Buffers van een bezetter: uit de teamregels van zijn team, of de standaardbuffer als de
    /// teamnaam ontbreekt of geen regels heeft.
    /// </summary>
    private static (int Voor, int Na) BufferVoorEnNaVanBezetter(
        BestaandeWedstrijd bezetter, Dictionary<string, List<TeamRegel>> allTeamRules)
        => !string.IsNullOrEmpty(bezetter.TeamNaam) && allTeamRules.TryGetValue(bezetter.TeamNaam, out var regels)
            ? BufferVoorEnNa(regels)
            : (StandardBufferMinutes, StandardBufferMinutes);

    /// <summary>
    /// Hoogste gelijktijdige deelveld-bezetting binnen [start, einde), per blok van 5 minuten.
    /// Alleen bezettingen kleiner dan een heel veld tellen mee.
    /// </summary>
    private static decimal MaxDeelCapaciteit(TimeOnly start, TimeOnly einde, List<BestaandeWedstrijd> bezettingen)
    {
        decimal maxCap = 0;
        for (var t = start; t < einde; t = t.AddMinutes(5))
        {
            var te = t.AddMinutes(5);
            decimal cap = bezettingen
                .Where(o => o.VeldDeelGebruik < 1.0m && o.AanvangsTijd < te && o.EindTijd > t)
                .Sum(o => o.VeldDeelGebruik);
            maxCap = Math.Max(maxCap, cap);
        }
        return maxCap;
    }
}
