using Microsoft.Extensions.Logging;
using Planner.Shared.Email.Trace;

namespace Planner.Shared.TeamResolution;

/// <summary>
/// Uitkomst van <see cref="EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync"/>. Alleen bij
/// <see cref="Herkend"/> zet de tier <see cref="TeamNaam"/> en <see cref="Tegenstander"/> op zijn eigen
/// classificatie; anders blijft die ongewijzigd.
/// </summary>
/// <param name="Herkend">True als één van beide genoemde teams in de eigen teamlijst staat.</param>
/// <param name="TeamNaam">De canonieke naam van het eigen team (alleen bij <see cref="Herkend"/>).</param>
/// <param name="Tegenstander">De tegenstander na een eventuele wissel (alleen bij <see cref="Herkend"/>).</param>
public sealed record EigenTeamUitkomst(bool Herkend, string? TeamNaam, string? Tegenstander)
{
    public static readonly EigenTeamUitkomst NietHerkend = new(false, null, null);
}

/// <summary>
/// Eigen-team- en tegenstanderbepaling voor een inkomend bericht (#700/#889), sinds #1716 (slice 4)
/// tier-onafhankelijk: beide <c>BerichtPipeline</c>-tiers roepen deze ene klasse aan. De database zit
/// achter <see cref="ITeamResolver"/> (poort <see cref="ITeamCandidateRepository"/> per tier); de
/// teamnaamregel zelf staat alleen in <see cref="TeamNaamNormalisatie"/>, via de resolver.
///
/// <para>
/// <b>Waarom dit geen stringheuristiek is.</b> Voorheen werd "eigen team" geraden uit de vorm van de
/// tekst: geen spatie, of beginnend met de clubcode. Een tegenstander die zijn team zonder spatie schreef
/// ("AjaxJO13-2") werd daardoor als eigen team gezien, met thuis en uit omgedraaid, zonder foutmelding.
/// Nu is de vraag: wélke van de twee staat in onze teamlijst? Lost geen van beide op (ook niet bij
/// <see cref="ResolutionBron.MeerdereKandidaten"/>), dan wordt er nooit een naam gegokt.
/// </para>
/// </summary>
public static class EigenTeamBepaling
{
    /// <summary>
    /// Bepaalt welke van de twee genoemde teams het eigen team is. Eerst het genoemde team; lost dat niet
    /// op, dan het tegenstanderveld (een tegenstander die namens zichzelf schrijft): dan worden team en
    /// tegenstander verwisseld. Zonder clubcode wordt de herkenning overgeslagen.
    /// </summary>
    public static async Task<EigenTeamUitkomst> BepaalEigenTeamEnTegenstanderAsync(
        string? teamNaam, string? tegenstander, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
        {
            trace?.TeamHerkenningOvergeslagen("geen clubcode");
            log.LogWarning("TEAMRESOLUTIE - geen clubCode beschikbaar; teamherkenning overgeslagen");
            return EigenTeamUitkomst.NietHerkend;
        }

        var team = (teamNaam ?? "").Trim();
        var tegenstanderTekst = (tegenstander ?? "").Trim();

        var teamUitkomst = await ProbeerResolveAsync(resolver, team, clubCode, log, trace, TraceCodes.TeamHerkenning);
        if (teamUitkomst is not null && teamUitkomst.IsOpgelost)
        {
            LogUitkomst(log, teamUitkomst);
            return new EigenTeamUitkomst(true, teamUitkomst.CanoniekeTeamnaam, tegenstander);
        }

        // Het genoemde "team" is niet van ons. Misschien staat ons team in het tegenstanderveld.
        var tegenstanderUitkomst = await ProbeerResolveAsync(resolver, tegenstanderTekst, clubCode, log, trace, TraceCodes.TegenstanderHerkenning);
        if (tegenstanderUitkomst is not null && tegenstanderUitkomst.IsOpgelost)
        {
            trace?.TeamWissel(tegenstanderUitkomst.CanoniekeTeamnaam, team);
            log.LogInformation("TEAMRESOLUTIE - team en tegenstander verwisseld op basis van de teamlijst");
            LogUitkomst(log, tegenstanderUitkomst);
            return new EigenTeamUitkomst(true, tegenstanderUitkomst.CanoniekeTeamnaam, team);
        }

        var kandidaten = teamUitkomst?.Kandidaten.Count ?? 0;
        log.LogInformation(
            "TEAMRESOLUTIE - geen eigen team herkend (bron={Bron}, kandidaten={Kandidaten})",
            teamUitkomst?.Bron ?? ResolutionBron.Onopgelost, kandidaten);
        return EigenTeamUitkomst.NietHerkend;
    }

    /// <summary>
    /// Haalt uit een wedstrijdstring ("Thuisteam - Uitteam", uit de opponent-lookup #1139) de kant die ons
    /// team is, door beide kanten tegen de teamlijst te resolven. De eerste oplosbare kant levert de
    /// canonieke naam; geen van beide oplosbaar geeft <c>null</c>. Er wordt nooit een naam gegokt.
    /// </summary>
    public static async Task<string?> BepaalEigenTeamUitWedstrijdAsync(
        string? wedstrijd, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(wedstrijd)) return null;

        foreach (var kant in wedstrijd.Split(" - ", 2, StringSplitOptions.TrimEntries))
        {
            var uitkomst = await ProbeerResolveAsync(resolver, kant, clubCode, log, trace, TraceCodes.OpponentTeamHerkenning);
            if (uitkomst is not null && uitkomst.IsOpgelost)
                return uitkomst.CanoniekeTeamnaam;
        }
        return null;
    }

    /// <summary>
    /// Resolveert één teamaanduiding. Retourneert <c>null</c> bij lege invoer of een storing: de aanroeper
    /// behandelt dat als "niet herkend", zodat een fout in de vertaallaag nooit tot een verkeerd gekoppeld
    /// team leidt.
    /// </summary>
    internal static async Task<TeamResolutionResult?> ProbeerResolveAsync(
        ITeamResolver resolver, string ruweTekst, string clubCode, ILogger log, TraceBuilder? trace, string code)
    {
        if (string.IsNullOrWhiteSpace(ruweTekst)) return null;

        try
        {
            var u = await resolver.ResolveAsync(new TeamResolutionRequest(ruweTekst, null, null, clubCode));
            trace?.TeamHerkenning(code, ruweTekst, u.Bron.ToString(), u.Confidence, u.Kandidaten.Select(k => k.Teamnaam), u.IsOpgelost ? u.CanoniekeTeamnaam : null);
            return u;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMRESOLUTIE - resolutie mislukt");
            trace?.TeamHerkenning(code, ruweTekst, null, 0, null, null);
            return null;
        }
    }

    private static void LogUitkomst(ILogger log, TeamResolutionResult uitkomst)
        => log.LogInformation(
            "TEAMRESOLUTIE - teamId={TeamId} bron={Bron} confidence={Confidence}",
            uitkomst.TeamId, uitkomst.Bron, uitkomst.Confidence);
}
