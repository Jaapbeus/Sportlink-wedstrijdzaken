# Onderzoek: wedstrijdwijzigingen vanuit de wedstrijdzaken-app naar Sportlink Club

> Datum: 2026-09-04. Status: onderzoek, geen code. Alleen-lezen analyse van club.sportlink.com plus één door de wedstrijdsecretaris zelf uitgevoerde en teruggedraaide kleedkamerwijziging (meegelezen in netwerkverkeer).
> Bevat bewust geen persoonsgegevens, club-/accommodatie-ID's, tokens of wachtwoorden. Waar iets niet hard is vastgesteld staat **[onzeker]**.

## 1. Antwoord in één alinea

Het kan. club.sportlink.com is geen server-rendered site maar een React-SPA (Vite/rolldown-build) die alles doet via een JSON-API onder `/navajo/entity/common/clubweb/...` (Navajo, hetzelfde framework als de Sportlink Dataservice). Authenticatie loopt via standaard Keycloak (`idm.sportlink.com`, realm `sportlink`, client `sportlink-club-web`) met een `Authorization: Bearer <accessToken>`-header; er zijn geen sessie-cookies en geen CSRF-token. Alle schrijfacties die jij in de UI kunt doen zijn gewone `PUT`/`POST`-calls met een kleine JSON-body. Eén ervan (`UpdateMatchDressingRooms`) is live waargenomen en gaf `{"IsSuccess": true}`. De traagheid zit niet in de UI maar in enkele zware server-calls; gerichte calls per wedstrijd zijn snel.

## 2. Vastgestelde feiten

### 2.1 Architectuur
| Onderdeel | Bevinding | Bron |
|---|---|---|
| Frontend | React SPA, RTK Query, hoofdbundle ~3,1 MB (`/assets/main-*.js`) | netwerk + bundle |
| API-basis | `https://club.sportlink.com/navajo/entity/common/clubweb/` | `config.json` (`baseUrl`, `baseEntity`) |
| Formaat | JSON in, JSON uit. Mutaties antwoorden met `{"IsSuccess": bool}` of een entity-object; validatiefouten komen als `entityViolation.Violations` (codes zoals `INVALID_TEXT_1024`, `INVALID_UPDATE_ACTION`) | bundle |
| Extra headers | `X-Navajo-Entity`, `X-Navajo-Instance`, `X-Navajo-Locale` (app zet ze zelf; of ze verplicht zijn is **[onzeker]**) | bundle |
| Auth | `Authorization: Bearer <accessToken>` (ingelogd) of Basic auth met een publiek "clubweb-public"-account uit `config.json` (alleen voor publieke schermen) | bundle |
| Zonder token | `GET .../competition/match/Match?PublicMatchId=...` → HTTP 401 | live getest |
| Token-opslag | localStorage `SLC_OAUTH_TOKEN`; geen auth-cookies | live |
| IdP | Keycloak. Discovery-document bevestigt grant types `authorization_code`, `refresh_token`, `device_code`, `password`, `client_credentials`, `token-exchange`; PKCE `S256`; `device_authorization_endpoint` aanwezig | `idm.sportlink.com/realms/sportlink/.well-known/openid-configuration` |
| Feature flags | ConfigCat (`ccproxy.sportlink-infra.net`) | `config.json` |
| Monitoring | Sentry (session replay) en Google Analytics actief: Sportlink ziet gebruikersgedrag in de UI | sessionStorage/cookies |

### 2.2 Wedstrijd-identificatie
- `PublicMatchId` (bv. `M392686417`) is de sleutel voor alle detail- en mutatiecalls.
- `ExternalMatchId` (bv. `3403`) is het getoonde "Wedstrijdnr." in de UI.
- Onze database heeft `his.matches.wedstrijdcode` en `wedstrijdnummer` (BIGINT). **[onzeker, te verifiëren]**: `PublicMatchId` = `"M" + wedstrijdcode` en `ExternalMatchId` = `wedstrijdnummer`. Dit is één SQL-query tegen de eigen DB om te bevestigen.
- De server levert per wedstrijd permissie-flags: `IsEditFieldAllowed`, `IsAssignDressingRoomsAllowed`, `IsAssignOfficialsAllowed`, `IsEditFieldSidePanelAllowed`, `IsAddScoreAllowed`, `IsHomeMatch`, plus `TaskStatus` (bv. `MISSING_DRESSINGROOMS`). Ideaal om knoppen in onze app aan/uit te zetten.

### 2.3 Deep-links (route in de SPA)
- `/competition-affairs/match-details/{PublicMatchId}` → detailpagina van één wedstrijd. Live getest: bruikbaar na ~3 s.
- Overige routes: `/competition-affairs/match-program`, `/match-results`, `/change-requests`, `/field-occupation`, `/dressing-room-occupation`.

### 2.4 Schrijf-endpoints (uit de bundle; methode en body letterlijk uit de code)
| Actie | Endpoint (relatief aan API-basis) | Methode | Body | Status |
|---|---|---|---|---|
| Kleedkamers toewijzen | `competition/match/UpdateMatchDressingRooms` | PUT | `{PublicMatchId, HomeDressingRoomId, AwayDressingRoomId, OfficialDressingRoomId}` | **live bevestigd**, 2× `{"IsSuccess":true}` |
| Veld(deel) wijzigen | `competition/match/UpdateMatchField` | PUT | `{PublicMatchId, FieldId, FieldSize, FieldOffset, IsForceUpdate}` | uit code |
| Datum/tijd/accommodatie (= wijzigingsverzoek) | `competition/match/UpdateMatchDetails` | PUT | volledige wedstrijd-form (o.a. `PublicMatchId`, matchDate, startTime, facilityId, fieldId, `matchChangeRequestRemarks`, verzamel-/vertrektijd) **[exacte veldnamen onzeker]** | uit code |
| ↳ respons bevat | `ConfirmationNeeded.ValidationResultMessages`, `HasBlockingMessages` → tweestaps: eerst valideren, dan bevestigen | | | uit code |
| Opmerking bij wedstrijd | `competition/match/MatchRemarks` | PUT | `{PublicMatchId, Remarks}` | uit code |
| Scheidsrechters/officials | `competition/match/official/MatchOfficialsAction` | PUT | `{PublicMatchId, OfficialsToBeAssigned:[...]}`; respons `Officials[].ValidationDescription` | uit code |
| Officials zoeken | `competition/match/official/SearchMatchOfficials`, `PreferredOfficials` | GET | | uit code |
| Wijzigingsverzoek goedkeuren/afwijzen (inkomend) | `competition/match/changerequest/MatchChangeRequestAction` | PUT | `{Action: APPROVE\|DENY, PublicMatchId, PublicPersonId, PublicRequestId, Remarks}` | uit code |
| Wijzigingsverzoeken lezen | `changerequest/MatchChangeRequests`, `MatchChangeRequest?PublicRequestId=`, `MatchChangeRequestFilters` | GET | | uit code |
| Oefenwedstrijd aanmaken | `competition/match/clubmatch/ClubMatch` | POST | o.a. `MatchDate`, `Duration`, `ExternalMatchId`, teams, locatie; respons `{PublicMatchId, IsSuccess}` | uit code |
| Oefenwedstrijd verwijderen / uitslag | `clubmatch/ClubMatchDelete`, `clubmatch/ClubMatchScore` | | | uit code |
| Picklists (velden, tijden, kleedkamers) | `competition/match/picklist/PickLists?PublicMatchId=`, `facility/MatchFacilitiesList?PublicMatchId=`, `competition/match/MatchDetailsSidePanel?PublicMatchId=&TypeOfRequest=DRESSINGROOMS\|FIELDS` | GET | | live gezien |

UI-tekst op de detailpagina bevestigt de semantiek: "Het wijzigen van datum, tijd en accommodatie vereist goedkeuring door de tegenstander. Je kan hier direct een wijzigingsverzoek aanmaken." Een uitgaand wijzigingsverzoek is dus geen apart endpoint maar het gevolg van `UpdateMatchDetails` met gewijzigde datum/tijd/accommodatie **[onzeker: niet live uitgevoerd]**.

### 2.5 Waar zit de traagheid (gemeten met Performance API)
| Call | Duur |
|---|---|
| `MatchProgramOverview?DateFrom=..&DateTo=..` (4 weken) | **21,2 s** |
| `member/registrations/PersonRegistrations` (wordt op élke pagina geladen, ook wedstrijddetail) | 7,2 – 7,7 s |
| `user/dashboard/DashboardPersonChanges` | 6,0 s |
| `competition/match/Match?PublicMatchId=` | 2,2 s |
| `picklist/PickLists?PublicMatchId=` | 2,1 s |
| Overige calls (UserInfo, Club, MatchHeader, MatchFacilitiesList, mutaties) | 0,2 – 1,1 s |
| Pagina zelf (HTML+JS) | 0,15 s |

Conclusie: de UI wacht op een paar zware, voor ons irrelevante calls. Wie de API direct aanroept, betaalt alleen de 2 s van `Match` plus ~0,3 s per mutatie. De deep-link omzeilt de 21 s-overzichtscall maar niet de 7 s `PersonRegistrations`.

## 3. Drie oplossingsrichtingen, beoordeeld

### A. Deep-link/knop (laagste risico, snel te bouwen)
In `BlazorAdmin/Pages/Dagplanning.razor` (rij-rendering r. ~454-470) per wedstrijd een knop `https://club.sportlink.com/competition-affairs/match-details/{PublicMatchId}`.
- Voordeel: nul contractueel risico, geen auth-werk, 1 uur werk. Bespaart de 21 s-overzichtscall en het handmatig zoeken.
- Nadeel: nog steeds ~3-10 s laden en zelf klikken; niets wordt vanuit onze app opgeslagen. Vereist de mapping wedstrijdcode → PublicMatchId (zie 2.2).

### B. Directe API-calls met de sessie van de wedstrijdsecretaris (snelst in gebruik)
Onze backend (Azure Function) roept dezelfde `PUT`-calls aan met een Bearer-token van Jaaps Sportlink-account.
- Token verkrijgen: **[onzeker welke werkt]**
  1. Authorization-code+PKCE via de bestaande client `sportlink-club-web`: alleen als Keycloak onze redirect-URI accepteert. Waarschijnlijk niet (whitelist), niet getest.
  2. Eenmalige interactieve login (Playwright of handmatig) → `refresh_token` bewaren in Key Vault → backend vernieuwt via `token_endpoint` met `grant_type=refresh_token&client_id=sportlink-club-web`. Public client, dus geen secret nodig. Werkt zolang de Keycloak SSO-sessie niet verloopt; looptijden onbekend.
  3. `device_code`-grant: endpoint bestaat realm-breed; of de client het toestaat is onbekend.
  4. `password`-grant staat realm-breed aan; per client meestal uit. Ook ongewenst (wachtwoord opslaan).
- Voordeel: kleedkamer/veld/official wijzigen in < 1 s vanuit onze app; permissie-flags van de server bepalen wat mag; validatiefouten komen gestructureerd terug.
- Nadeel: onofficieel, kan bij elke release van Sportlink breken (bundle-hashes wijzigen al; endpoints minder vaak). Sentry/GA zien ons verkeer niet, maar de server logt het wel op Jaaps account. Raakt de gebruiksvoorwaarden van Sportlink; niet onderzocht welke clausule. Alle acties gebeuren op naam van Jaap.

### C. Browser-automation (Playwright met Jaaps sessie)
- Voordeel: gebruikt exact de UI-flow, inclusief de tweestaps-bevestiging bij `UpdateMatchDetails`.
- Nadeel: erft de traagheid (7-21 s per scherm), fragiel op DOM-wijzigingen, zwaar in Azure (headless Chromium). Alleen zinvol als B faalt op token-verkrijging; dan Playwright uitsluitend gebruiken om in te loggen en het token af te vangen, verder B.

## 4. Aanbeveling
1. **Nu**: A bouwen (knop in Dagplanning). Vooraf de mapping `PublicMatchId` ↔ `wedstrijdcode` verifiëren met één query.
2. **Daarna, als proef buiten productie**: B-variant 2. Eerst een read-only spike: token vernieuwen via `refresh_token` en `GET competition/match/Match` aanroepen vanuit een lokale console. Meet hoe lang het refresh-token geldig blijft (dagen of weken bepaalt of dit praktisch is).
3. **Eerste schrijfactie in de app**: kleedkamers (`UpdateMatchDressingRooms`), want die is live bevestigd, omkeerbaar en raakt geen tegenstander of KNVB. Daarna veld, dan officials. Datum/tijd (wijzigingsverzoek) als laatste, achter een expliciete bevestigingsdialoog met de `ValidationResultMessages` van Sportlink.
4. Guardrails: alleen wedstrijden met `IsHomeMatch=true` en de betreffende `Is...Allowed=true`; elke mutatie loggen met vóór/na-waarde; EgressGuard-patroon hergebruiken zodat lokaal nooit per ongeluk naar Sportlink wordt geschreven.

## 5. Wat ik NIET heb gedaan of weet
- Geen enkele mutatie zelf uitgevoerd; de kleedkamerwijziging is door de wedstrijdsecretaris gedaan en teruggedraaid.
- Geen token uitgelezen of opgeslagen.
- Exacte body van `UpdateMatchDetails` en `ClubMatch` niet live gezien.
- Token-/refresh-levensduur, MFA-eisen en redirect-URI-whitelist van de Keycloak-client niet getest.
- Gebruiksvoorwaarden van Sportlink niet gelezen; risico op accountblokkade bij geautomatiseerd gebruik is reëel maar niet gekwantificeerd.
- Of `X-Navajo-*`-headers verplicht zijn is niet getest.

## 6. Bronnen
- Live netwerkverkeer en Performance API in club.sportlink.com (ingelogde sessie, 2026-09-04).
- `https://club.sportlink.com/config.json` en hoofdbundle `/assets/main-*.js`.
- `https://idm.sportlink.com/realms/sportlink/.well-known/openid-configuration`.
- Repo: `docs/SPORTLINK-CLUB-SCHERMEN-ANALYSE.md`, `FunctionApp/Enitities.cs`, `BlazorAdmin/Pages/Dagplanning.razor`, `FunctionApp/Infrastructure/EgressGuard.cs`, `docs/ARCHITECTURE-PLANNER.md`.
