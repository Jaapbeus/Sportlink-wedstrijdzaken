using FunctionApp.Postgres.Planner.Repositories;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Services/TeamScheduleService.cs</c> (#888).
/// <para>
/// De logica hier is provider-agnostisch — het aflopen van de zaterdagen tot het seizoenseinde en
/// het bepalen van de status per zaterdag is pure C# en woordelijk gelijk aan het origineel. Alleen
/// de drie data-aanroepen wijzen naar de Postgres-repositories.
/// </para>
/// </summary>
internal static class TeamScheduleService
{
    internal static async Task<TeamScheduleResponse?> GetTeamScheduleAsync(
        string connectionString, string team, string clubCode)
    {
        if (!await PlannerMatchRepository.TeamExistsAsync(connectionString, team, clubCode))
            return null;

        // Dezelfde terugval als het origineel wanneer public.season leeg is: drie maanden vooruit.
        // Bewust hier en niet in de repository — zo blijft "geen seizoen bekend" zichtbaar als een
        // afwezige waarde in de datalaag in plaats van als een verzonnen datum.
        var seizoenEinde = await PlannerSettingsRepository.GetSeasonEndDateAsync(connectionString)
            ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(3));
        var vandaag     = DateOnly.FromDateTime(DateTime.Today);
        var wedstrijden = await PlannerMatchRepository.GetFutureMatchesForTeamAsync(
            connectionString, team, vandaag, seizoenEinde, clubCode);

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
