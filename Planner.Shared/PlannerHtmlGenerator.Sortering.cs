namespace Planner.Shared;

// Sorteerlogica van de categorietabel (#1625): uit PlannerHtmlGenerator.cs gehaald; pure functie.
public static partial class PlannerHtmlGenerator
{
        /// <summary>
        /// Sorteerwaarde van een wedstrijd voor de categorietabel: leeftijd x 10 (+1 voor meisjes),
        /// daarna MO20, G-teams, O23, VR, senioren (<paramref name="clubCode"/> + cijfer) en overig.
        /// </summary>
        private static int LeeftijdUitNaam(string naam, string? clubCode)
        {
            // Check van hoog naar laag om JO19 niet als JO1 te matchen
            for (int i = 23; i >= 7; i--)
            {
                if (naam.Contains($"JO{i}") || naam.Contains($"MO{i}"))
                    return i * 10 + (naam.Contains($"MO{i}") ? 1 : 0);
            }
            if (naam.Contains("MO20")) return 201;
            // G-teams: na MO20, voor O23
            if (System.Text.RegularExpressions.Regex.IsMatch(naam, @"\bG\d"))
                return 210;
            if (naam.Contains("O23")) return 230;
            if (naam.Contains("VR")) return 300;
            // Senioren (eerste team en reserves): helemaal onderaan
            if (!string.IsNullOrWhiteSpace(clubCode)
                && System.Text.RegularExpressions.Regex.IsMatch(naam,
                    $@"{System.Text.RegularExpressions.Regex.Escape(clubCode)} \d"))
                return 400;
            return 500;
        }
}
