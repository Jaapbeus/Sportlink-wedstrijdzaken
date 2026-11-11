namespace Planner.Shared;

public static partial class PlannerShared
{
    /// <summary>Finds the latest fitting earlier slot on every field, without stopping at the first occupancy.</summary>
    public static List<CandidateSlot> FindLatestFitPerField(
        List<VeldBeschikbaarheidInfo> availableFields,
        List<BestaandeWedstrijd> occupations,
        Dictionary<string, List<TeamRegel>> allTeamRules,
        List<TeamRegel> requestingTeamRules,
        decimal veldFractie, int duurMinuten,
        TimeOnly upperBound, TimeOnly windowStart)
    {
        var result = new List<CandidateSlot>();
        if (duurMinuten <= 0 || duurMinuten > 1440) return result;

        int upperBoundMinute = (int)Math.Floor(upperBound.ToTimeSpan().TotalMinutes);
        foreach (var field in availableFields)
        {
            int startMinute = Math.Max((int)Math.Ceiling(windowStart.ToTimeSpan().TotalMinutes),
                (int)Math.Ceiling(field.BeschikbaarVanaf.ToTimeSpan().TotalMinutes));
            int endMinute = Math.Min(upperBoundMinute,
                (int)Math.Floor(field.BeschikbaarTot.ToTimeSpan().TotalMinutes));
            int latestStartMinute = endMinute - duurMinuten;
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();

            for (int minute = latestStartMinute; minute >= startMinute; minute -= 5)
            {
                var start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute));
                var end = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute + duurMinuten));
                if (!CanFitMatch(start, end, veldFractie, field.VeldNummer,
                        fieldOccs, allTeamRules, requestingTeamRules)) continue;

                result.Add(new CandidateSlot
                {
                    VeldNummer = field.VeldNummer,
                    AanvangsTijd = start,
                    EindTijd = end
                });
                break;
            }
        }
        return result;
    }

}
