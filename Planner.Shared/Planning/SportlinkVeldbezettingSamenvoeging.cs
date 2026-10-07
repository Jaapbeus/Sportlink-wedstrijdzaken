using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>
/// Voegt de Planning (onze eigen wedstrijden) samen met de veldplanner van Sportlink (#1582).
/// <para>
/// <b>Sportlink is leidend</b> voor wedstrijden, aanvangstijden, speelduur en velden (zie
/// <c>docs/ARCHITECTUUR.md</c>, "Sportlink is de bron van waarheid"). Waar Sportlink bereikbaar is bepaalt de
/// veldplanner dus wat de Planning toont:
/// <list type="bullet">
/// <item>een eigen regel met een Sportlink-blok neemt veld, tijd, duur en afmeting van dat blok over;</item>
/// <item>een Sportlink-blok zonder eigen regel wordt zelf een regel — bijvoorbeeld een wedstrijd van een andere club
/// op hetzelfde park, die de velden wel bezet maar niet in onze database staat;</item>
/// <item>een eigen regel die Sportlink niet kent blijft staan maar krijgt de markering "niet in Sportlink":
/// een afwijking is zichtbaar, nooit stil.</item>
/// </list>
/// Is Sportlink niet bereikbaar (<paramref name="blokken"/> is <c>null</c>), dan blijft de Planning ongewijzigd.
/// </para>
/// <para>
/// Generiek over het regeltype omdat beide databasetiers hun eigen regelmodel hebben (record resp. class, zie
/// <see cref="Planner.Shared.Deel.IVeldbezettingRegel"/>); de regels staan dus één keer hier, niet per tier.
/// </para>
/// </summary>
public static class SportlinkVeldbezettingSamenvoeging
{
    /// <summary>Waarde van het veld <c>Bron</c> voor een regel waarvan veld, tijd en duur uit de Sportlink-veldplanner komen.</summary>
    public const string BronSportlink = "Sportlink";

    /// <summary>De onderdelen van een Sportlink-blok die een tier nodig heeft om er zelf een regel van te maken.</summary>
    public sealed record NieuweRegel(string Wedstrijd, string Thuis, string? Uit, string StartTijd, string Veld, int DuurMinuten, decimal Veldafmeting);

    /// <summary>Splitst het label van een Sportlink-blok; de tiers hoeven dat niet elk te doen.</summary>
    public static NieuweRegel VanBlok(SportlinkVeldplannerBlok blok)
    {
        var (thuis, uit) = SplitsWedstrijd(blok.Label);
        return new NieuweRegel(blok.Label, thuis, uit, blok.StartTijd, blok.Veld, blok.DuurMinuten, blok.Veldafmeting);
    }

    /// <param name="eigen">De wedstrijden uit onze database voor die dag.</param>
    /// <param name="blokken">De Sportlink-veldplanner voor die dag, of <c>null</c> bij een terugval.</param>
    /// <param name="sleutel">Het teamlabel "Thuis - Uit" en de starttijd van een eigen regel.</param>
    /// <param name="overschrijf">Neemt de gegevens van een Sportlink-blok over in een eigen regel.</param>
    /// <param name="nieuw">Maakt een regel van een Sportlink-blok dat geen eigen regel heeft.</param>
    /// <param name="nietInSportlink">Markeert een eigen regel die Sportlink niet kent.</param>
    /// <param name="sorteerTijd">De aanvangstijd van een regel, voor de volgorde van het resultaat.</param>
    public static IReadOnlyList<T> Voeg<T>(
        IReadOnlyList<T> eigen, IReadOnlyList<SportlinkVeldplannerBlok>? blokken,
        Func<T, (string Label, string? Starttijd)> sleutel,
        Func<T, SportlinkVeldplannerBlok, T> overschrijf,
        Func<SportlinkVeldplannerBlok, T> nieuw,
        Func<T, T> nietInSportlink,
        Func<T, string?> sorteerTijd)
    {
        if (blokken == null) return eigen;

        var koppeling = SportlinkVeldplannerKoppeling.Koppel(eigen.Select(sleutel).ToList(), blokken);
        var resultaat = new List<T>(eigen.Count + blokken.Count);
        for (var i = 0; i < eigen.Count; i++)
            resultaat.Add(koppeling.TryGetValue(i, out var blok) ? overschrijf(eigen[i], blok) : nietInSportlink(eigen[i]));

        // Referentie-vergelijking: twee blokken met dezelfde velden zijn nog steeds twee wedstrijden.
        var gebruikt = new HashSet<SportlinkVeldplannerBlok>(koppeling.Values, ReferenceEqualityComparer.Instance);
        resultaat.AddRange(blokken.Where(b => !gebruikt.Contains(b)).Select(nieuw));

        return resultaat.OrderBy(r => string.IsNullOrWhiteSpace(sorteerTijd(r)) ? "99:99" : sorteerTijd(r), StringComparer.Ordinal).ToList();
    }

    /// <summary>Splitst "Thuis - Uit"; zonder scheidingsteken is de uitploeg <c>null</c>.</summary>
    public static (string Thuis, string? Uit) SplitsWedstrijd(string? wedstrijd)
    {
        var tekst = wedstrijd ?? "";
        var i = tekst.IndexOf(" - ", StringComparison.Ordinal);
        return i < 0 ? (tekst.Trim(), null) : (tekst[..i].Trim(), tekst[(i + 3)..].Trim());
    }

    /// <summary>
    /// De tegenstander van <paramref name="teamNaam"/> in <paramref name="wedstrijd"/>: de andere kant van het label.
    /// De Planning toonde hier de uitploeg, zodat bij een uitwedstrijd de eigen ploeg in beide kolommen stond (#1582).
    /// Is niet vast te stellen welke kant het eigen team is, dan geldt <paramref name="uitteam"/>.
    /// </summary>
    public static string? Tegenstander(string? wedstrijd, string? teamNaam, string? uitteam)
    {
        var (thuis, uit) = SplitsWedstrijd(wedstrijd);
        if (uit == null || string.IsNullOrWhiteSpace(teamNaam)) return uitteam;

        var eigenThuis = SportlinkVeldplannerKoppeling.Normaliseer(teamNaam) == SportlinkVeldplannerKoppeling.Normaliseer(thuis);
        var eigenUit = SportlinkVeldplannerKoppeling.Normaliseer(teamNaam) == SportlinkVeldplannerKoppeling.Normaliseer(uit);
        // Geen exacte treffer: een ploegnaam met een extra clubvoorvoegsel telt alleen als precies één kant past.
        if (!eigenThuis && !eigenUit)
        {
            eigenThuis = SportlinkVeldplannerKoppeling.ZijnZelfdePloeg(teamNaam, thuis);
            eigenUit = SportlinkVeldplannerKoppeling.ZijnZelfdePloeg(teamNaam, uit);
        }
        if (eigenThuis == eigenUit) return uitteam ?? uit;
        return eigenThuis ? uit : thuis;
    }
}
