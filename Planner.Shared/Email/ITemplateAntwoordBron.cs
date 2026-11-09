using Newtonsoft.Json.Linq;

namespace Planner.Shared.Email;

/// <summary>
/// De tier-afhankelijke bouwstenen van <see cref="TemplateAntwoordBouwer"/> (#1627). De beslislogica
/// (welke tak bij welke vlag in het plannerresponse, in welke volgorde) staat in Planner.Shared; deze
/// naad levert per databasetier de typen die daar niet bestaan: <c>BerichtResponseGenerator</c>, de
/// DTO's van het plannerresponse, <c>EmailTemplate</c> en de sjabloonopslag.
/// <para>
/// Een instantie hoort bij één antwoord en kent dus classificatie, bericht, clubinstellingen en trace.
/// DTO-gegevens komen als <see cref="JToken"/> binnen en worden in de adapter getypeerd, zodat de
/// tier-specifieke modelklassen niet naar Planner.Shared hoeven te verhuizen. Voor de placeholders van een
/// databasesjabloon (#1780) leest de bouwer ze via <see cref="LeesWedstrijd"/> en
/// <see cref="LeesBeschikbaarheid"/> terug als de gedeelde poorten.
/// </para>
/// </summary>
public interface ITemplateAntwoordBron
{
    /// <summary>
    /// Zet de wedstrijd-DTO om en gooit bij een ongeldige DTO (<c>JsonSerializationException</c>). Wordt bij een
    /// herplanverzoek direct na het parsen aangeroepen, vóór takkeuze en sjabloonophaling (gedrag van voor #1627).
    /// </summary>
    void ValideerWedstrijd(JToken? wedstrijd) => LeesWedstrijd(wedstrijd);

    /// <summary>De wedstrijd uit het plannerresponse als gedeelde poort; <c>null</c> als er geen is. Gooit bij een ongeldige DTO.</summary>
    IAntwoordWedstrijd? LeesWedstrijd(JToken? wedstrijd);

    /// <summary>Het beschikbaarheidsresponse als gedeelde poort; een leeg response (niet beschikbaar) als het token ontbreekt.</summary>
    IAntwoordBeschikbaarheid LeesBeschikbaarheid(JToken? beschikbaarheid);

    /// <summary>De classificatie van het bericht waarop wordt geantwoord (voor de placeholders <c>overzicht</c> en <c>bijlage_zin</c>).</summary>
    BerichtClassificatie Classificatie { get; }

    (string onderwerp, string body) WedstrijdAlIngepland(JToken? wedstrijd);

    /// <param name="tegenstander">Tegenstander uit het plannerresponse; <c>null</c> = val terug op de classificatie.</param>
    (string onderwerp, string body) TeamOnbekend(string? tegenstander);

    (string onderwerp, string body) DatumOnbekend();

    (string onderwerp, string body) MultiDatum(IReadOnlyList<(string datum, JToken? response)> resultaten);

    /// <summary>Standaard beschikbaarheidsantwoord: deserialiseert het response, probeert het sjabloon <c>beschikbaarheid_check</c>.</summary>
    Task<(string onderwerp, string body)> BeschikbaarheidStandaardAsync(string plannerResponseJson);

    (string onderwerp, string body) HerplanTeLaat(JToken? wedstrijd, int deadlineDagen, int dagenTotWedstrijd);

    (string onderwerp, string body) VerzetZonderDatum(JToken? wedstrijd, List<string> vrijeZaterdagen);

    (string onderwerp, string body) HerplanGewensteDatum(JToken? wedstrijd, string? gewensteDatum, JToken? beschikbaarheid);

    (string onderwerp, string body) HerplanStandaard(JToken? wedstrijd, JToken? herplanOpties);

    /// <summary>
    /// Haalt het databasesjabloon <paramref name="sleutel"/> op en bouwt daarmee het antwoord (inclusief
    /// trace-melding); <c>null</c> als er geen sjabloon is en het ingebouwde antwoord moet gelden.
    /// </summary>
    /// <param name="extraPlaceholders">Placeholders bovenop de basisset, zie <c>BerichtAntwoordGenerator.Sjabloon.cs</c> (#1780).</param>
    /// <param name="gecontroleerdDagdeel">Het dagdeel dat de planner controleerde; vult <c>{{dagdeel}}</c> en de controlezin.</param>
    Task<(string onderwerp, string body)?> ProbeerSjabloonAsync(
        string sleutel, IDictionary<string, string>? extraPlaceholders = null, string? gecontroleerdDagdeel = null);

    (string onderwerp, string body) TeamContactStandaard();

    (string onderwerp, string body) BevestigingStandaard();

    (string onderwerp, string body) BuitenScopeStandaard();
}
