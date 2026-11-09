namespace Planner.Shared.TeamResolution;

/// <summary>
/// Beslisregel voor de Sportlink-<c>teamsoort</c> bij de teamcanonicalisatie (#1785). Tier-
/// onafhankelijk, zodat SQL Server en Postgres exact dezelfde keuze maken (codekwaliteitsregel 1).
///
/// <para>
/// <b>Waarom.</b> Sportlink levert per club bondsteams (<c>teamsoort = bond</c>, KNVB-notatie;
/// daar hangen de competitiewedstrijden aan) en lokale teams (<c>teamsoort = lokaal</c>,
/// clubeigen notatie). Normaal vallen beide samen op de genormaliseerde sleutel. Heeft de
/// bondsnaam een achtervoegsel dat de lokale naam mist (<c>[club] O14-1JM</c> naast <c>JO14-1</c>),
/// dan lukt dat niet en bleven er twee actieve teams over. Het lokale team won bij een exacte
/// naammatch, maar heeft geen competitiewedstrijden. Een herplanverzoek vond daardoor "geen
/// wedstrijd", en een alias naar het bondsteam werd nooit gebruikt.
/// </para>
/// <para>
/// <b>Besluit van de eigenaar:</b> een canoniek team dat in Sportlink <i>alleen</i> als lokaal
/// voorkomt, is standaard inactief. Komt er minstens één bondsrij in de groep voor, dan blijft
/// het team actief. Andere teamsoorten (bijvoorbeeld <c>jeugd</c>/<c>senioren</c> in de
/// democlub) blijven ook actief: alleen expliciet lokaal telt.
/// </para>
/// </summary>
public static class TeamSoortBeleid
{
    public const string Bond = "bond";
    public const string Lokaal = "lokaal";

    public static bool IsBond(string? teamsoort) => Is(teamsoort, Bond);

    public static bool IsLokaal(string? teamsoort) => Is(teamsoort, Lokaal);

    /// <summary>
    /// Bepaalt of een canoniek team actief hoort te zijn, op basis van de teamsoorten van alle
    /// bronrijen die op dezelfde genormaliseerde sleutel uitkomen. Inactief alleen als er minstens
    /// één rij is en álle rijen lokaal zijn.
    /// </summary>
    public static bool IsActief(IEnumerable<string?> teamsoortenInGroep)
    {
        ArgumentNullException.ThrowIfNull(teamsoortenInGroep);
        var soorten = teamsoortenInGroep.ToList();
        return soorten.Count == 0 || !soorten.All(IsLokaal);
    }

    /// <summary>
    /// Kiest de bronrij die de canonieke weergavenaam levert, en of het team actief is. De
    /// bondsnotatie heeft voorkeur, want die staat ook in <c>his.matches.wedstrijd</c> en is wat
    /// tegenstanders gebruiken; anders de eerste rij.
    /// </summary>
    public static (T Gekozen, bool IsActief) KiesCanoniek<T>(IReadOnlyList<T> groep, Func<T, string?> teamsoort)
    {
        ArgumentNullException.ThrowIfNull(groep);
        ArgumentNullException.ThrowIfNull(teamsoort);
        if (groep.Count == 0) throw new ArgumentException("Een teamgroep heeft minstens één rij.", nameof(groep));

        // Geen FirstOrDefault(...) ?? groep[0]: bij een value type is "default" niet null en zou de
        // terugval nooit gebeuren.
        var gekozen = groep[0];
        foreach (var rij in groep)
        {
            if (!IsBond(teamsoort(rij))) continue;
            gekozen = rij;
            break;
        }
        return (gekozen, IsActief(groep.Select(teamsoort)));
    }

    private static bool Is(string? teamsoort, string verwacht)
        => string.Equals(teamsoort?.Trim(), verwacht, StringComparison.OrdinalIgnoreCase);
}
