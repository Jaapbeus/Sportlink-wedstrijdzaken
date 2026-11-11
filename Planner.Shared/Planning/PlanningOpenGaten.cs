namespace Planner.Shared.Planning;

public static class PlanningOpenGaten
{
    public static List<(TimeOnly Van, TimeOnly Tot, int MaxDuurMinuten)> Vind(
        TimeOnly vanaf, TimeOnly tot, IEnumerable<BestaandeWedstrijd> bezettingen, int bufferMinuten)
    {
        var gaten = new List<(TimeOnly, TimeOnly, int)>();
        var start = vanaf.ToTimeSpan();
        var einde = tot.ToTimeSpan();
        if (start >= einde) return gaten;

        foreach (var bezet in bezettingen.OrderBy(b => b.AanvangsTijd))
        {
            var bezetVan = bezet.AanvangsTijd.ToTimeSpan() - TimeSpan.FromMinutes(bufferMinuten);
            var bezetTot = bezet.EindTijd.ToTimeSpan() + TimeSpan.FromMinutes(bufferMinuten);
            if (bezetTot <= start || bezetVan >= einde) continue;

            var gatTot = bezetVan < einde ? bezetVan : einde;
            if (gatTot - start >= TimeSpan.FromMinutes(30))
                gaten.Add((TimeOnly.FromTimeSpan(start), TimeOnly.FromTimeSpan(gatTot),
                    (int)(gatTot - start).TotalMinutes));
            if (bezetTot > start) start = bezetTot < einde ? bezetTot : einde;
        }

        if (einde - start >= TimeSpan.FromMinutes(30))
            gaten.Add((TimeOnly.FromTimeSpan(start), TimeOnly.FromTimeSpan(einde),
                (int)(einde - start).TotalMinutes));
        return gaten;
    }
}
