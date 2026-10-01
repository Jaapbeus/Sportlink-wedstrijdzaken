namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Wat het formulier "Wedstrijd aanmaken" aanlevert, aangevuld met wat de server uit de eigen
/// database weet (leeftijdscategorie van het team, veldnaam, accommodatie).</summary>
public sealed record ClubMatchInvoer(
    DateTime MatchDateTime,
    int Duration,
    string TeamNaam,
    bool VrijeTekst,
    string? Leeftijdscategorie,
    string Tegenstander,
    string? VeldNaam,
    string Omschrijving,
    string? Accommodatie);

/// <summary>Uitkomst van <see cref="ClubMatchAanvraagBouwer.Bouw"/>: een aanvraag, of een fout die
/// het aanmaken tegenhoudt. Waarschuwingen zijn keuzes die de beheerder moet kunnen zien.</summary>
public sealed record ClubMatchBouwResultaat(
    SportlinkClubMatchAanvraag? Aanvraag,
    string? Fout,
    IReadOnlyList<string> Waarschuwingen);

/// <summary>
/// Vertaalt de formulierinvoer naar de <c>ClubMatch</c>-body met de vier lijsten uit
/// <see cref="SportlinkClubMatchContext"/> (#1427). Volgt wat Sportlinks eigen formulier doet: het
/// eigen team levert het <c>T…</c>-ID (thuis én uit), de teamnamen zijn vrije tekst, en wat niet
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
        string? teamId = team?.Id;
        if (team == null)
        {
            if (!invoer.VrijeTekst)
                return Fout($"Team '{teamNaam}' is niet (eenduidig) gevonden in de Sportlink-teamlijst.");
            teamId = d.PublicHomeTeamId;
            waarschuwingen.Add($"Vrije teamnaam '{teamNaam}': Sportlinks standaardteam wordt als team gebruikt; '{teamNaam}' staat als naam van het thuisteam.");
        }
        if (string.IsNullOrWhiteSpace(teamId))
            return Fout("Sportlink gaf geen standaardteam terug — kies een team uit de lijst.");

        if (d.ExternalMatchId == null)
            return Fout("Sportlink gaf geen wedstrijdnummer terug (ClubMatchDefaults).");

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
            ExternalMatchId: d.ExternalMatchId,
            Description: invoer.Omschrijving,
            HomeTeam: teamNaam,
            AwayTeam: invoer.Tegenstander.Trim(),
            PublicTeamId: teamId,
            AgeClassCode: BepaalAgeClass(invoer.Leeftijdscategorie, context.AgeClasses, d.AgeClassCode, waarschuwingen),
            SportIdTag: BepaalSportIdTag(team, context.Activities, d.SportIdTag, waarschuwingen),
            IsHomeMatch: true,
            FacilityId: facility.FacilityId,
            SubFacilityId: subFacilityId);
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
        return achtervoegsel.Count == 1 ? achtervoegsel[0] : null;
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

    private static string? BepaalAgeClass(
        string? leeftijdscategorie, IReadOnlyList<SportlinkClubAgeClass> ageClasses, string? standaard, List<string> waarschuwingen)
    {
        var omschrijving = AgeClassOmschrijving(leeftijdscategorie);
        var treffer = omschrijving == null ? null
            : ageClasses.FirstOrDefault(a => string.Equals(a.Description?.Trim(), omschrijving, StringComparison.OrdinalIgnoreCase));
        if (treffer?.Id != null) return treffer.Id;

        var reden = string.IsNullOrWhiteSpace(leeftijdscategorie) ? "onbekend" : $"'{leeftijdscategorie}' niet in Sportlinks lijst";
        waarschuwingen.Add($"Leeftijdscategorie {reden} — Sportlinks standaard ({standaard ?? "geen"}) wordt gebruikt.");
        return standaard;
    }

    private static string? BepaalSportIdTag(
        SportlinkClubTeam? team, IReadOnlyList<SportlinkClubActivity> activiteiten, string? standaard, List<string> waarschuwingen)
    {
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

    /// <summary>Precies één treffer → die; nul of meerdere → <c>null</c>.</summary>
    private static T? SingleOrDefaultIfMany<T>(this IEnumerable<T> bron, Func<T, bool> predicaat) where T : class
    {
        var treffers = bron.Where(predicaat).Take(2).ToList();
        return treffers.Count == 1 ? treffers[0] : null;
    }
}
