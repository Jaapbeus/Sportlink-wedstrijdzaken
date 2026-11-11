using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace Planner.Shared.TeamResolution;

/// <summary>
/// Eigen team afleiden uit de afzender (#1862): staat de afzender in de teambegeleiding bij precies één team, dan
/// is dat het team waarover hij mailt als de mail zelf geen herkenbaar team noemt. Eenmaal voor beide
/// databasetiers; de tier levert alleen de opzoeking (<see cref="IAfzenderTeamBron"/>).
/// <list type="number">
///   <item><description>Een genoemd team in de mail wint altijd. Begeleidt de afzender een ander team, dan komt er
///   alleen een tracenotitie zonder namen (eigenaarsbesluit 6A).</description></item>
///   <item><description>Precies één begeleid team, dat via de <see cref="ITeamResolver"/> oplost: dat team, met een
///   controleregel in het antwoord.</description></item>
///   <item><description>Meerdere teams: niets kiezen en de teams ook niet noemen (5A); de wedervraag volgt.</description></item>
/// </list>
/// Privacy: het e-mailadres gaat nooit naar trace of log, ook niet gehasht. De trace noemt alleen het aantal
/// gevonden teams (0, 1 of meer, 4A); de opzoeking schrijft geen inzageregel (4A). Een Reply-To dat afwijkt van
/// From slaat de opzoeking over, zodat een vervalste afzender via een eigen antwoordadres nooit te weten komt bij
/// welk team een adres hoort.
/// </summary>
public static class AfzenderTeamBepaling
{
    public const string AantalSleutel = "aantalTeams";
    public const string AfwijkendTeamSleutel = "afzenderBegeleidtAnderTeam";

    /// <summary>
    /// Past de afzender-opzoeking toe na de gewone teambepaling. Geeft <c>true</c> als er daarna een eigen team
    /// staat (genoemd of afgeleid); bij een afgeleid team zijn <see cref="BerichtClassificatie.TeamNaam"/> en
    /// <see cref="BerichtClassificatie.TeamAfgeleidUitAfzender"/> gezet.
    /// </summary>
    public static async Task<bool> VerwerkAsync(
        BerichtClassificatie classificatie, bool herkend, AfzenderOpzoeking? opzoeking, ITeamResolver resolver,
        string clubCode, ILogger log, TraceBuilder? trace)
    {
        var adres = AfzenderTeamBronHulp.NormaliseerAdres(opzoeking?.Afzender);
        if (opzoeking is null || adres.Length == 0 || string.IsNullOrWhiteSpace(clubCode))
            return herkend;
        if (!TraceBuilder.IsTeamVereist(classificatie.Type.ToString())) return herkend;
        if (opzoeking.Bron is OvergeslagenAfzenderTeamBron overgeslagen)
        {
            trace?.BegeleidingAfzenderOvergeslagen(overgeslagen.Reden);
            return herkend;
        }
        if (AntwoordadresWijktAf(opzoeking))
        {
            trace?.BegeleidingAfzenderOvergeslagen("antwoordadres wijkt af van afzender");
            return herkend;
        }

        IReadOnlyList<string> teams;
        try
        {
            teams = Uniek(await opzoeking.Bron.ZoekTeamsVanAfzenderAsync(adres, clubCode));
        }
        catch (Exception ex)
        {
            // Geen foutdetails: die kunnen het adres bevatten. Een storing is "niet gevonden", nooit een gok.
            log.LogWarning("TEAMRESOLUTIE - opzoeking in de begeleiding mislukt ({FoutType})", ex.GetType().Name);
            trace?.BegeleidingAfzender(AantalOmschrijving(0), false, ZekerheidsNiveau.Onzeker, false);
            return herkend;
        }

        if (teams.Count != 1)
        {
            // Meerdere teams: expliciet vastleggen (5A), zodat de vervolgroute nooit uit de opponent een team afleidt.
            classificatie.MeerdereAfzenderTeams = teams.Count > 1 && !herkend;
            trace?.BegeleidingAfzender(AantalOmschrijving(teams.Count), false,
                teams.Count > 1 ? ZekerheidsNiveau.Onzeker : ZekerheidsNiveau.Mislukt, false);
            return herkend;
        }

        var uitkomst = await EigenTeamBepaling.ProbeerResolveAsync(resolver, teams[0], clubCode, log, null, TraceCodes.BegeleidingAfzender);
        var canoniek = uitkomst?.IsOpgelost == true ? uitkomst.CanoniekeTeamnaam : null;
        if (herkend)
        {
            var wijktAf = canoniek is not null && !string.Equals(canoniek, classificatie.TeamNaam, StringComparison.OrdinalIgnoreCase);
            trace?.BegeleidingAfzender(AantalOmschrijving(1), false, ZekerheidsNiveau.Zeker, wijktAf);
            return true;
        }

        if (canoniek is null)
        {
            trace?.BegeleidingAfzender(AantalOmschrijving(1), false, ZekerheidsNiveau.Onzeker, false);
            return false;
        }

        classificatie.TeamNaam = canoniek;
        classificatie.TeamAfgeleidUitAfzender = true;
        trace?.BegeleidingAfzender(AantalOmschrijving(1), true, ZekerheidsNiveau.Zeker, false);
        log.LogInformation("TEAMRESOLUTIE - eigen team afgeleid uit de teambegeleiding van de afzender");
        return true;
    }

    /// <summary>
    /// De adressen uit de <c>Reply-To</c>-header(s) van een bericht (de internetheaders die de Graph-adapter al meegeeft).
    /// Is een header niet te lezen, dan telt de ruwe waarde als afwijkend adres: bij twijfel geen opzoeking.
    /// </summary>
    public static IReadOnlyList<string> ReplyToUitHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var adressen = new List<string>();
        foreach (var (naam, waarde) in headers ?? [])
        {
            if (!string.Equals(naam?.Trim(), "Reply-To", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(waarde)) continue;
            try
            {
                var lijst = new System.Net.Mail.MailAddressCollection();
                lijst.Add(waarde);
                adressen.AddRange(lijst.Select(a => a.Address));
            }
            catch (FormatException)
            {
                adressen.Add(waarde.Trim());
            }
        }
        return adressen;
    }

    private static string AantalOmschrijving(int aantal) => aantal switch { 0 => "0", 1 => "1", _ => "meer" };

    private static bool AntwoordadresWijktAf(AfzenderOpzoeking opzoeking)
    {
        var van = opzoeking.Afzender!.Trim();
        return opzoeking.ReplyTo?.Any(a => !string.IsNullOrWhiteSpace(a)
            && !string.Equals(a.Trim(), van, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static IReadOnlyList<string> Uniek(IReadOnlyList<string>? teams)
        => (teams ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
