namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor teamrooster-opvragen.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class TeamScheduleService
{
    public static async Task<TeamScheduleResponse?> GetTeamScheduleAsync(string team)
    {
        if (!await PlannerDataAccess.TeamExistsAsync(team))
            return null;

        var seizoenEinde = await PlannerDataAccess.GetSeasonEndDateAsync()
            ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(3));
        var vandaag     = DateOnly.FromDateTime(DateTime.Today);
        var wedstrijden = await PlannerDataAccess.GetFutureMatchesForTeamAsync(team, vandaag, seizoenEinde);

        var zaterdagen = new List<TeamScheduleZaterdag>();
        var zaterdag = vandaag;
        while (zaterdag.DayOfWeek != DayOfWeek.Saturday)
            zaterdag = zaterdag.AddDays(1);

        while (zaterdag <= seizoenEinde)
        {
            var zatStr = zaterdag.ToString("yyyy-MM-dd");
            var opDeDag = wedstrijden.Where(w => w.Datum == zatStr).ToList();

            string status;
            TeamScheduleWedstrijd? bezetDoor = null;
            var bezet = opDeDag.FirstOrDefault(w => w.Type == "competitie" || w.Type == "beker");
            if (bezet != null) { status = "bezet"; bezetDoor = bezet; }
            else
            {
                var oefen = opDeDag.FirstOrDefault(w => w.Type == "oefenwedstrijd");
                if (oefen != null) { status = "oefenwedstrijd"; bezetDoor = oefen; }
                else status = "vrij";
            }
            zaterdagen.Add(new TeamScheduleZaterdag { Datum = zatStr, Status = status, BezetDoor = bezetDoor });
            zaterdag = zaterdag.AddDays(7);
        }

        return new TeamScheduleResponse
        {
            Team = team,
            SeizoenEinde = seizoenEinde.ToString("yyyy-MM-dd"),
            Zaterdagen = zaterdagen,
            Wedstrijden = wedstrijden
        };
    }
}
