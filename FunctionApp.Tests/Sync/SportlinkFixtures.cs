namespace FunctionApp.Tests.Sync;

/// <summary>
/// Opgenomen Sportlink-API-antwoorden in het echte gegevensformaat (#867) — veldnamen en -vormen
/// (datumformaten, query-opbouw) zijn overgenomen uit FunctionApp/CLAUDE.md's "Sportlink API
/// Reference" (live gevalideerd tegen <c>https://data.sportlink.com</c>), niet bedacht. Bewust géén
/// demodata-vorm: de demodataseed (<c>Script.PostDeployment1.sql</c>) schrijft datums in een ander
/// formaat dan de echte bron levert, en juist datuminterpretatie is een bekend verschil tussen
/// database-engines (#867-issuetekst) — een test die alleen tegen demodata draait, mist dat.
/// </summary>
public static class SportlinkFixtures
{
    public static SportlinkFixtureServer BuildServer(long wedstrijdcode, string clubCode)
    {
        var server = new SportlinkFixtureServer();

        server.RespondWithJson("/teams", $$"""
            [
              {
                "teamcode": 90001,
                "lokaleteamcode": 1,
                "poulecode": 7,
                "teamnaam": "{{clubCode}} JO13-1",
                "competitienaam": "0214 JO13 Zaterdag",
                "klasse": "3e klasse",
                "poule": "03 (C)",
                "klassepoule": "3e klasse 03 (C)",
                "spelsoort": "veld",
                "competitiesoort": "regulier",
                "geslacht": "M",
                "teamsoort": "veld",
                "leeftijdscategorie": "Onder 13",
                "kalespelsoort": "veld",
                "speeldag": "zaterdag",
                "speeldagteam": "{{clubCode}} JO13-1",
                "more": ""
              }
            ]
            """);

        server.RespondWithJson("/programma", $$"""
            [
              {
                "wedstrijddatum": "2026-09-05T10:00:00+0200",
                "wedstrijdcode": {{wedstrijdcode}},
                "wedstrijdnummer": 19780,
                "teamnaam": "{{clubCode}} JO13-1",
                "thuisteamclubrelatiecode": "BBBZXXXX",
                "uitteamclubrelatiecode": "BBBZYYYY",
                "thuisteamid": 99007,
                "thuisteam": "{{clubCode}} JO13-1",
                "thuisteamlogo": "https://binaries.sportlink.com/logo-thuis.png",
                "uitteamid": 222309,
                "uitteam": "Tegenstander JO13-1",
                "uitteamlogo": "https://binaries.sportlink.com/logo-uit.png",
                "teamvolgorde": 1,
                "competitiesoort": "regulier",
                "competitie": "0214 JO13 Zaterdag",
                "klasse": "3e klasse",
                "poule": "03 (C)",
                "klassepoule": "3e klasse 03 (C)",
                "kaledatum": "2026-09-05 00:00:00.00",
                "datum": "05 sep.",
                "vertrektijd": "08:35",
                "verzameltijd": "09:30",
                "aanvangstijd": "10:00",
                "wedstrijd": "{{clubCode}} JO13-1 - Tegenstander JO13-1",
                "status": "Te spelen",
                "scheidsrechters": "A. (Arie) Jansen (Scheidsrechter)",
                "scheidsrechter": "A. (Arie) Jansen",
                "accommodatie": "Sportpark Oost",
                "veld": "veld 3",
                "locatie": "Veld",
                "plaats": "TESTSTAD",
                "rijders": null,
                "kleedkamerthuisteam": "1",
                "kleedkameruitteam": "2",
                "kleedkamerscheidsrechter": "",
                "meer": "wedstrijd-informatie?wedstrijdcode={{wedstrijdcode}}"
              }
            ]
            """);

        // /uitslagen verrijkt de bestaande /programma-rij alleen met scorevelden — zelfde
        // wedstrijdcode, geen nieuwe wedstrijd (zie FunctionApp/CLAUDE.md, "Sync strategie").
        server.RespondWithJson("/uitslagen", $$"""
            [
              {
                "wedstrijddatum": "2026-09-05T10:00:00+0200",
                "wedstrijdcode": {{wedstrijdcode}},
                "wedstrijdnummer": 19780,
                "datum": "05 sep.",
                "wedstrijd": "{{clubCode}} JO13-1 - Tegenstander JO13-1",
                "accommodatie": "Sportpark Oost",
                "aanvangstijd": "10:00",
                "thuisteam": "{{clubCode}} JO13-1",
                "thuisteamid": 99007,
                "thuisteamlogo": "https://binaries.sportlink.com/logo-thuis.png",
                "thuisteamclubrelatiecode": "BBBZXXXX",
                "uitteamclubrelatiecode": "BBBZYYYY",
                "uitteam": "Tegenstander JO13-1",
                "uitteamid": 222309,
                "uitteamlogo": "https://binaries.sportlink.com/logo-uit.png",
                "competitiesoort": "regulier",
                "status": "Gespeeld",
                "meer": "wedstrijd-informatie?wedstrijdcode={{wedstrijdcode}}",
                "datumopgemaakt": "05 sep. 2026",
                "uitslag": "3-1",
                "uitslag-regulier": "3-1",
                "uitslag-nv": "",
                "uitslag-s": "",
                "competitienaam": "0214 JO13 Zaterdag",
                "eigenteam": "thuis",
                "sportomschrijving": "Voetbal",
                "verenigingswedstrijd": "Ja"
              }
            ]
            """);

        server.RespondWithJson("/wedstrijd-informatie", $$"""
            {
              "wedstrijdinformatie": {
                "wedstrijdnummer": {{wedstrijdcode}},
                "wedstijdnummerintern": 19780,
                "veldnaam": "veld 3",
                "veldlocatie": "Veld",
                "vertrektijd": "08:35",
                "rijder": "",
                "thuisscore": "3",
                "thuisscoreRegulier": "3",
                "thuisscoreNv": "",
                "thuisscoreS": "",
                "uitscore": "1",
                "uitscoreRegulier": "1",
                "uitscoreNv": "",
                "uitscoreS": "",
                "klasse": "3e klasse",
                "wedstrijdtype": "Competitie",
                "competitietype": "regulier",
                "categorie": "JO13",
                "wedstrijddatetime": "2026-09-05T10:00:00",
                "wedstrijddatum": "2026-09-05T00:00:00",
                "wedstrijddatumopgemaakt": "05 sep. 2026",
                "aanvangstijd": "10:00",
                "aanvangstijdopgemaakt": "10:00 uur",
                "duur": 50,
                "speltype": "veld",
                "aanduiding": "",
                "poulecode": "7",
                "poule": "03 (C)",
                "thuisteamid": 99007,
                "thuisteam": "{{clubCode}} JO13-1",
                "uitteamid": 222309,
                "uitteam": "Tegenstander JO13-1",
                "opmerkingen": ""
              },
              "officials": {
                "verenigingsscheidsrechtercode": "",
                "verenigingsscheidsrechter": "",
                "overigeofficialcode": "SR001",
                "overigeofficial": "A. (Arie) Jansen"
              },
              "matchofficials": {
                "scheidsrechters": "A. (Arie) Jansen (Scheidsrechter)"
              },
              "kleedkamers": {
                "thuis": "1",
                "uit": "2",
                "official": "3"
              },
              "accommodatie": {
                "naam": "Sportpark Oost",
                "straat": "Sportlaan 1",
                "plaats": "TESTSTAD",
                "telefoon": "0000000000",
                "routeplanner": "https://maps.example/sportpark-oost"
              },
              "thuisteam": {
                "naam": "{{clubCode}} JO13-1",
                "code": "90001",
                "website": "https://example.test",
                "shirtkleur": "Rood",
                "straat": "Sportlaan 1",
                "postcodeplaats": "0000 AA TESTSTAD",
                "telefoon": "0000000000",
                "email": "info@voorbeeld.nl"
              },
              "uitteam": {
                "naam": "Tegenstander JO13-1",
                "code": "222309",
                "website": "https://tegenstander.test",
                "shirtkleur": "Blauw",
                "straat": "Voorbeeldstraat 2",
                "postcodeplaats": "1111 BB ANDERESTAD",
                "telefoon": "0000000000",
                "email": "trainer@voorbeeld.nl"
              }
            }
            """);

        return server;
    }
}
