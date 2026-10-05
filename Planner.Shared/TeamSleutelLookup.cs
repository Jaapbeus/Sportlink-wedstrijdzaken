namespace Planner.Shared;

/// <summary>
/// Zoekt een per-team ingestelde waarde (voorkeurstijd, voorkeursveld) op voor een teamnaam uit een
/// andere bron (#1545).
///
/// <para>
/// De beheertabellen bewaren de naam zoals de beheerder hem koos (bijv. "JO23-4"), de wedstrijdbron
/// levert de KNVB-notatie ("[club] O23-4"). Een exacte stringvergelijking vond de instelling daardoor
/// nooit en de planning viel stilzwijgend terug op de default van de leeftijdscategorie.
/// </para>
///
/// <para>
/// Volgorde: (1) exacte sleutel → (2) gelijke genormaliseerde sleutel
/// (<see cref="TeamNaamNormalisatie.NormaliseerVoorVergelijking"/>) → (3) bij een aanduiding zonder
/// geslacht-prefix ("23-4") alleen als precies één team dezelfde leeftijd en hetzelfde teamnummer
/// heeft. Meerdere kandidaten → niets gevonden; er wordt nooit gekozen tussen JO en MO.
/// </para>
/// </summary>
public static class TeamSleutelLookup
{
    public static bool TryGetValue<T>(
        IReadOnlyDictionary<string, T> bron, string? teamNaam, string? clubPrefix, out T waarde)
    {
        waarde = default!;
        if (string.IsNullOrWhiteSpace(teamNaam) || bron.Count == 0) return false;

        if (bron.TryGetValue(teamNaam, out var exact))
        {
            waarde = exact;
            return true;
        }

        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(teamNaam, clubPrefix);
        if (sleutel.Length == 0) return false;

        // Ordinal gesorteerd, zodat twee schrijfwijzen van hetzelfde team in de beheertabel
        // ("JO23-4" én "[club] O23-4") altijd dezelfde winnaar opleveren.
        var genormaliseerd = bron.Keys
            .Select(k => (Sleutel: k, Norm: TeamNaamNormalisatie.NormaliseerVoorVergelijking(k, clubPrefix)))
            .OrderBy(k => k.Sleutel, StringComparer.Ordinal)
            .ToList();

        var gelijk = genormaliseerd.FirstOrDefault(k => k.Norm == sleutel);
        if (gelijk.Sleutel is not null)
        {
            waarde = bron[gelijk.Sleutel];
            return true;
        }

        var gezocht = TeamNaamNormalisatie.Parse(teamNaam, clubPrefix);
        if (gezocht is null || gezocht.Prefix is not null) return false;

        var kandidaten = genormaliseerd
            .Where(k => TeamNaamNormalisatie.Parse(k.Norm) is { } c
                        && c.LeeftijdNummer == gezocht.LeeftijdNummer
                        && c.TeamNummer == gezocht.TeamNummer)
            .GroupBy(k => k.Norm)
            .ToList();
        if (kandidaten.Count != 1) return false;

        waarde = bron[kandidaten[0].First().Sleutel];
        return true;
    }
}
