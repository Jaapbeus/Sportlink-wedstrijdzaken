namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Wat het formulier "Wedstrijd aanmaken" aanlevert, aangevuld met wat de server uit de eigen
/// database weet (leeftijdscategorie van het team, veldnaam, accommodatie). Sinds #1437 ook de
/// clubinstelling <paramref name="Spelactiviteit"/> (Sportlink-omschrijving of IdTag; gevuld én gevonden
/// wint altijd), het <paramref name="Velddeel"/> en een expliciet gekozen <paramref name="AgeClassCodeOverride"/>
/// (Sportlink-<c>Id</c>, wint van de leeftijdscategorie van het team).</summary>
public sealed record ClubMatchInvoer(
    DateTime MatchDateTime,
    int Duration,
    string TeamNaam,
    bool VrijeTekst,
    string? Leeftijdscategorie,
    string Tegenstander,
    string? VeldNaam,
    string Omschrijving,
    string? Accommodatie,
    string? Spelactiviteit = null,
    string Velddeel = ClubMatchVelddeel.Heel,
    string? AgeClassCodeOverride = null);

/// <summary>Uitkomst van <see cref="ClubMatchAanvraagBouwer.Bouw"/>: een aanvraag, of een fout die
/// het aanmaken tegenhoudt. Waarschuwingen zijn keuzes die de beheerder moet kunnen zien.</summary>
public sealed record ClubMatchBouwResultaat(
    SportlinkClubMatchAanvraag? Aanvraag,
    string? Fout,
    IReadOnlyList<string> Waarschuwingen);

/// <summary>
/// Vertaalt de formulierinvoer naar de <c>ClubMatch</c>-body met de vier lijsten uit
/// <see cref="SportlinkClubMatchContext"/> (#1427). Volgt wat Sportlinks eigen formulier doet: het
/// eigen team levert het <c>T…</c>-ID van het thuisteam, de tegenstander gaat als vrije tekst met de
/// "geen team"-sentinel (#1871 — vóór die fix ging het eigen ID ook als uit-ID mee), en wat niet
/// eenduidig af te leiden is valt terug op <c>ClubMatchDefaults</c> — met een waarschuwing.
/// <para>
/// <b>Wat bewust een fout is en geen terugval:</b> een gekozen team of veld dat Sportlink niet
/// (eenduidig) kent. Een oefenwedstrijd op het verkeerde team of veld is erger dan geen
/// oefenwedstrijd — zelfde regel als bij de teamresolutie: bij meerdere kandidaten wordt niets
/// gekozen.
/// </para>
/// </summary>
public static class ClubMatchAanvraagBouwer
{
    public static ClubMatchBouwResultaat Bouw(ClubMatchInvoer invoer, SportlinkClubMatchContext context)
    {
        var waarschuwingen = new List<string>();
        var d = context.Defaults;
        var teamNaam = invoer.TeamNaam.Trim();

        var team = ZoekTeam(context.Teams, teamNaam);
        if (team == null && !invoer.VrijeTekst)
            return Fout($"Team '{teamNaam}' is niet (eenduidig) gevonden in de Sportlink-teamlijst.");
        var (teamId, thuisNaam) = BepaalThuis(team, teamNaam, d.PublicHomeTeamId, waarschuwingen);
        var uitTeamId = BepaalUitTeamId(d.PublicAwayTeamId, teamId, context.Teams, waarschuwingen);

        var facility = ZoekFacility(context.Facilities, invoer.Accommodatie, d.FacilityId);
        if (facility == null)
            return Fout("Geen accommodatie gevonden in de Sportlink-locatielijst.");

        string? subFacilityId;
        if (string.IsNullOrWhiteSpace(invoer.VeldNaam))
        {
            subFacilityId = d.SubFacilityId;
            waarschuwingen.Add("Geen veld gekozen — Sportlinks standaardveld wordt gebruikt.");
        }
        else
        {
            subFacilityId = ZoekVeld(facility, invoer.VeldNaam);
            if (subFacilityId == null)
                return Fout($"Veld '{invoer.VeldNaam}' is niet gevonden bij accommodatie '{facility.NormalizedName}' in Sportlink.");
        }

        var aanvraag = new SportlinkClubMatchAanvraag(
            MatchDate: DateOnly.FromDateTime(invoer.MatchDateTime),
            StartTime: TimeOnly.FromDateTime(invoer.MatchDateTime),
            Duration: invoer.Duration,
            // #1437: het wedstrijdnummer komt niet meer uit ClubMatchDefaults maar uit onze eigen teller;
            // de aanroeper (ClubMatchEndpointCore) vult het in vlak vóór de Sportlink-aanroep.
            ExternalMatchId: null,
            Description: invoer.Omschrijving,
            HomeTeam: thuisNaam,
            AwayTeam: invoer.Tegenstander.Trim(),
            PublicTeamId: teamId,
            AgeClassCode: BepaalAgeClass(invoer, context.AgeClasses, d.AgeClassCode, waarschuwingen),
            SportIdTag: BepaalSportIdTag(team, invoer.Spelactiviteit, context.Activities, d.SportIdTag, waarschuwingen),
            IsHomeMatch: true,
            FacilityId: facility.FacilityId,
            SubFacilityId: subFacilityId,
            // AANNAME (#1437): zie ClubMatchVelddeel — alleen "1.0" is live bevestigd; FieldOffset 0 = eerste deel.
            FieldSize: invoer.Velddeel,
            FieldOffset: "0",
            PublicAwayTeamId: uitTeamId);
        return new ClubMatchBouwResultaat(aanvraag, null, waarschuwingen);

        ClubMatchBouwResultaat Fout(string fout) => new(null, fout, waarschuwingen);
    }

    /// <summary>
    /// Zoekt het eigen team op naam. Sportlinks <c>TeamName</c> staat zonder clubnaam ("35+4"),
    /// onze naam met ("AllStars 35+4"): eerst exact, anders een team waarvan de naam het laatste woord-deel
    /// van de onze is. Alleen een unieke treffer telt — meerdere → <c>null</c>.
    /// </summary>
    public static SportlinkClubTeam? ZoekTeam(IReadOnlyList<SportlinkClubTeam> teams, string naam)
    {
        var bruikbaar = teams.Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.TeamName)).ToList();
        var exact = bruikbaar.Where(t => string.Equals(t.TeamName!.Trim(), naam, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.Count == 1 ? exact[0] : null;

        var achtervoegsel = bruikbaar
            .Where(t => naam.EndsWith(" " + t.TeamName!.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (achtervoegsel.Count > 0) return achtervoegsel.Count == 1 ? achtervoegsel[0] : null;

        // #1737: onze database kent een team in bondsnotatie ("[club] O14-2") en lokale notatie ("JO14-2");
        // Sportlinks teamlijst gebruikt de lokale. Vergelijk daarom op de genormaliseerde sleutel,
        // met en zonder leidend clubwoord.
        var sleutels = new List<string> { TeamNaamNormalisatie.NormaliseerVoorVergelijking(naam) };
        var spatie = naam.IndexOf(' ');
        if (spatie > 0) sleutels.Add(TeamNaamNormalisatie.NormaliseerVoorVergelijking(naam[(spatie + 1)..]));
        var genormaliseerd = bruikbaar
            .Where(t => sleutels.Contains(TeamNaamNormalisatie.NormaliseerVoorVergelijking(t.TeamName), StringComparer.Ordinal))
            .ToList();
        return genormaliseerd.Count == 1 ? genormaliseerd[0] : null;
    }

    /// <summary>Accommodatie: op naam (instelling <c>accommodatie</c>), anders de door Sportlink als
    /// standaard gemarkeerde, anders die van <c>ClubMatchDefaults</c>, anders de enige.</summary>
    public static SportlinkClubFacility? ZoekFacility(
        IReadOnlyList<SportlinkClubFacility> facilities, string? accommodatie, string? standaardFacilityId)
    {
        var bruikbaar = facilities.Where(f => !string.IsNullOrWhiteSpace(f.FacilityId)).ToList();
        if (!string.IsNullOrWhiteSpace(accommodatie))
        {
            var opNaam = bruikbaar.Where(f => string.Equals(f.NormalizedName?.Trim(), accommodatie.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (opNaam.Count == 1) return opNaam[0];
        }
        return bruikbaar.SingleOrDefaultIfMany(f => f.IsDefault)
            ?? bruikbaar.SingleOrDefaultIfMany(f => f.FacilityId == standaardFacilityId)
            ?? (bruikbaar.Count == 1 ? bruikbaar[0] : null);
    }

    /// <summary>Veld op naam ("veld 1"), hoofdletter- en spatie-ongevoelig.</summary>
    public static string? ZoekVeld(SportlinkClubFacility facility, string veldNaam)
    {
        var gezocht = veldNaam.Trim();
        var treffers = (facility.Fields ?? Array.Empty<SportlinkClubField>())
            .Where(v => !string.IsNullOrWhiteSpace(v.SubFacilityId)
                     && (string.Equals(v.Name?.Trim(), gezocht, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(v.NormalizedName?.Trim(), gezocht, StringComparison.OrdinalIgnoreCase)))
            .Select(v => v.SubFacilityId)
            .Distinct()
            .ToList();
        return treffers.Count == 1 ? treffers[0] : null;
    }

    /// <summary>
    /// Onze leeftijdscategorie ("JO10", "MO15", "1-99", "VR") → de omschrijving in Sportlinks
    /// lijst ("Onder 10 (M)", "Onder 15 Meiden (V)", "Senioren (M)", "Senioren Vrouwen (V)").
    /// <c>null</c> als er geen herkenbare categorie is.
    /// </summary>
    public static string? AgeClassOmschrijving(string? leeftijdscategorie)
    {
        if (string.IsNullOrWhiteSpace(leeftijdscategorie)) return null;
        var ruw = leeftijdscategorie.Trim();
        if (ruw == "1-99") return "Senioren (M)";
        // Normaliseer kent een al genormaliseerde "MO15" niet als meidencategorie (hij herkent
        // meiden alleen aan het woord "Meiden"/"Meisjes") — daarom het MO-voorvoegsel eerst zelf.
        var sleutel = ruw.StartsWith("MO", StringComparison.OrdinalIgnoreCase)
            ? "MO" + new string(ruw.Where(char.IsDigit).ToArray())
            : LeeftijdNormalisatie.Normaliseer(ruw);
        if (sleutel == "VR") return "Senioren Vrouwen (V)";
        if (sleutel.Length > 2 && int.TryParse(sleutel[2..], out var jaar))
        {
            if (sleutel.StartsWith("JO", StringComparison.Ordinal)) return $"Onder {jaar} (M)";
            if (sleutel.StartsWith("MO", StringComparison.Ordinal)) return $"Onder {jaar} Meiden (V)";
        }
        return null;
    }

    /// <summary>Het Sportlink-<c>Id</c> van onze leeftijdscategorie ("JO10" → "110"), of <c>null</c> als die niet herkend
    /// of niet in Sportlinks lijst staat. Gedeeld met het formulier-endpoint (#1437), zodat de dropdown dezelfde
    /// voorinvulling krijgt als de server zou kiezen.</summary>
    public static string? ZoekAgeClassId(string? leeftijdscategorie, IReadOnlyList<SportlinkClubAgeClass> ageClasses)
    {
        var omschrijving = AgeClassOmschrijving(leeftijdscategorie);
        if (omschrijving == null) return null;
        return ageClasses.FirstOrDefault(a => string.Equals(a.Description?.Trim(), omschrijving, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string? BepaalAgeClass(
        ClubMatchInvoer invoer, IReadOnlyList<SportlinkClubAgeClass> ageClasses, string? standaard, List<string> waarschuwingen)
    {
        // #1437: een expliciet gekozen leeftijdscategorie wint van die van het team (validatie op bestaan
        // gebeurt bij de aanroeper, vóór deze methode).
        if (!string.IsNullOrWhiteSpace(invoer.AgeClassCodeOverride)) return invoer.AgeClassCodeOverride.Trim();

        var treffer = ZoekAgeClassId(invoer.Leeftijdscategorie, ageClasses);
        if (treffer != null) return treffer;

        var leeftijdscategorie = invoer.Leeftijdscategorie;
        var reden = string.IsNullOrWhiteSpace(leeftijdscategorie) ? "onbekend" : $"'{leeftijdscategorie}' niet in Sportlinks lijst";
        waarschuwingen.Add($"Leeftijdscategorie {reden} — Sportlinks standaard ({standaard ?? "geen"}) wordt gebruikt.");
        return standaard;
    }

    /// <summary>
    /// Spelactiviteit. De clubinstelling (omschrijving of IdTag, hoofdletterongevoelig) wint altijd als hij
    /// gevonden wordt (#1437); leeg → het eigen team, anders Sportlinks standaard; gevuld maar niet gevonden →
    /// hetzelfde als leeg, met een waarschuwing.
    /// </summary>
    private static string? BepaalSportIdTag(
        SportlinkClubTeam? team, string? instelling, IReadOnlyList<SportlinkClubActivity> activiteiten, string? standaard, List<string> waarschuwingen)
    {
        if (!string.IsNullOrWhiteSpace(instelling))
        {
            var gezocht = instelling.Trim();
            var treffers = activiteiten
                .Where(a => !string.IsNullOrWhiteSpace(a.IdTag)
                         && (string.Equals(a.IdTag!.Trim(), gezocht, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a.Description?.Trim(), gezocht, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.IdTag!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (treffers.Count == 1) return treffers[0];
            waarschuwingen.Add(treffers.Count == 0
                ? $"De clubinstelling Spelactiviteit '{gezocht}' staat niet in Sportlinks lijst — de spelactiviteit van het team of Sportlinks standaard wordt gebruikt."
                : $"De clubinstelling Spelactiviteit '{gezocht}' komt meerdere keren voor in Sportlinks lijst — de spelactiviteit van het team of Sportlinks standaard wordt gebruikt.");
        }

        if (team is { ExternalSportId: { Length: > 0 } sport, SportTag: { Length: > 0 } tag })
        {
            var idTag = $"{sport}/{tag}";
            if (activiteiten.Any(a => string.Equals(a.IdTag, idTag, StringComparison.OrdinalIgnoreCase)))
                return idTag;
        }
        if (team != null)
            waarschuwingen.Add($"Spelactiviteit van het team niet in Sportlinks lijst — standaard ({standaard ?? "geen"}) wordt gebruikt.");
        return standaard;
    }

    /// <summary>
    /// Thuisteam (#1871, zoals Sportlinks eigen formulier): een gevonden clubteam gaat met zijn eigen <c>Id</c> én
    /// <c>TeamName</c> (Sportlink toont toch de naam bij het ID); een vrije teamnaam met Sportlinks standaard-ID.
    /// </summary>
    private static (string Id, string Naam) BepaalThuis(
        SportlinkClubTeam? team, string teamNaam, string? standaardId, List<string> waarschuwingen)
    {
        if (team != null) return (team.Id!, team.TeamName!.Trim());
        waarschuwingen.Add($"Vrije teamnaam '{teamNaam}': niet gekoppeld aan een Sportlink-team (Sportlinks standaardteam); '{teamNaam}' staat als naam van het thuisteam.");
        return (GeenTeamOfStandaard(standaardId), teamNaam);
    }

    /// <summary>
    /// Uitteam-ID (#1871). De tegenstander is altijd vrije tekst en krijgt dus nooit een clubteam-ID — met het
    /// eigen ID toont Sportlink het eigen team als uitteam, en die koppeling is in Sportlink Club daarna niet meer
    /// naar vrije tekst te wijzigen. Sportlinks standaard (live: de sentinel) wint, tenzij dat een clubteam of het
    /// thuis-ID is.
    /// </summary>
    private static string BepaalUitTeamId(
        string? standaardId, string thuisId, IReadOnlyList<SportlinkClubTeam> clubTeams, List<string> waarschuwingen)
    {
        var uitId = GeenTeamOfStandaard(standaardId);
        if (uitId == SportlinkClubMatchAanvraag.GeenTeamId) return uitId;
        if (uitId != thuisId && !clubTeams.Any(t => string.Equals(t.Id, uitId, StringComparison.Ordinal))) return uitId;
        waarschuwingen.Add("Sportlinks standaard-uitteam is een eigen clubteam — de tegenstander gaat zonder teamkoppeling mee.");
        return SportlinkClubMatchAanvraag.GeenTeamId;
    }

    /// <summary>Sportlinks standaard-ID uit <c>ClubMatchDefaults</c>, of — leeg — de "geen team"-sentinel; zo doet
    /// Sportlinks eigen formulier het voor een vrije-tekst-team (#1871, <c>defaults.Public…TeamId || sentinel</c>).</summary>
    private static string GeenTeamOfStandaard(string? standaard)
        => string.IsNullOrWhiteSpace(standaard) ? SportlinkClubMatchAanvraag.GeenTeamId : standaard.Trim();

    /// <summary>Precies één treffer → die; nul of meerdere → <c>null</c>.</summary>
    private static T? SingleOrDefaultIfMany<T>(this IEnumerable<T> bron, Func<T, bool> predicaat) where T : class
    {
        var treffers = bron.Where(predicaat).Take(2).ToList();
        return treffers.Count == 1 ? treffers[0] : null;
    }
}
