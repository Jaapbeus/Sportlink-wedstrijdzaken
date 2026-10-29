using System.Globalization;

namespace Planner.Shared.Email;

// Placeholders voor databasesjablonen (#1780): de gegevens waarmee een eigen sjabloon de dynamische delen van
// een ingebouwd antwoord kan invullen. Ze komen bovenop de basisplaceholders van BouwAangepasteAntwoord
// (voornaam, aanhef, datum, team, tegenstander, aanvangstijd, dagdeel, coordinator). Een gegeven dat er niet
// is, geeft een lege waarde.
public static partial class BerichtAntwoordGenerator
{
    private static Dictionary<string, string> NieuwePlaceholders()
        => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>De wedstrijd zoals de planner die vond: <c>wedstrijd</c>, <c>wedstrijd_datum</c>, <c>wedstrijd_aanvangstijd</c>, <c>wedstrijd_veld</c>.</summary>
    public static Dictionary<string, string> WedstrijdPlaceholders(IAntwoordWedstrijd? wedstrijd)
    {
        var placeholders = NieuwePlaceholders();
        placeholders["wedstrijd"] = wedstrijd?.Wedstrijd ?? "";
        placeholders["wedstrijd_datum"] = wedstrijd == null ? "" : FormatDatum(wedstrijd.Datum);
        placeholders["wedstrijd_aanvangstijd"] = wedstrijd?.AanvangsTijd ?? "";
        placeholders["wedstrijd_veld"] = wedstrijd?.VeldNaam ?? "";
        return placeholders;
    }

    /// <summary>
    /// <c>tegenstander</c> zoals de planner die teruggaf (de classificatie kan afwijken). Geeft de planner er geen,
    /// dan blijft de basisplaceholder uit de classificatie staan, net als in het ingebouwde antwoord.
    /// </summary>
    public static Dictionary<string, string> TeamOnbekendPlaceholders(string? tegenstander)
    {
        var placeholders = NieuwePlaceholders();
        if (!string.IsNullOrWhiteSpace(tegenstander))
            placeholders["tegenstander"] = AiVeldSanering.TegenstanderOfGeneriek(tegenstander, "de opgegeven tegenstander");
        return placeholders;
    }

    /// <summary><c>overzicht</c>: per datum wat wel en niet kan, inclusief de controlezin over het dagdeel; zoals in het ingebouwde antwoord.</summary>
    public static Dictionary<string, string> MultiDatumPlaceholders(
        IReadOnlyList<(string datum, IAntwoordBeschikbaarheid response)> resultaten, BerichtClassificatie classificatie)
    {
        var overzicht = "";
        foreach (var (datum, response) in resultaten)
            overzicht += BouwDatumSectie(datum, response, classificatie) + "\n";
        overzicht += DagdeelVenster.ControleAlinea(resultaten.Select(r => r.response.GecontroleerdDagdeel));

        var placeholders = NieuwePlaceholders();
        placeholders["overzicht"] = overzicht.TrimEnd();
        return placeholders;
    }

    /// <summary>De wedstrijd plus <c>deadline_dagen</c> en <c>dagen_tot_wedstrijd</c>.</summary>
    public static Dictionary<string, string> HerplanTeLaatPlaceholders(
        IAntwoordWedstrijd? wedstrijd, int deadlineDagen, int dagenTotWedstrijd)
    {
        var placeholders = WedstrijdPlaceholders(wedstrijd);
        placeholders["deadline_dagen"] = deadlineDagen.ToString(CultureInfo.InvariantCulture);
        placeholders["dagen_tot_wedstrijd"] = dagenTotWedstrijd.ToString(CultureInfo.InvariantCulture);
        return placeholders;
    }

    /// <summary>
    /// De wedstrijd plus <c>vrije_zaterdagen</c> (de voorzet met de lijst, of de zin dat er geen zijn) en
    /// <c>bijlage_zin</c> (alleen gevuld als de KNVB-kalender daadwerkelijk als bijlage meegaat).
    /// </summary>
    public static Dictionary<string, string> VerzetZonderDatumPlaceholders(
        IAntwoordWedstrijd? wedstrijd, IReadOnlyList<string> vrijeZaterdagen, bool knvbBijlage)
    {
        var placeholders = WedstrijdPlaceholders(wedstrijd);
        placeholders["vrije_zaterdagen"] = VrijeZaterdagenTekst(vrijeZaterdagen).TrimEnd();
        placeholders["bijlage_zin"] = knvbBijlage ? KnvbBijlageZin : "";
        return placeholders;
    }

    /// <summary>De wedstrijd plus <c>gewenste_datum</c> en <c>mogelijkheden</c> (wat er op de gewenste datum kan).</summary>
    public static Dictionary<string, string> HerplanGewensteDatumPlaceholders(
        IAntwoordWedstrijd? wedstrijd, string? gewensteDatum, IAntwoordBeschikbaarheid? beschikbaarheid)
    {
        var gewenstDatumTekst = FormatDatum(gewensteDatum);
        var placeholders = WedstrijdPlaceholders(wedstrijd);
        placeholders["gewenste_datum"] = gewenstDatumTekst;
        placeholders["mogelijkheden"] = GewensteDatumMogelijkheden(gewenstDatumTekst, beschikbaarheid);
        return placeholders;
    }
}
