using System.Text;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Een nagebootste Sportlink-dataservice voor <c>/programma</c> die zich gedraagt als de echte (#1915): maximaal 100 rijen per
/// aanroep, oplopend in tijd, en de filters <c>thuis</c>/<c>uit</c> en <c>teamcode</c>; <c>aantaldagen</c> wordt aanvaard (de
/// testdag valt altijd in het venster). Gedeeld door beide tiers via <c>&lt;Compile Link&gt;</c> (zelfde aanpak als
/// <see cref="SportlinkFixtures"/>), zodat de nabootsing niet per tier kan uiteenlopen. Alle testdata is synthetisch: eigen
/// testclub, fictieve tegenstanders en teams.
/// </summary>
public static class PlafondDataservice
{
    /// <summary>Het aantal eigen teams van de testclub; elke wedstrijd hoort bij precies één van die teams.</summary>
    public const int AantalTeams = 40;

    /// <summary>
    /// De teamcodes liggen bewust ver van de codes in andere integratietests (bijv. 90001/90002 in de lease-tests, die een
    /// CHECK-constraint op een teamcode leggen): testdata van deze klasse mag nooit een andere test raken.
    /// </summary>
    public const long TeamBasis = 87000000;

    /// <summary>Eén wedstrijd van de testdag, van het eigen team <paramref name="Team"/> (teamcode).</summary>
    public sealed record Wed(long Code, bool Thuis, long Team);

    /// <summary>Wedstrijd <paramref name="i"/> (1-gebaseerd) van de testdag; de oneven nummers zijn thuiswedstrijden.</summary>
    public static Wed W(long basis, int i) => new(basis + i, Thuis: i % 2 == 1, Team: TeamBasis + 1 + i % AantalTeams);

    public static IReadOnlyList<Wed> Dag(long basis, int aantal) => Enumerable.Range(1, aantal).Select(i => W(basis, i)).ToList();

    /// <summary>Het <c>/teams</c>-antwoord: <see cref="AantalTeams"/> eigen teams met teamcodes vanaf <see cref="TeamBasis"/> + 1.</summary>
    public static string TeamsJson(string clubCode)
    {
        var teams = Enumerable.Range(1, AantalTeams).Select(k => $$"""
            {"teamcode":{{TeamBasis + k}},"lokaleteamcode":{{k}},"poulecode":7,"teamnaam":"{{clubCode}} JO13-{{k}}",
             "competitienaam":"0214 JO13 Zaterdag","klasse":"3e klasse","poule":"03 (C)","klassepoule":"3e klasse 03 (C)",
             "spelsoort":"veld","competitiesoort":"regulier","geslacht":"M","teamsoort":"veld","leeftijdscategorie":"Onder 13",
             "kalespelsoort":"veld","speeldag":"zaterdag","speeldagteam":"{{clubCode}} JO13-{{k}}","more":""}
            """);
        return "[" + string.Join(',', teams) + "]";
    }

    /// <summary>Het <c>/programma</c>-antwoord voor een lijst wedstrijden, in het formaat van de echte dataservice.</summary>
    public static string Json(IEnumerable<Wed> wedstrijden, long basis, string clubCode, string datum)
    {
        var sb = new StringBuilder("[");
        var eerste = true;
        foreach (var w in wedstrijden)
        {
            if (!eerste) sb.Append(',');
            eerste = false;
            var nummer = (int)(w.Code - basis);
            var tijd = $"{8 + nummer / 12:00}:{nummer % 12 * 5:00}";
            var eigen = $"{clubCode} JO13-{w.Team - TeamBasis}";
            var thuisteam = w.Thuis ? eigen : $"Tegenstander {nummer}";
            var uitteam = w.Thuis ? $"Tegenstander {nummer}" : eigen;
            sb.Append($$"""
                {"wedstrijddatum":"{{datum}}T{{tijd}}:00+0200","wedstrijdcode":{{w.Code}},"wedstrijdnummer":{{500000 + nummer}},
                 "teamnaam":"{{eigen}}","thuisteam":"{{thuisteam}}","uitteam":"{{uitteam}}","teamvolgorde":1,
                 "competitiesoort":"regulier","kaledatum":"{{datum}} 00:00:00.00","aanvangstijd":"{{tijd}}",
                 "wedstrijd":"{{thuisteam}} - {{uitteam}}","status":"Te spelen","accommodatie":"Sportpark Oost","veld":"veld 3"}
                """);
        }
        return sb.Append(']').ToString();
    }

    /// <summary>
    /// De dataservice: zonder filter de eerste 100 van alles; met <c>thuis=JA&amp;uit=NEE</c>, <c>thuis=NEE&amp;uit=JA</c> en/of
    /// <c>teamcode=…</c> de gevraagde verzameling (ook weer maximaal 100). Met <paramref name="negeertFilters"/> kent hij geen
    /// enkel filter: elke aanroep geeft dezelfde eerste 100.
    /// </summary>
    public static SportlinkFixtureServer Bouw(
        long basis, string clubCode, string datum, IReadOnlyList<Wed> dag, bool negeertFilters = false)
    {
        var server = SportlinkFixtures.BuildServer(basis, clubCode);
        server.RespondWithJson("/teams", TeamsJson(clubCode));
        server.RespondWithJson("/uitslagen", "[]");
        server.RespondWithJson("/programma", request =>
        {
            var query = request.Url!.Query;
            IEnumerable<Wed> selectie = dag;
            if (!negeertFilters)
            {
                if (query.Contains("thuis=JA&uit=NEE")) selectie = selectie.Where(w => w.Thuis);
                else if (query.Contains("thuis=NEE&uit=JA")) selectie = selectie.Where(w => !w.Thuis);
                var team = request.QueryString["teamcode"];
                if (team != null) selectie = selectie.Where(w => w.Team == long.Parse(team));
            }
            return Json(selectie.Take(100), basis, clubCode, datum);
        });
        return server;
    }
}
