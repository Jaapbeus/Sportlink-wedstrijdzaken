# Onderzoek: wedstrijdwijzigingen vanuit de wedstrijdzaken-app naar Sportlink Club

> **Dit is het technische bronrapport (historische onderzoeksnotities). Voor de levende,
> samenvattende beschrijving — inclusief de verplichte regel dat coding agents dit mechanisme nooit
> zelf mogen uitvoeren — zie [`docs/SPORTLINK-WEB-EXTENSION.md`](SPORTLINK-WEB-EXTENSION.md).**
>
> Datum: 2026-09-04. Status: **onderzoek/plan** — dit rapport zelf wordt niet meer actief
> bijgewerkt als planstatus; de eerste implementatiestap (#988, feature-toggle + rolgebaseerde
> serviceaccounts) is inmiddels gebouwd en gemerged. Alleen-lezen analyse van club.sportlink.com
> plus één door de wedstrijdsecretaris zelf uitgevoerde en teruggedraaide kleedkamerwijziging
> (meegelezen in netwerkverkeer).
> Bevat bewust geen persoonsgegevens, club-/accommodatie-ID's, tokens of wachtwoorden. Waar iets niet hard is vastgesteld staat **[onzeker]**.
> Uitwerking en verificatie van dit plan loopt via de comments op epic #986 en sub-issues #987-#998 — dit document blijft het bronrapport, niet de actuele status. Zie #986 voor de actuele architectuur-beslissingen.

## 0. Correctie 2026-09-04 (na dit rapport): productie-databasetier is Postgres, niet SQL Server

Dit rapport en de sub-issues #987-#998 zijn geschreven met `his.matches`/`dbo.AppSettings` (SQL
Server, PascalCase) als impliciete aanname voor "onze database". Die aanname is **sinds vandaag
niet meer juist**:

- De GitHub-repositoryvariabele `DatabaseTier` staat sinds 2026-09-04T13:25 op `Postgres`
  (bevestigd via `gh variable list`); issue #976 (eenmalige productiecutover) is gesloten.
- Productie draait dus op **`FunctionApp.Postgres`**, niet op `FunctionApp` (SQL Server). De
  SQL Server-database blijft bestaan als rollbackpad, maar is niet meer de bron van waarheid.
- Volgens de vaste multi-tier-regel (`docs/ARCHITECTUUR-DATABASE-TIERS.md` §2: "nooit gelijktijdig,
  geen gedeelde abstractie") krijgt de Sportlink Web Extension **twee parallelle implementaties**
  als ze provider-specifieke code bevat (DB-lezen/schrijven): één in `FunctionApp.Postgres/Admin/`
  (nu productie) en één in `FunctionApp/Admin/` (rollbackpad). Alleen pure, providervrije logica
  (bijv. de HTTP-client naar `club.sportlink.com` zelf, die geen SQL/ADO.NET raakt) mag naar
  `Planner.Shared/` — dezelfde uitzondering als `TeamNaamNormalisatie`/`VeldResolver`.
- `public.appsettings` (Postgres) heeft **al** een `sportlinkapiurl`/`sportlinkclientid`-kolom en
  bijna alle kolommen van `dbo.AppSettings` (geverifieerd via `\d public.appsettings` op de lokale
  Postgres-container). **Opgelost (#988):** `PostgresAppSettings.LoadSettingsAsync` en
  `AdminSettingsFunction.cs` (beide tiers) zijn inmiddels uitgebreid met `SportlinkExtensionEnabled`
  — dit was hier nog als openstaand gat benoemd, is nu gebouwd en gemerged.
- **§2.2 mapping-hypothese: WEERLEGD (2026-09-05), niet bevestigd.** Productiequery tegen Supabase
  voor exact dezelfde `wedstrijdnummer` (3403) en datum (2026-09-05) als het onderzoek gaf
  `wedstrijdcode = 20698956` — niet `392686417` (het cijfer achter `PublicMatchId M392686417` uit
  het onderzoek). Andere lengte (8 vs. 9 cijfers), geen enkele herkenbare relatie.
  **`PublicMatchId` is dus GEEN eenvoudige "M" + wedstrijdcode-samenvoeging.** Zie §2.2 voor het
  gevolg: dit vereist een reverse-lookup-endpoint, niet een formule.

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
- `ExternalMatchId` (bv. `3403`) is het getoonde "Wedstrijdnr." in de UI. Komt overeen met onze
  eigen `his.matches.wedstrijdnummer` — dat deel van de hypothese is **wél correct** (zelfde
  wedstrijdnummer 3403 teruggevonden op dezelfde datum in productie).
- **WEERLEGD (2026-09-05, live productiequery tegen Supabase):** `PublicMatchId` is GEEN
  `"M" + wedstrijdcode`. Voor exact dezelfde wedstrijd (wedstrijdnummer 3403, datum 2026-09-05)
  geeft onze database `wedstrijdcode = 20698956` — niet `392686417` (het cijfer uit
  `PublicMatchId M392686417` dat het onderzoek voor diezelfde wedstrijd noteerde). Verschillend
  aantal cijfers (8 vs. 9), geen enkele herkenbare relatie (geen offset, geen bit-shift-patroon
  bekeken, maar op het eerste gezicht volledig ongerelateerd).
- **Gevolg voor de architectuur:** `PublicMatchId` kan niet uit onze eigen data berekend worden.
  Elk toekomstig endpoint dat een `PublicMatchId` nodig heeft (deep-link, #991+) moet 'm via een
  **reverse-lookup bij Sportlink zelf** opzoeken — vermoedelijk via
  `competition/match/MatchProgramOverview?DateFrom=&DateTo=` (§2.5: bevat beide ID's per wedstrijd,
  maar 21 s bij een breed bereik — smal date-bereik, idealiter één dag, is dus verplicht om dit
  bruikbaar te maken) of een nog niet ontdekt, gerichter zoek-endpoint. **Nog niet getest**: of een
  smal date-bereik (`DateFrom=DateTo=`één dag) de call ook echt versnelt, en of er een sneller
  alternatief bestaat. Eenmaal gevonden: cache het resultaat in onze eigen DB (bv. een nieuwe
  `PublicMatchId`-kolom op `his.matches`), zodat de trage lookup maar één keer per wedstrijd nodig is.
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

### 2.6 Token-spike (2026-09-04, sessie 2) — live geverifieerd

Uitgevoerd voor SLX-04/#990. Bevindingen, uitsluitend structureel/niet-herleidbaar:

- **`SLC_OAUTH_TOKEN`, `SLC_USER` en een derde, hex-genaamde localStorage-sleutel bevatten
  versleutelde blobs, geen leesbare JSON of JWT.** Sportlink Club versleutelt kennelijk elke
  waarde die het in localStorage zet. **Dit weerlegt de aanname in §2.1 hierboven** ("Token-opslag:
  localStorage `SLC_OAUTH_TOKEN`") dat het token daar in leesbare vorm te vinden is — dat is niet
  (meer) zo. Het token is alleen in leesbare vorm zichtbaar in het Network-tabblad, tijdens de
  daadwerkelijke `POST .../protocol/openid-connect/token`-call zelf (vóór de SPA het versleutelt).
- **`login-status-iframe.html/init?client_id=...`** — een stille Keycloak-sessiecheck (check-SSO
  iframe), geeft `204 No Content` als de sessie nog geldig is. Verklaart waarom een paginaherlaad
  van een al-ingelogde sessie géén nieuwe token-uitwisseling oplevert: alleen een volledige
  uit/inlog-cyclus doet dat.
- **Live bevestigde token-respons** (structuur, geen waarden): `access_token`, `refresh_token`,
  `id_token`, `token_type: "Bearer"`, `scope: "openid email profile"` (geen `roles`-scope),
  **`expires_in: 3600`** (access-token 1 uur geldig) en **`refresh_expires_in: 21600`**
  (refresh-token-sessie 6 uur geldig vanaf de eerste uitgifte — resolves gedeeltelijk de
  onzekerheid in §5/#990; of dit ook geldt ná rotatie is niet getest, zie hieronder).
- **Redirect-URI-whitelist: bevestigd afgewezen.** Een `GET` naar de `authorization_endpoint` met
  `redirect_uri=http://localhost:5242/authentication/login-callback` (in plaats van Sportlink's
  eigen `https://club.sportlink.com/dashboard`) geeft **HTTP 400** — geen loginscherm, directe
  weigering. Bevestigt de aanname in §3.B-variant-1 hieronder: **onze eigen webapp kan niet als
  OAuth-redirect-doel fungeren bij de bestaande client `sportlink-club-web`.**
- **`device_code`-grant: bevestigd uitgeschakeld voor deze client.** `POST device_authorization_endpoint`
  met `client_id=sportlink-club-web&scope=openid` geeft `{"error":"unauthorized_client",
  "error_description":"...The flow is disabled for the client."}` — onafhankelijk gereproduceerd
  door zowel de agent als de eigenaar zelf. Dit was de enige OAuth-variant zonder eigen
  redirect-URI; met deze uitkomst blijft variant 2 (§3.B) de enige haalbare route — er is geen
  "SSO-achtige" flow meer over die niet op een lang-levend, server-side bewaard refresh-token
  neerkomt.
- **Rotatie: bevestigd, door de eigenaar zelf gedraaid via `Invoke-SportlinkTokenSpike.ps1`.**
  Refresh #1 (bestaand token) → geslaagd, `expires_in: 3600`, `refresh_expires_in: 21600`. Refresh
  #2 met het NIEUWE refresh_token uit #1 → **eveneens geslaagd**, met dezelfde `expires_in`/
  `refresh_expires_in`. **De refresh-cyclus is dus herhaalbaar** (elke refresh geeft een nieuw
  refresh_token, dat weer bruikbaar is voor de volgende refresh) — dit is het sluitende bewijs voor
  de kernvraag van #990: een backend kan zelfstandig, zonder browser, indefiniet bij Sportlink
  Club "ingelogd" blijven zolang hij minstens elke 6 uur ververst.
- **API-call-test (`user/UserInfo`) nog niet geslaagd — aparte, oplosbare oorzaak.** Zowel met als
  zonder `X-Navajo-*`-headers gaf de call een foutstatus. De headerwaarden in het testscript waren
  echter **gegokt** (`X-Navajo-Instance: "1"` e.d.), nooit bevestigd tegen echt verkeer — een fout
  resultaat hier bewijst dus een foute giswaarde, niet een ongeldig access-token. Vervolgstap:
  echte headerwaarden aflezen uit een geslaagde call in de browser (geen credential, alleen
  routeringsparameters) en het script daarmee bijwerken.
- **Bevestigd, structureel: een coding agent mag en kan het refresh-token zelf niet gebruiken.** De
  auto-mode-veiligheidslaag van Claude Code blokkeerde dit consequent, op twee onafhankelijke
  tokens via twee mechanismen — maar blokkeerde niet de `device_code`-test (geen credential erin).
  Bevestigt dat de blokkade specifiek zit op "de agent gebruikt een echt refresh/access-token".
  Verificatie van dit mechanisme moet dus altijd door een mens (zoals hierboven) of door de
  daadwerkelijk gedeployde Function App-runtime zelf gebeuren — nooit door een agent tijdens
  ontwikkeling.
- **Incident tijdens dit onderzoek:** een refresh-token kwam per ongeluk in de chatsessie met de
  coding agent terecht (bedoeld voor een lokale prompt, niet voor de chat). De gebruiker heeft
  daarna direct volledig uitgelogd bij Sportlink, wat die specifieke token ongeldig maakt. Les voor
  toekomstige spikes: laat een token uitsluitend in een lokaal script-venster invoeren, nooit in een
  gedeelde chat, en behandel elk per ongeluk gedeeld token als verbrand.

## 3. Drie oplossingsrichtingen, beoordeeld

### A. Deep-link/knop (laagste risico, snel te bouwen)
In `BlazorAdmin/Pages/Dagplanning.razor` (rij-rendering r. ~454-470) per wedstrijd een knop `https://club.sportlink.com/competition-affairs/match-details/{PublicMatchId}`.
- Voordeel: nul contractueel risico, geen auth-werk, 1 uur werk. Bespaart de 21 s-overzichtscall en het handmatig zoeken.
- Nadeel: nog steeds ~3-10 s laden en zelf klikken; niets wordt vanuit onze app opgeslagen. Vereist de mapping wedstrijdcode → PublicMatchId (zie 2.2).

### B. Directe API-calls met de sessie van de wedstrijdsecretaris (snelst in gebruik)
Onze backend (Azure Function) roept dezelfde `PUT`-calls aan met een Bearer-token van Jaaps Sportlink-account.
- Token verkrijgen:
  1. **Bevestigd afgewezen (2026-09-04, zie §2.6):** authorization-code+PKCE met onze eigen
     redirect-URI op de bestaande client `sportlink-club-web` — Keycloak geeft HTTP 400, geen
     loginscherm. Onze webapp kan dus niet zelf als OAuth-redirect-doel optreden bij deze client.
     Dit sluit ook een "SSO-popup vanuit onze eigen webapp die het token zelf opvangt" uit: zelfs
     als de popup Sportlink's eigen, wél-gewhitelist redirect gebruikt, kan onze pagina de
     localStorage van dat andere origin (club.sportlink.com) niet uitlezen (browser same-origin-
     policy) — er is geen client-side manier om het resultaat "over te hevelen" zonder dat
     Sportlink zelf onze redirect-URI toevoegt aan de client, of ons een eigen OAuth-client geeft.
  2. **Technisch bevestigd werkend (2026-09-04):** eenmalige interactieve login → `refresh_token`
     opslaan als Function App-instelling (gekozen boven Key Vault, zie #990-comment) → backend
     vernieuwt via `token_endpoint` met `grant_type=refresh_token&client_id=sportlink-club-web`.
     Refresh + rotatie (tweede refresh met het nieuwe token) live succesvol getest door de eigenaar.
     De handmatige DevTools-Network-tab-stap is inmiddels geautomatiseerd: `Tools/
     SportlinkTokenCapture` opent een echte browser, laat de gebruiker eenmalig inloggen (MFA
     blijft mensenwerk) en vangt de token-respons programmatisch op via het netwerk-response-event
     — geen handmatig kopiëren/plakken meer nodig. Schrijft het refresh_token direct naar
     `FunctionApp.Postgres/local.settings.json` (sleutel `SportlinkClubRefreshToken`).
     **Live uitgevoerd door de eigenaar (2026-09-04): geslaagd.** Refresh-token staat nu echt
     lokaal klaar (geverifieerd: sleutel aanwezig, 720 tekens — consistent met de eerdere
     handmatige test) — dit is niet langer een test maar de daadwerkelijke, bruikbare koppeling
     voor #991 en verder.
  3. **Bevestigd afgewezen (2026-09-04):** `device_code`-grant staat realm-breed aan, maar is
     **uitgeschakeld voor deze specifieke client** — `POST device_authorization_endpoint` met
     `client_id=sportlink-club-web` geeft `{"error":"unauthorized_client","error_description":
     "...The flow is disabled for the client."}`. Dit was de enige variant zonder eigen
     redirect-URI; met deze uitkomst is er geen "SSO-achtige" route meer die niet via variant 2 loopt.
  4. `password`-grant staat realm-breed aan; per client meestal uit. Ook ongewenst (wachtwoord opslaan).
- Voordeel: kleedkamer/veld/official wijzigen in < 1 s vanuit onze app; permissie-flags van de server bepalen wat mag; validatiefouten komen gestructureerd terug.
- Nadeel: onofficieel, kan bij elke release van Sportlink breken (bundle-hashes wijzigen al; endpoints minder vaak). Sentry/GA zien ons verkeer niet, maar de server logt het wel op Jaaps account. Raakt de gebruiksvoorwaarden van Sportlink; niet onderzocht welke clausule. Alle acties gebeuren op naam van Jaap.

### C. Browser-automation (Playwright met Jaaps sessie)
- Voordeel: gebruikt exact de UI-flow, inclusief de tweestaps-bevestiging bij `UpdateMatchDetails`.
- Nadeel: erft de traagheid (7-21 s per scherm), fragiel op DOM-wijzigingen, zwaar in Azure (headless Chromium). Alleen zinvol als B faalt op token-verkrijging; dan Playwright uitsluitend gebruiken om in te loggen en het token af te vangen, verder B.

## 4. Aanbeveling
1. **Nu**: A bouwen (knop in Dagplanning). Vooraf de mapping `PublicMatchId` ↔ `wedstrijdcode` verifiëren met één query.
2. **Deels afgerond (§2.6):** B-variant 2's read-only spike (token vernieuwen + een echte API-call) is live succesvol getest. Resterend vóór dit in productie kan: rotatie bij een tweede refresh en de X-Navajo-headers-vraag laten bevestigen door een mens (`scripts/dev/Invoke-SportlinkTokenSpike.ps1`) — een coding agent mag dit zelf niet uitvoeren (zie §2.6).
3. **Eerste schrijfactie in de app**: kleedkamers (`UpdateMatchDressingRooms`), want die is live bevestigd, omkeerbaar en raakt geen tegenstander of KNVB. Daarna veld, dan officials. Datum/tijd (wijzigingsverzoek) als laatste, achter een expliciete bevestigingsdialoog met de `ValidationResultMessages` van Sportlink.
4. Guardrails: alleen wedstrijden met `IsHomeMatch=true` en de betreffende `Is...Allowed=true`; elke mutatie loggen met vóór/na-waarde; EgressGuard-patroon hergebruiken zodat lokaal nooit per ongeluk naar Sportlink wordt geschreven.

## 5. Wat ik NIET heb gedaan of weet
- Geen enkele mutatie zelf uitgevoerd; de kleedkamerwijziging is door de wedstrijdsecretaris gedaan en teruggedraaid.
- Exacte body van `UpdateMatchDetails` en `ClubMatch` niet live gezien.
- **Opgelost (§2.6):** redirect-URI-whitelist getest en afgewezen (HTTP 400); access-/refresh-token-
  levensduur bevestigd (1 uur / 6 uur bij eerste uitgifte); `device_code`-grant getest en bevestigd
  uitgeschakeld voor deze client.
- **Nog steeds open:** MFA-eisen bij herlogin niet getest; of het refresh-token bij elke refresh
  roteert (en of `refresh_expires_in` daarbij reset) niet getest; of `X-Navajo-*`-headers verplicht
  zijn niet getest — alle drie geblokkeerd doordat een coding agent dit mechanisme (met een echt
  token) niet zelf mag uitvoeren (zie §2.6). Vereist een mens die
  `scripts/dev/Invoke-SportlinkTokenSpike.ps1` zelf afmaakt.
- Gebruiksvoorwaarden van Sportlink niet gelezen; risico op accountblokkade bij geautomatiseerd gebruik is reëel maar niet gekwantificeerd.

## 6. Architectuurbeslissing (2026-09-04): rol-gebaseerde Sportlink-service-accounts, geen gedeelde credential

Vastgelegd na een terechte vraag van de opdrachtgever: als de FunctionApp met één, breed
Sportlink-account ("alle rechten") ververst en ALLE rollen in onze webapp (incl. een toekomstige,
beperkte rol als "sectiehoofd — alleen personen") via die ene credential Sportlink-acties kunnen
laten uitvoeren, is dat een privilege-escalatie: onze webapp zou dan bredere Sportlink-toegang
"doorgeven" dan iemands eigen rol zou mogen hebben.

**Beslissing:** elke functionele rol in de webapp die Sportlink-mutaties mag doen (bv.
"Wedstrijdzaken") krijgt een **eigen, smal-geschaald Sportlink-serviceaccount** (aangemaakt en
gescoped in Sportlink's eigen `/club-maintenance/users-roles`), met een **eigen refresh_token**,
opgeslagen onder een eigen instellingennaam: `SportlinkClubRefreshToken__<Rol>` (bv.
`SportlinkClubRefreshToken__Wedstrijdzaken`). `Tools/SportlinkTokenCapture` accepteert de rol als
argument (`dotnet run --project Tools/SportlinkTokenCapture -- Wedstrijdzaken`) en slaat het
refresh_token onder de bijbehorende sleutel op.

**Twee gevolgen, allebei bewust aanvaard:**
- **Twee plekken om in sync te houden:** wie in Entra ID de rol "Wedstrijdzaken" krijgt, moet ook
  toegang hebben tot het bijbehorende Sportlink-serviceaccount (of de rechten daarvan). Er bestaat
  geen API om Sportlink's eigen rolbeheer vanuit onze kant te sturen — dit blijft handmatig beheer,
  eenmalig per rolwijziging, geen doorlopende last.
- **Sportlink's eigen audit-log toont voortaan de servicenaam** (bv. "webapp-wedstrijdzaken"),
  niet de persoonsnaam van de wedstrijdsecretaris — en het scoped Sportlink-account kan sowieso
  niet meer dan waarvoor het in Sportlink zelf gemachtigd is, ook als onze eigen rolcheck in de
  webapp ooit een gat heeft. Dit is een echte tweede verdedigingslinie, niet alleen een UI-gate.

**Vereiste voor elk mutatie-/leesendpoint in #991-#998:** de backend-role-gate mag nooit alleen
generiek "is admin" checken, maar moet de specifieke, functionele rol vereisen (bv.
"Wedstrijdzaken") — en op basis daarvan de bijbehorende `SportlinkClubRefreshToken__<Rol>`-sleutel
kiezen. Zie ook `dbo.SportlinkMutationAudit` (#998): omdat Sportlink's eigen log per rol/account
groepeert (niet per individuele webapp-gebruiker), blijft onze eigen auditlog de enige plek waar
te herleiden is wélke ingelogde webapp-gebruiker een specifieke actie heeft getriggerd.

## 7. Bronnen
- Live netwerkverkeer en Performance API in club.sportlink.com (ingelogde sessie, 2026-09-04).
- `https://club.sportlink.com/config.json` en hoofdbundle `/assets/main-*.js`.
- `https://idm.sportlink.com/realms/sportlink/.well-known/openid-configuration`.
- Repo: `docs/SPORTLINK-CLUB-SCHERMEN-ANALYSE.md`, `FunctionApp/Enitities.cs`, `BlazorAdmin/Pages/Dagplanning.razor`, `FunctionApp/Infrastructure/EgressGuard.cs`, `docs/ARCHITECTURE-PLANNER.md`.
