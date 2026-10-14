using AwesomeAssertions;
using Planner.Shared.Email;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Karakterisatietests (#1627) voor <c>BerichtPipeline.BouwTemplateAntwoord</c>: legt per tak (vlag-volgorde,
/// standaardantwoord, sjabloon-override) de uitkomst vast op de ongewijzigde code, zodat de verhuizing naar
/// Planner.Shared aantoonbaar geen gedragswijziging is. De verwachte waarden zijn gelijk aan die van de SQL
/// Postgres-tier (FunctionApp.Postgres.Tests). Geen database nodig: het sjabloon komt uit een stub.
/// </summary>
public class BouwTemplateAntwoordKarakterisatieTests
{
    private static readonly System.Text.RegularExpressions.Regex Aanhef = new("^(Goedemorgen|Goedemiddag|Goedenavond)");

    public sealed record Geval(string Naam, VerzoekType Type, string PlannerJson, string? Tegenstander);

    private sealed record Verwacht(string Onderwerp, string Body, string Trace);

    private static readonly Geval[] Gevallen =
    {
        new("b-alIngepland", VerzoekType.BeschikbaarheidCheck, "{\"wedstrijdAlIngepland\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", "Gast JO13-1"),
        new("b-teamOnbekend", VerzoekType.BeschikbaarheidCheck, "{\"teamOnbekend\":true,\"tegenstander\":\"Onbekend JO13-1\"}", "Gast JO13-1"),
        new("b-teamOnbekend-zonderTegenstanderInJson", VerzoekType.BeschikbaarheidCheck, "{\"teamOnbekend\":true}", "Gast JO13-1"),
        new("b-teamOnbekend-nergensTegenstander", VerzoekType.BeschikbaarheidCheck, "{\"teamOnbekend\":true}", null),
        new("b-datumOnbekend", VerzoekType.BeschikbaarheidCheck, "{\"datumOnbekend\":true}", null),
        new("b-multiDatum", VerzoekType.BeschikbaarheidCheck, "{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-11-14\",\"response\":{\"beschikbaar\":true}},{\"datum\":\"2026-11-21\"}]}", null),
        new("b-standaard", VerzoekType.BeschikbaarheidCheck, "{\"beschikbaar\":true}", null),
        new("b-standaard-metDagdeel", VerzoekType.BeschikbaarheidCheck, "{\"beschikbaar\":true,\"gecontroleerdDagdeel\":\"ochtend\"}", null),
        new("b-standaard-nietBeschikbaar", VerzoekType.BeschikbaarheidCheck, "{\"beschikbaar\":false,\"reden\":\"Geen veld vrij\"}", null),
        new("b-vlagenFalse", VerzoekType.BeschikbaarheidCheck, "{\"wedstrijdAlIngepland\":false,\"teamOnbekend\":false,\"datumOnbekend\":false,\"multiDatum\":false,\"beschikbaar\":true}", null),
        new("b-voorrang-alIngepland-boven-teamOnbekend", VerzoekType.BeschikbaarheidCheck, "{\"wedstrijdAlIngepland\":true,\"teamOnbekend\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("b-voorrang-teamOnbekend-boven-datumOnbekend", VerzoekType.BeschikbaarheidCheck, "{\"teamOnbekend\":true,\"datumOnbekend\":true,\"tegenstander\":\"Onbekend JO13-1\"}", null),
        new("b-voorrang-datumOnbekend-boven-multiDatum", VerzoekType.BeschikbaarheidCheck, "{\"datumOnbekend\":true,\"multiDatum\":true,\"resultaten\":[]}", null),
        new("h-teLaat", VerzoekType.HerplanVerzoek, "{\"herplanTeLaat\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"},\"deadlineDagen\":8,\"dagenTotWedstrijd\":3}", null),
        new("h-teLaat-standaardwaarden", VerzoekType.HerplanVerzoek, "{\"herplanTeLaat\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("h-verzetZonderDatum", VerzoekType.HerplanVerzoek, "{\"verzetZonderDatum\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"},\"vrijeZaterdagen\":[\"2026-11-14\",\"2026-11-21\"]}", null),
        new("h-verzetZonderDatum-zonderLijst", VerzoekType.HerplanVerzoek, "{\"verzetZonderDatum\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("h-gewensteDatum", VerzoekType.HerplanVerzoek, "{\"gewensteDatum\":\"2026-11-28\",\"beschikbaarheid\":{\"beschikbaar\":true},\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("h-gewensteDatum-zonderBeschikbaarheid", VerzoekType.HerplanVerzoek, "{\"gewensteDatum\":\"2026-11-28\",\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("h-opties", VerzoekType.HerplanVerzoek, "{\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"},\"herplanOpties\":{\"beschikbaar\":true,\"huidigeWedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}}", null),
        new("h-leeg", VerzoekType.HerplanVerzoek, "{}", null),
        new("h-voorrang-teLaat-boven-verzetZonderDatum", VerzoekType.HerplanVerzoek, "{\"herplanTeLaat\":true,\"verzetZonderDatum\":true,\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("h-voorrang-verzetZonderDatum-boven-gewensteDatum", VerzoekType.HerplanVerzoek, "{\"verzetZonderDatum\":true,\"gewensteDatum\":\"2026-11-28\",\"beschikbaarheid\":{\"beschikbaar\":true},\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\"}}", null),
        new("tc-standaard", VerzoekType.TeamContactOpvragen, "{}", null),
        new("bv-standaard", VerzoekType.Bevestiging, "{}", null),
        new("bs-standaard", VerzoekType.BuitenScope, "{}", null)
    };

    private static readonly Dictionary<string, Verwacht> Golden = new()
    {
        ["h-verzetZonderDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nAls voorzet: volgens ons huidige programma hebben we op de volgende zaterdagen nog geen wedstrijd:\n- zaterdag 14 november 2026\n- zaterdag 21 november 2026\n\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator"),
        ["h-teLaat|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 3 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["h-leeg|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nEr is geen wedstrijd gevonden voor JO13-2 op de opgegeven datum. Controleer de teamnaam en datum en probeer het opnieuw.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/generator"),
        ["b-voorrang-datumOnbekend-boven-multiDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bericht. We konden er geen concrete datum uit opmaken. Kun je aangeven welke datum of datums je in gedachten hebt? Dan controleren we de beschikbaarheid van onze velden voor je.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=datumOnbekend;sjabloon=datumOnbekend/generator"),
        ["h-gewensteDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat momenteel gepland op zaterdag 14 november 2026 om 10:30 op .\n\nOp zaterdag 28 november 2026 is er ruimte beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=gewensteDatum;sjabloon=gewensteDatum/generator"),
        ["b-standaard|override"] = new("Override-onderwerp beschikbaarheid_check", "{aanhef} Jan, override-tekst voor beschikbaarheid_check\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/database-override"),
        ["b-vlagenFalse|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nOp de opgegeven datum is helaas geen veld beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/generator"),
        ["b-teamOnbekend-nergensTegenstander|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van  niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["h-gewensteDatum-zonderBeschikbaarheid|override"] = new("Override-onderwerp herplan_verzoek", "{aanhef} Jan, override-tekst voor herplan_verzoek\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/database-override"),
        ["b-voorrang-alIngepland-boven-teamOnbekend|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat al ingepland op zaterdag 14 november 2026 om 10:30.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=wedstrijdAlIngepland;sjabloon=wedstrijdAlIngepland/generator"),
        ["b-teamOnbekend-nergensTegenstander|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van  niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["h-gewensteDatum-zonderBeschikbaarheid|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 op  kan helaas niet verplaatst worden. Er zijn geen alternatieven beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/generator"),
        ["tc-standaard|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nUw vraag over de begeleiding van JO13-2 is doorgestuurd. De begeleider neemt rechtstreeks contact met u op. Contactgegevens worden niet gedeeld conform AVG.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=team_contact_opvragen/generator"),
        ["b-standaard-nietBeschikbaar|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nOp de opgegeven datum is helaas geen veld beschikbaar. Geen veld vrij\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/generator"),
        ["b-voorrang-teamOnbekend-boven-datumOnbekend|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Onbekend JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["b-alIngepland|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat al ingepland op zaterdag 14 november 2026 om 10:30.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=wedstrijdAlIngepland;sjabloon=wedstrijdAlIngepland/generator"),
        ["b-standaard|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nOp de opgegeven datum is helaas geen veld beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/generator"),
        ["b-datumOnbekend|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bericht. We konden er geen concrete datum uit opmaken. Kun je aangeven welke datum of datums je in gedachten hebt? Dan controleren we de beschikbaarheid van onze velden voor je.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=datumOnbekend;sjabloon=datumOnbekend/generator"),
        ["b-voorrang-alIngepland-boven-teamOnbekend|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat al ingepland op zaterdag 14 november 2026 om 10:30.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=wedstrijdAlIngepland;sjabloon=wedstrijdAlIngepland/generator"),
        ["h-leeg|override"] = new("Override-onderwerp herplan_verzoek", "{aanhef} Jan, override-tekst voor herplan_verzoek\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/database-override"),
        ["h-teLaat-standaardwaarden|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 0 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["tc-standaard|override"] = new("Override-onderwerp team_contact_opvragen", "{aanhef} Jan, override-tekst voor team_contact_opvragen\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=team_contact_opvragen/database-override"),
        ["b-voorrang-teamOnbekend-boven-datumOnbekend|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Onbekend JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["b-teamOnbekend-zonderTegenstanderInJson|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Gast JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["bs-standaard|override"] = new("Override-onderwerp buiten_scope", "{aanhef} Jan, override-tekst voor buiten_scope\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=buiten_scope/database-override"),
        ["b-teamOnbekend|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Onbekend JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["h-verzetZonderDatum-zonderLijst|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nOp korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator"),
        ["b-datumOnbekend|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bericht. We konden er geen concrete datum uit opmaken. Kun je aangeven welke datum of datums je in gedachten hebt? Dan controleren we de beschikbaarheid van onze velden voor je.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=datumOnbekend;sjabloon=datumOnbekend/generator"),
        ["b-alIngepland|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat al ingepland op zaterdag 14 november 2026 om 10:30.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=wedstrijdAlIngepland;sjabloon=wedstrijdAlIngepland/generator"),
        ["h-opties|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 op  kan helaas niet verplaatst worden. Er zijn geen alternatieven beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/generator"),
        ["h-voorrang-teLaat-boven-verzetZonderDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 0 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["h-opties|override"] = new("Override-onderwerp herplan_verzoek", "{aanhef} Jan, override-tekst voor herplan_verzoek\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=herplan_verzoek/database-override"),
        ["b-multiDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\n**zaterdag 14 november 2026:** Helaas geen veld beschikbaar.\n\n**zaterdag 21 november 2026:** Helaas geen veld beschikbaar.\n\nLaat weten welke optie(s) de voorkeur hebben, dan gaan we samen plannen en definitief opnemen in de planning.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=multiDatum;sjabloon=multiDatum/generator"),
        ["b-vlagenFalse|override"] = new("Override-onderwerp beschikbaarheid_check", "{aanhef} Jan, override-tekst voor beschikbaarheid_check\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/database-override"),
        ["h-teLaat-standaardwaarden|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 0 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["h-verzetZonderDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nAls voorzet: volgens ons huidige programma hebben we op de volgende zaterdagen nog geen wedstrijd:\n- zaterdag 14 november 2026\n- zaterdag 21 november 2026\n\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator"),
        ["h-verzetZonderDatum-zonderLijst|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nOp korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator"),
        ["b-standaard-metDagdeel|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nOp de opgegeven datum in de ochtend (08:30 - 12:00) is helaas niets beschikbaar.\n\nLet op: we hebben alleen de ochtend (08:30 - 12:00) gecontroleerd; andere dagdelen zijn niet meegenomen in dit antwoord.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/generator"),
        ["b-standaard-nietBeschikbaar|override"] = new("Override-onderwerp beschikbaarheid_check", "{aanhef} Jan, override-tekst voor beschikbaarheid_check\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/database-override"),
        ["b-teamOnbekend-zonderTegenstanderInJson|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Gast JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["b-standaard-metDagdeel|override"] = new("Override-onderwerp beschikbaarheid_check", "{aanhef} Jan, override-tekst voor beschikbaarheid_check\n\nLet op: we hebben alleen de ochtend (08:30 - 12:00) gecontroleerd; andere dagdelen zijn niet meegenomen in dit antwoord.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=beschikbaarheid_check/database-override"),
        ["h-gewensteDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat momenteel gepland op zaterdag 14 november 2026 om 10:30 op .\n\nOp zaterdag 28 november 2026 is er ruimte beschikbaar.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=gewensteDatum;sjabloon=gewensteDatum/generator"),
        ["h-voorrang-teLaat-boven-verzetZonderDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 0 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["bv-standaard|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bevestiging. Het verzoek is geregistreerd en wordt door de coördinator verwerkt.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=bevestiging/generator"),
        ["bs-standaard|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bericht. Dit verzoek vereist handmatige afhandeling en is ter beoordeling bij de coördinator neergelegd.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=buiten_scope/generator"),
        ["b-multiDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\n**zaterdag 14 november 2026:** Helaas geen veld beschikbaar.\n\n**zaterdag 21 november 2026:** Helaas geen veld beschikbaar.\n\nLaat weten welke optie(s) de voorkeur hebben, dan gaan we samen plannen en definitief opnemen in de planning.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=multiDatum;sjabloon=multiDatum/generator"),
        ["h-teLaat|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nDe wedstrijd Voorbeeld JO13-2 - Gast JO13-1 staat gepland op zaterdag 14 november 2026 om 10:30 op . Dat is over 3 dag(en).\n\nVolgens onze richtlijn moet een herplanverzoek minimaal 8 dagen voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer automatisch verwerken.\n\nNeem voor uitzonderingen rechtstreeks contact op met de coördinator.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=herplanTeLaat;sjabloon=herplanTeLaat/generator"),
        ["bv-standaard|override"] = new("Override-onderwerp bevestiging", "{aanhef} Jan, override-tekst voor bevestiging\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=standaard;sjabloon=bevestiging/database-override"),
        ["h-voorrang-verzetZonderDatum-boven-gewensteDatum|override"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nOp korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator"),
        ["b-teamOnbekend|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nWe kunnen de wedstrijd van Onbekend JO13-1 niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn? Dan kunnen we de beschikbaarheid voor je controleren.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=teamOnbekend;sjabloon=teamOnbekend/generator"),
        ["b-voorrang-datumOnbekend-boven-multiDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je bericht. We konden er geen concrete datum uit opmaken. Kun je aangeven welke datum of datums je in gedachten hebt? Dan controleren we de beschikbaarheid van onze velden voor je.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=datumOnbekend;sjabloon=datumOnbekend/generator"),
        ["h-voorrang-verzetZonderDatum-boven-gewensteDatum|standaard"] = new("Re: Oefenwedstrijd", "{aanhef} Jan,\n\nBedankt voor je verzoek om de wedstrijd Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 te verzetten.\n\nWe kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat jullie dit onderling verder kunnen afstemmen.\n\nOp korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.\n\nMet vriendelijke groet,\n\nALLSTARS Veldplanner", "tak=verzetZonderDatum;sjabloon=verzetZonderDatum/generator")
    };

    public static IEnumerable<object[]> Alle()
    {
        foreach (var g in Gevallen)
        {
            yield return new object[] { g.Naam, false };
            yield return new object[] { g.Naam, true };
        }
    }

    [Theory]
    [MemberData(nameof(Alle))]
    public async Task BouwTemplateAntwoord_LegtUitkomstPerTakVast(string naam, bool metSjabloon)
    {
        var geval = Gevallen.Single(g => g.Naam == naam);
        var sleutel = $"{naam}|{(metSjabloon ? "override" : "standaard")}";
        var classificatie = new BerichtClassificatie { Type = geval.Type, TeamNaam = "JO13-2", Tegenstander = geval.Tegenstander };
        var bericht = new InkomendBericht
        {
            Afzender = "trainer@voorbeeld.nl",
            AfzenderNaam = "Jan de Vries",
            Onderwerp = "Oefenwedstrijd",
            Body = "Kunnen we een wedstrijd spelen?"
        };
        var clubSettings = new ClubAppSettingsSnapshot("ALLSTARS Veldplanner", null, null, null, null);
        var trace = new TraceBuilder();

        var (onderwerp, ruweBody) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, geval.PlannerJson, bericht, null, clubSettings, "ALLSTARS", trace,
            k => Task.FromResult<EmailTemplate?>(metSjabloon
                ? new EmailTemplate(k, "Override-onderwerp " + k, "{{aanhef}} {{voornaam}}, override-tekst voor " + k)
                : null));

        // De aanhef hangt van het tijdstip af (GetTijdsgebondenAanhef); normaliseer die voor een stabiele vergelijking.
        var body = Aanhef.Replace(ruweBody, "{aanhef}");
        var traceSamenvatting = string.Join(";", trace.Bouw().Stappen
            .Where(s => s.Code == TraceCodes.Tak || s.Code == TraceCodes.Sjabloon)
            .Select(s => s.Code == TraceCodes.Tak
                ? $"tak={s.Uitkomst}"
                : $"sjabloon={s.Details["sjabloon"]}/{s.Details["bron"]}"));


        Golden.Should().ContainKey(sleutel);
        var verwacht = Golden[sleutel];
        onderwerp.Should().Be(verwacht.Onderwerp);
        body.Should().Be(verwacht.Body);
        traceSamenvatting.Should().Be(verwacht.Trace);
    }

    // Foutpad (#1627, Codex ronde 1 P2): de oude BouwTemplateAntwoord zette de wedstrijd-DTO bij een herplanverzoek
    // direct na het parsen om, vóór takkeuze en sjabloonophaling. Een ongeldige DTO gaf dus een
    // JsonSerializationException zonder dat het sjabloon werd opgehaald, met en zonder override.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BouwTemplateAntwoord_HerplanMetOngeldigeWedstrijd_FaaltVoorSjabloonophaling(bool metSjabloon)
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek, TeamNaam = "JO13-2" };
        var bericht = new InkomendBericht { Afzender = "trainer@voorbeeld.nl", AfzenderNaam = "Jan de Vries", Onderwerp = "Oefenwedstrijd", Body = "Verzetten?" };
        var sjabloonAanroepen = 0;

        var act = () => BerichtPipeline.BouwTemplateAntwoord(
            classificatie, "{\"wedstrijd\":\"geen-object\"}", bericht, null,
            new ClubAppSettingsSnapshot("ALLSTARS Veldplanner", null, null, null, null), "ALLSTARS", new TraceBuilder(),
            k =>
            {
                sjabloonAanroepen++;
                return Task.FromResult<EmailTemplate?>(metSjabloon ? new EmailTemplate(k, "Onderwerp", "Tekst") : null);
            });

        await act.Should().ThrowAsync<Newtonsoft.Json.JsonSerializationException>();
        sjabloonAanroepen.Should().Be(0);
    }
}
