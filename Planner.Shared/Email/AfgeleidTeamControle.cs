namespace Planner.Shared.Email;

/// <summary>
/// De controleregel in het antwoord als het eigen team uit de afzender is afgeleid (#1862): de afzender noemde zelf
/// geen team, dus het antwoord zegt van welk team het uitgaat en vraagt het te melden als dat niet klopt. Alleen
/// de teamnaam, nooit gegevens van de begeleiding.
/// </summary>
public static class AfgeleidTeamControle
{
    /// <summary>De regel (met voorafgaande lege regel), of leeg als het team niet is afgeleid of niet getoond mag worden.</summary>
    public static string Regel(BerichtClassificatie classificatie)
    {
        if (!classificatie.TeamAfgeleidUitAfzender) return "";
        var team = AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd);
        return string.IsNullOrWhiteSpace(team)
            ? ""
            : $"\n\nWe gaan uit van {team}; klopt dat niet, laat het ons weten.";
    }
}
