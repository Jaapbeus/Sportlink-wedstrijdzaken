namespace Planner.Shared.Email;

// Alleen BCL: dit bestand wordt als gelinkt bronbestand ook in BlazorAdmin gecompileerd (#1461, #1780), zodat
// de verwerker en de beheerpagina dezelfde sleutels, uitleg, placeholders en standaardteksten gebruiken.

/// <summary>
/// De sleutels waaronder een databasesjabloon (Beheer &gt; E-mailtemplates, tabel
/// <c>emailtemplateinstellingen</c>) een ingebouwd antwoord van de e-mailverwerker vervangt (#1780). Elk
/// antwoord dat <c>TemplateAntwoordBouwer</c> kan kiezen heeft hier één sleutel; zonder actieve rij
/// voor de club blijft de ingebouwde tekst gelden.
/// </summary>
public static class AntwoordSjabloonSleutels
{
    public const string BeschikbaarheidCheck = "beschikbaarheid_check";
    public const string HerplanVerzoek = "herplan_verzoek";
    public const string TeamContactOpvragen = "team_contact_opvragen";
    public const string Bevestiging = "bevestiging";
    public const string BuitenScope = "buiten_scope";

    public const string WedstrijdAlIngepland = "wedstrijd_al_ingepland";
    public const string TeamOnbekend = "team_onbekend";
    public const string DatumOnbekend = "datum_onbekend";
    public const string MultiDatum = "multi_datum";
    public const string HerplanTeLaat = "herplan_te_laat";
    public const string VerzetZonderDatum = "verzet_zonder_datum";
    public const string HerplanGewensteDatum = "herplan_gewenste_datum";
}

/// <summary>Eén overschrijfbaar antwoord: wanneer het geldt, welke placeholders het kent en de standaardtekst voor het formulier.</summary>
/// <param name="Groep">Rubriek in de keuzelijst: Beschikbaarheid, Herplanning, Teambegeleiding, Bevestiging of Overig.</param>
/// <param name="Wanneer">Wanneer de verwerker dit antwoord kiest, in gewone taal.</param>
/// <param name="ExtraPlaceholders">Placeholders bovenop <see cref="AntwoordSjabloonCatalogus.BasisPlaceholders"/>.</param>
/// <param name="Opmerking">Wat de beheerder moet weten voordat hij de tekst overschrijft; leeg als er niets bijzonders is.</param>
/// <param name="StandaardOnderwerp">Leeg betekent: <c>Re: &lt;onderwerp van de mail&gt;</c>.</param>
/// <param name="StandaardBody">Zo dicht mogelijk bij de ingebouwde tekst; de handtekening komt er automatisch onder.</param>
public sealed record AntwoordSjabloonInfo(
    string Sleutel,
    string Groep,
    string Wanneer,
    IReadOnlyList<string> ExtraPlaceholders,
    string Opmerking,
    string StandaardOnderwerp,
    string StandaardBody);

/// <summary>De twaalf overschrijfbare antwoorden, in de volgorde van de beheerpagina.</summary>
public static class AntwoordSjabloonCatalogus
{
    /// <summary>Placeholders die elk sjabloon kent (<c>BerichtAntwoordGenerator.BouwAangepasteAntwoord</c>).</summary>
    public static IReadOnlyList<string> BasisPlaceholders { get; } =
        ["voornaam", "aanhef", "datum", "team", "tegenstander", "aanvangstijd", "dagdeel", "coordinator"];

    private static readonly string[] WedstrijdPlaceholders =
        ["wedstrijd", "wedstrijd_datum", "wedstrijd_aanvangstijd", "wedstrijd_veld"];

    private const string AlleenMetWedstrijd =
        "Geldt alleen als de planner de wedstrijd vond; anders blijft de ingebouwde tekst staan die uitlegt dat er geen wedstrijd is gevonden.";

    private const string GaatTerReview =
        "Dit antwoord vraagt de afzender om informatie en gaat standaard eerst ter review (zekerheidspoort), ook met een eigen tekst.";

    public static IReadOnlyList<AntwoordSjabloonInfo> Alle { get; } =
    [
        new(AntwoordSjabloonSleutels.BeschikbaarheidCheck, "Beschikbaarheid",
            "Beschikbaarheidsvraag waar we op antwoorden omdat het niet past, en geen van de bijzondere gevallen hieronder geldt.",
            [],
            "Vervangt ook de lijst met beschikbare tijden en alternatieven uit de standaardtekst; die staan niet in een eigen tekst.",
            "Beschikbaarheid wedstrijd {{datum}} — {{team}} vs {{tegenstander}}",
            "Beste {{aanhef}},\n\nWij willen graag informeren of jullie beschikbaar zijn voor de wedstrijd op {{datum}} om {{aanvangstijd}}.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nKunt u ons zo spoedig mogelijk laten weten of de locatie en het tijdstip uitkomen?"),

        new(AntwoordSjabloonSleutels.WedstrijdAlIngepland, "Beschikbaarheid",
            "De wedstrijd staat al in het programma; de afzender krijgt alleen informatie.",
            WedstrijdPlaceholders, AlleenMetWedstrijd,
            "",
            "{{aanhef}} {{voornaam}},\n\nDe wedstrijd {{wedstrijd}} staat al ingepland op {{wedstrijd_datum}} om {{wedstrijd_aanvangstijd}} op {{wedstrijd_veld}}."),

        new(AntwoordSjabloonSleutels.TeamOnbekend, "Beschikbaarheid",
            "We herkennen het team of de wedstrijd niet en vragen de afzender om welk team het gaat.",
            [], GaatTerReview,
            "",
            "{{aanhef}} {{voornaam}},\n\nWe kunnen de wedstrijd van {{tegenstander}} niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren."),

        new(AntwoordSjabloonSleutels.DatumOnbekend, "Beschikbaarheid",
            "In de mail staat geen bruikbare datum; de afzender wordt om een datum gevraagd.",
            [], GaatTerReview,
            "",
            "{{aanhef}} {{voornaam}},\n\nBedankt voor je bericht. We konden er geen concrete datum uit opmaken. Kun je aangeven welke datum of datums je in gedachten hebt? Dan controleren we de beschikbaarheid van onze velden voor je."),

        new(AntwoordSjabloonSleutels.MultiDatum, "Beschikbaarheid",
            "De afzender vroeg om meer dan één datum en minstens één datum past niet.",
            ["overzicht"],
            "{{overzicht}} bevat per datum wat wel en niet kan, inclusief de tijden en alternatieven. {{coordinator}} is leeg als er geen coördinator is ingesteld.",
            "",
            "{{aanhef}} {{voornaam}},\n\n{{overzicht}}\n\nLaat weten welke optie(s) de voorkeur hebben, dan gaan we samen met {{coordinator}} plannen en definitief opnemen in de planning."),

        new(AntwoordSjabloonSleutels.HerplanVerzoek, "Herplanning",
            "Herplanverzoek waarvoor geen van de bijzondere gevallen hieronder geldt (het algemene antwoord met alternatieven).",
            WedstrijdPlaceholders,
            "Vervangt ook de lijst met eerdere en latere mogelijkheden uit de standaardtekst; die staan niet in een eigen tekst.",
            "Verzoek herplanning wedstrijd {{datum}} — {{team}} vs {{tegenstander}}",
            "Beste {{aanhef}},\n\nWij verzoeken u vriendelijk de geplande wedstrijd van {{datum}} om {{aanvangstijd}} te herplannen.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nZou u ons een alternatieve datum en tijd kunnen voorstellen?"),

        new(AntwoordSjabloonSleutels.HerplanTeLaat, "Herplanning",
            "Het herplanverzoek komt binnen de termijn waarbinnen verzetten niet meer automatisch kan.",
            [.. WedstrijdPlaceholders, "deadline_dagen", "dagen_tot_wedstrijd"], AlleenMetWedstrijd,
            "",
            "{{aanhef}} {{voornaam}},\n\nDe wedstrijd {{wedstrijd}} staat gepland op {{wedstrijd_datum}} om {{wedstrijd_aanvangstijd}} op {{wedstrijd_veld}}. Dat is over {{dagen_tot_wedstrijd}} dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal {{deadline_dagen}} dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator."),

        new(AntwoordSjabloonSleutels.VerzetZonderDatum, "Herplanning",
            "De tegenstander wil verzetten maar noemt geen nieuwe datum; wij geven vrije zaterdagen als voorzet.",
            [.. WedstrijdPlaceholders, "vrije_zaterdagen", "bijlage_zin"],
            AlleenMetWedstrijd + " {{bijlage_zin}} is alleen gevuld als de KNVB-kalender als bijlage meegaat; de begeleiding zit dan in BCC.",
            "",
            "{{aanhef}} {{voornaam}},\n\nBedankt voor je verzoek om de wedstrijd {{wedstrijd}} op {{wedstrijd_datum}} om {{wedstrijd_aanvangstijd}} te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\n{{vrije_zaterdagen}}\n\n{{bijlage_zin}}"),

        new(AntwoordSjabloonSleutels.HerplanGewensteDatum, "Herplanning",
            "Het herplanverzoek noemt een gewenste nieuwe datum en we hebben gecontroleerd wat daar kan.",
            [.. WedstrijdPlaceholders, "gewenste_datum", "mogelijkheden"],
            AlleenMetWedstrijd + " {{mogelijkheden}} zegt of er ruimte is op de gewenste datum, met de beschikbare velden en tijden.",
            "",
            "{{aanhef}} {{voornaam}},\n\nDe wedstrijd {{wedstrijd}} staat momenteel gepland op {{wedstrijd_datum}} om {{wedstrijd_aanvangstijd}} op {{wedstrijd_veld}}.\n\n{{mogelijkheden}}"),

        new(AntwoordSjabloonSleutels.TeamContactOpvragen, "Teambegeleiding",
            "Iemand vraagt naar de contactpersoon van een team; de vraag wordt doorgestuurd naar de begeleider.",
            [], "",
            "Uw vraag over de begeleiding van {{team}}",
            "Beste {{voornaam}},\n\nUw vraag over de begeleiding van {{team}} is doorgestuurd. De begeleider neemt rechtstreeks contact met u op."),

        new(AntwoordSjabloonSleutels.Bevestiging, "Bevestiging",
            "Iemand bevestigt iets; de afzender krijgt een korte ontvangstbevestiging.",
            [], "",
            "Bevestiging wedstrijd {{datum}} — {{team}} vs {{tegenstander}}",
            "Beste {{aanhef}},\n\nHierbij bevestigen wij de wedstrijd op {{datum}} om {{aanvangstijd}}.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nTot dan!"),

        new(AntwoordSjabloonSleutels.BuitenScope, "Overig",
            "Vangnet voor een verzoek dat de verwerker niet herkent; komt zelden voor.",
            [], "",
            "Uw bericht ontvangen",
            "Beste {{voornaam}},\n\nBedankt voor uw bericht. Uw vraag valt buiten het bereik van de automatische verwerking. Neem contact op met de club voor verdere hulp."),
    ];

    /// <summary>De gegevens van een sleutel, of <c>null</c> als het geen bekende sleutel is.</summary>
    public static AntwoordSjabloonInfo? Zoek(string? sleutel)
        => Alle.FirstOrDefault(i => string.Equals(i.Sleutel, sleutel, StringComparison.Ordinal));

    /// <summary>Alle placeholders van een sleutel: de basisset plus de eigen.</summary>
    public static IReadOnlyList<string> PlaceholdersVoor(string? sleutel)
        => [.. BasisPlaceholders, .. Zoek(sleutel)?.ExtraPlaceholders ?? []];
}
