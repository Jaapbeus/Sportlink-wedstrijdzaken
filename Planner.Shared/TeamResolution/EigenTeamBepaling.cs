using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
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
public enum EigenTeamBesluit { Onbekend, Herkend, Handmatig }

public sealed record EigenTeamUitkomst(bool Herkend, string? TeamNaam, string? Tegenstander)
{
    public EigenTeamBesluit Besluit { get; init; } = Herkend ? EigenTeamBesluit.Herkend : EigenTeamBesluit.Onbekend;
    public static readonly EigenTeamUitkomst Handmatig = new(false, null, null) { Besluit = EigenTeamBesluit.Handmatig };
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
    /// Resolves both fields before choosing an own team. Distinct resolved identities require
    /// authoritative source ownership from fixture data; inconclusive evidence requires manual review.
    /// Unresolved multiple candidates are never automatically disambiguated.
    /// </summary>
    public static async Task<EigenTeamUitkomst> BepaalEigenTeamEnTegenstanderAsync(
        string? teamNaam, string? tegenstander, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace,
        ITeamFixtureContextRepository? fixtures = null, string? datum = null, string? tijd = null, IReadOnlyList<string>? datums = null)
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
        var tegenstanderUitkomst = await ProbeerResolveAsync(resolver, tegenstanderTekst, clubCode, log, trace, TraceCodes.TegenstanderHerkenning);
        // Null for nonempty input means a repository failure; normal unknown is a non-null Onopgelost.
        if (ResolverFailed(team, teamUitkomst) || ResolverFailed(tegenstanderTekst, tegenstanderUitkomst))
            return EigenTeamUitkomst.Handmatig;
        if (teamUitkomst?.Bron == ResolutionBron.MeerdereKandidaten || tegenstanderUitkomst?.Bron == ResolutionBron.MeerdereKandidaten)
            return EigenTeamUitkomst.Handmatig;

        if (teamUitkomst?.IsOpgelost == true && tegenstanderUitkomst?.IsOpgelost == true
            && teamUitkomst.TeamId != tegenstanderUitkomst.TeamId)
            return await KiesMetWedstrijdAsync(teamUitkomst, tegenstanderUitkomst, team, tegenstander,
                fixtures, clubCode, datum, tijd, datums, log);
        if (teamUitkomst?.IsOpgelost == true)
        {
            LogUitkomst(log, teamUitkomst);
            return new EigenTeamUitkomst(true, teamUitkomst.CanoniekeTeamnaam, tegenstander);
        }
        if (tegenstanderUitkomst?.IsOpgelost == true)
        {
            trace?.TeamWissel(tegenstanderUitkomst.CanoniekeTeamnaam, team);
            LogUitkomst(log, tegenstanderUitkomst);
            return new EigenTeamUitkomst(true, tegenstanderUitkomst.CanoniekeTeamnaam, team);
        }

        var kandidaten = teamUitkomst?.Kandidaten.Count ?? 0;
        log.LogInformation(
            "TEAMRESOLUTIE - geen eigen team herkend (bron={Bron}, kandidaten={Kandidaten})",
            teamUitkomst?.Bron ?? ResolutionBron.Onopgelost, kandidaten);
        return EigenTeamUitkomst.NietHerkend;
    }

    private static bool ResolverFailed(string text, TeamResolutionResult? result) => text.Length > 0 && result is null;

    /// <summary>Prepares the classification once, before either tier can dispatch planning or contact work.</summary>
    public static async Task<bool> BereidPlannerVoorAsync(
        BerichtClassificatie classificatie, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace,
        ITeamFixtureContextRepository? fixtureContext = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        (classificatie.Datum, classificatie.GewensteDatum, classificatie.Datums) = ModelDatum.Grens(classificatie.Datum, classificatie.GewensteDatum, classificatie.Datums);
        trace?.Classificatie(classificatie.Type.ToString(), classificatie.TeamNaam, classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd);
        var herkend = await BepaalEnPasToeAsync(classificatie, resolver, clubCode, log, trace, fixtureContext);
        if (classificatie.TeamHandmatigBeoordelen) return false;
        classificatie.TeamNaamOnbevestigd = !herkend;
        if (herkend) classificatie.LeeftijdsCategorie = TeamNaamNormalisatie.VulLeeftijdsCategorieAan(classificatie.LeeftijdsCategorie, classificatie.TeamNaam, clubCode);
        return herkend;
    }

    public static async Task<bool> BepaalEnPasToeAsync(
        BerichtClassificatie classificatie, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace,
        ITeamFixtureContextRepository? fixtureContext = null)
    {
        var result = await BepaalEigenTeamEnTegenstanderAsync(classificatie.TeamNaam, classificatie.Tegenstander,
            resolver, clubCode, log, trace, fixtureContext, classificatie.Datum, classificatie.AanvangsTijd, classificatie.Datums);
        classificatie.TeamHandmatigBeoordelen = result.Besluit == EigenTeamBesluit.Handmatig;
        if (!result.Herkend) return false;
        classificatie.TeamNaam = result.TeamNaam;
        classificatie.Tegenstander = result.Tegenstander;
        return true;
    }

    private static async Task<EigenTeamUitkomst> KiesMetWedstrijdAsync(
        TeamResolutionResult team, TeamResolutionResult opponent, string rawTeam, string? rawOpponent,
        ITeamFixtureContextRepository? fixtures, string club, string? datum, string? tijd,
        IReadOnlyList<string>? datums, ILogger log)
    {
        if (fixtures is null || !TeamFixtureChoice.TryDate(datum, datums, tijd, out var date))
            return EigenTeamUitkomst.Handmatig;
        try
        {
            var context = await fixtures.GetAsync(club, date, [team.TeamId!.Value, opponent.TeamId!.Value]);
            var selected = TeamFixtureChoice.Choose(context, team.TeamId.Value, opponent.TeamId.Value, tijd);
            if (selected is null) return EigenTeamUitkomst.Handmatig;
            return selected == opponent.TeamId
                ? new EigenTeamUitkomst(true, opponent.CanoniekeTeamnaam, rawTeam)
                : new EigenTeamUitkomst(true, team.CanoniekeTeamnaam, rawOpponent);
        }
        catch (Exception ex)
        {
            log.LogWarning("TEAMRESOLUTIE - wedstrijdcontext niet beschikbaar ({FoutType}); handmatige beoordeling nodig", ex.GetType().Name);
            return EigenTeamUitkomst.Handmatig;
        }
    }

    /// <summary>
    /// Haalt uit een wedstrijdstring ("Thuisteam - Uitteam", uit de opponent-lookup #1139) de kant die ons
    /// team is, door beide kanten tegen de teamlijst te resolven. Alleen één verschillende identiteit
    /// levert een naam; twee eigen teams of meerdere kandidaten geven <c>null</c>.
    /// </summary>
    public static async Task<string?> BepaalEigenTeamUitWedstrijdAsync(
        string? wedstrijd, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(wedstrijd)) return null;

        var gevonden = new Dictionary<int, string>();
        foreach (var kant in wedstrijd.Split(" - ", 2, StringSplitOptions.TrimEntries))
        {
            var uitkomst = await ProbeerResolveAsync(resolver, kant, clubCode, log, trace, TraceCodes.OpponentTeamHerkenning);
            if (uitkomst is null || uitkomst.Bron == ResolutionBron.MeerdereKandidaten) return null;
            if (uitkomst?.IsOpgelost == true)
                gevonden.TryAdd(uitkomst.TeamId!.Value, uitkomst.CanoniekeTeamnaam!);
        }
        return gevonden.Count == 1 ? gevonden.Values.Single() : null;
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
