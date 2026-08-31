namespace Planner.Shared;

/// <summary>
/// Het doel waarop één wedstrijd ingepland moet worden, samengesteld uit de drie lagen in de
/// vastgelegde rangorde: regels → ingevoerde voorkeuren → defaults per leeftijdscategorie (#666).
/// </summary>
/// <param name="Laag">0 = voorkeursveld-regel, 1 = eigen voorkeurstijd, 2 = leeftijdsdefault, 3 = niets.</param>
/// <param name="Prioriteit">Laag getal = belangrijker; beslist wie zijn doel als eerste claimt.</param>
public record PlanDoel(
    int Laag,
    int Prioriteit,
    TimeOnly? DoelTijd,
    int? VoorkeurVeldNummer,
    string? Bron,
    string Leeftijd);

/// <summary>
/// De pure planningsregels achter AutoPlan: welk doel krijgt een wedstrijd, in welke volgorde worden
/// wedstrijden verwerkt, en hoe wordt de afwijking t.o.v. de voorkeurstijd beoordeeld.
///
/// <para>
/// <b>Waarom gedeeld en niet per tier gedupliceerd (issue 888 vervolg, §42).</b> Deze methoden raken
/// geen database, geen instellingencache en geen tier-specifiek type — het is rekenlogica over
/// primitieven en gedeelde domeinmodellen. De architectuurregel in
/// docs/ARCHITECTUUR-DATABASE-TIERS.md is daar expliciet over: logica zonder tier-afhankelijkheid
/// hoort op precies één plek. Zelfde precedent als <see cref="FieldScheduler"/> (§38) en
/// <see cref="TeamNaamNormalisatie"/> (#889).
/// </para>
///
/// <para>
/// <b><see cref="BepaalPlanDoel"/> neemt primitieven, geen wedstrijdmodel.</b> De twee tiers hebben
/// elk hun eigen <c>WedstrijdRaw</c>-vorm (een class met setters op de SQL Server-tier, een
/// positional record op de Postgres-tier). Die shape delen zou een DTO-verhuizing afdwingen die
/// niets oplost; twee strings binnengeven is genoeg en koppelt niets.
/// </para>
/// </summary>
public static class AutoPlanRegels
{
    /// <summary>
    /// Stelt het planningsdoel voor één wedstrijd samen uit de drie lagen (#666).
    /// </summary>
    /// <param name="teamNaam">Teamnaam zoals die in de bron staat.</param>
    /// <param name="leeftijdsCategorie">Leeftijdscategorie uit de bron; leeg/null is toegestaan.</param>
    /// <param name="isAllstars">In demomodus wordt de leeftijd uit de teamnaam afgeleid als de bron er geen levert.</param>
    public static PlanDoel BepaalPlanDoel(
        string? teamNaam,
        string? leeftijdsCategorie,
        bool isAllstars,
        Dictionary<string, TeamVoorkeurVeld> voorkeurVelden,
        Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>> voorkeurLookup,
        Dictionary<string, Speeltijd> speeltijden)
    {
        var leeftijd = (!string.IsNullOrWhiteSpace(leeftijdsCategorie))
            ? leeftijdsCategorie
            : (isAllstars ? ExtractLeeftijdFromTeamNaam(teamNaam) ?? "" : "");

        // Laag 2 — default per leeftijdscategorie (mag null zijn: dan géén streeftijd)
        TimeOnly? defaultTijd = null;
        if (leeftijd.Length > 0 && speeltijden.TryGetValue(leeftijd, out var st))
            defaultTijd = st.StandaardVoorkeurTijd;

        // Laag 1 — eigen voorkeurstijd van het team voor deze speeldag
        TimeOnly? teamTijd = null;
        int teamPrioriteit = int.MaxValue;
        var team = teamNaam ?? "";
        if (voorkeurLookup.TryGetValue(team, out var voorkeuren) && voorkeuren.Count > 0)
        {
            var primair = voorkeuren.OrderBy(v => v.Prioriteit).First();
            teamTijd = primair.Tijd;
            teamPrioriteit = primair.Prioriteit;
        }

        // Laag 0 — voorkeursveld-regel. Een tijd óp die regel is het meest specifieke wat de
        // wedstrijdsecretaris kan opgeven en gaat dus vóór de losse voorkeurstijd van het team.
        if (voorkeurVelden.TryGetValue(team, out var vv))
        {
            return new PlanDoel(
                Laag: 0,
                Prioriteit: vv.Prioriteit,
                DoelTijd: vv.Tijd ?? teamTijd ?? defaultTijd,
                VoorkeurVeldNummer: vv.VeldNummer,
                Bron: vv.Tijd.HasValue ? "regel" : (teamTijd.HasValue ? "team" : (defaultTijd.HasValue ? "leeftijd" : null)),
                Leeftijd: leeftijd);
        }

        if (teamTijd.HasValue)
            return new PlanDoel(1, teamPrioriteit, teamTijd, null, "team", leeftijd);

        if (defaultTijd.HasValue)
            return new PlanDoel(2, 0, defaultTijd, null, "leeftijd", leeftijd);

        return new PlanDoel(3, 0, null, null, null, leeftijd);
    }

    /// <summary>
    /// Beoordeelt de afwijking t.o.v. de voorkeurstijd (#666). Dezelfde drempels als de Gantt-legenda,
    /// zodat tabel en tijdlijn hetzelfde verhaal vertellen. Bewust los van de status van de wedstrijd:
    /// die zegt alleen of de planner iets verplaatst t.o.v. de huidige stand.
    /// </summary>
    public static string BepaalVoorkeurStatus(string? voorkeurTijd, int? afwijkingMinuten)
    {
        if (voorkeurTijd == null || !afwijkingMinuten.HasValue) return "geen-voorkeur";
        int abs = Math.Abs(afwijkingMinuten.Value);
        if (abs == 0) return "op-tijd";
        if (abs <= 15) return "kleine-afwijking";
        return "grote-afwijking";
    }

    /// <summary>
    /// Leidt de leeftijdscategorie af uit de teamnaam — alleen gebruikt in demomodus, waar de bron
    /// geen categorie meelevert.
    /// </summary>
    public static string? ExtractLeeftijdFromTeamNaam(string? teamNaam)
    {
        if (string.IsNullOrWhiteSpace(teamNaam)) return null;
        var parts = teamNaam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var second = parts[1];
        var hyphenIdx = second.IndexOf('-');
        if (hyphenIdx > 0) second = second[..hyphenIdx];
        return second.ToUpperInvariant() switch
        {
            "HEREN" => "1-99",
            "DAMES" => "VR",
            "VROUWEN" => "VR",
            _ => string.IsNullOrWhiteSpace(second) ? null : second
        };
    }

    /// <summary>Sorteervolgorde per leeftijdscategorie: jongste eerst, senioren achteraan.</summary>
    public static int GetLeeftijdSortOrder(string? leeftijd)
    {
        if (string.IsNullOrWhiteSpace(leeftijd)) return 99;
        var l = leeftijd.Trim().ToUpperInvariant();
        if (l.StartsWith("JO") && int.TryParse(l[2..], out var jo)) return jo;
        if (l.StartsWith("MO") && int.TryParse(l[2..], out var mo)) return 50 + mo;
        if (l == "VR" || l.StartsWith("VROUWEN")) return 80;
        if (l.StartsWith("G")) return 85;
        return 90;
    }

    /// <summary>
    /// Standaard sorteertijd (in minuten na middernacht) voor een leeftijdscategorie zonder eigen
    /// streeftijd — jongere teams vroeger op de dag.
    /// </summary>
    public static int GetDefaultTimeSortKey(string? leeftijd)
    {
        var order = GetLeeftijdSortOrder(leeftijd);
        return order <= 11 ? 540 : order <= 13 ? 600 : order <= 15 ? 630 : order <= 17 ? 660
             : order <= 19 ? 690 : order <= 25 ? 720 : order <= 85 ? 750 : 780;
    }

    /// <summary>Bouwt de Sportlink-veldstring terug uit veldnaam plus optionele subpositie.</summary>
    public static string BuildSportlinkVeldString(string veldNaam, string subpositie)
    {
        var naam = veldNaam.Trim();
        return string.IsNullOrEmpty(subpositie) ? naam : $"{naam} {subpositie}";
    }
}
