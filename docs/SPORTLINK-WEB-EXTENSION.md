# Sportlink Web Extension

> **Status: gedeeltelijk gebouwd. Het read-only Match-endpoint (#991) is 2026-09-06 lokaal live
> geverifieerd tegen een echte testwedstrijd** (zie §4.4/#1036/#1038 voor de daarbij gevonden en
> gefixte bugs: `ExternalMatchId` kwam als JSON-getal terug in plaats van string (#1036), en
> `MatchDate` kwam genest terug (`{Date, StartTime, DateTime}`) in plaats van als losse ISO-string
> (#1038)). **Kleedkamers toewijzen (#992) is 2026-09-06 live bevestigd te werken** — na een
> afwijzing (`INVALID_COMBINATION_FACILITY_DRESSINGROOM`, #1040) leverde een netwerktrace door de
> eigenaar de echte identifiervorm: `{FacilityId}-DRESSINGROOM-{n}` (bijv.
> `"BBCF989-DRESSINGROOM-11"`), niet een los kleedkamernummer (zie #1045). Met die fix slaagde de
> mutatie echt (`{"isSuccess":true}`, bevestigd in het audit-log en een verse GET). **Veld wijzigen
> (#993) is 2026-09-06 live bevestigd te werken, volledig end-to-end.** Dezelfde netwerktrace toonde
> dat Sportlinks eigen UI niet `UpdateMatchField` aanroept (wat deze app eerst implementeerde, HTTP
> 602 "no valid entity key found") maar `UpdateMatchDetails` — een endpoint dat het VOLLEDIGE
> wedstrijdrecord verwacht, opgebouwd door een verse Match-GET-snapshot terug te sturen met alléén
> het gewijzigde veld overschreven (Sportlinks eigen UI-patroon, #1047). Onderweg bleek ook
> `PublicApplicantId` (aanvankelijk via `UserInfo` opgehaald) leeg mee te mogen voor een
> eigen-veld-wijziging — live bevestigd geaccepteerd (#1048), dus geen aparte, kwetsbare
> `UserInfo`-aanroep nodig voor dit pad. **`UserInfo` zelf is nog steeds stuk** (`HTTP 602`, #1048
> blijft open) en blokkeert alleen nog #996's actie-pad, dat wél een echte aanvrager-identiteit
> nodig heeft. **Inkomende wijzigingsverzoeken ophalen (#996, GET) is 2026-09-06 live bevestigd te
> werken** — toont echte, actuele verzoeken van tegenstanders. De actie (goedkeuren/afwijzen) is
> bewust NIET live getest en blijft geblokkeerd op #1048's `UserInfo`-bug. #994/#995/#997 zijn
> bewust nog niet gebouwd: de exacte requestvorm is niet live vastgesteld (zie de betreffende
> issues). Epic
> [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986). Dit document is de
> canonieke, levende beschrijving — bij twijfel of tegenspraak met een ouder issue-comment geldt
> dit document. Het bronrapport met alle live-geteste technische details staat in
> [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md).

## 1. Wat dit is

Een optionele uitbreiding die wedstrijdwijzigingen (kleedkamers, veld, scheidsrechters,
wijzigingsverzoeken) rechtstreeks vanuit deze webapp terugschrijft naar club.sportlink.com — in
plaats van dat de wedstrijdsecretaris dat apart, handmatig in Sportlink Club moet doen. Het is een
**onofficiële integratie**: Sportlink biedt hier geen publieke API voor, dit reverse-engineert de
JSON-API die hun eigen React-SPA gebruikt. Staat daarom standaard **UIT** per club
(`SportlinkExtensionEnabled` in Instellingen) en kan bij een Sportlink-release breken.

## 2. Voor gebruikers (wedstrijdsecretaris)

- Dit verandert vandaag nog niets aan hoe je werkt — de extension staat standaard uit, en zelfs
  wanneer een club hem aanzet, gebeurt er niets zonder dat jij op een knop klikt.
- Als je club de extension gebruikt: je krijgt een eigen, apart Sportlink-account voor deze
  koppeling (niet je eigen persoonlijke account) — een beheerder regelt dat samen met jou, zie §3.
- Je hoeft dat account maar **één keer** te koppelen (niet elke dag, niet elke week) — de koppeling
  blijft daarna zelfstandig geldig.
- Alles wat de extension straks doet, doet zij op naam van dat aparte account — niet op jouw eigen
  naam — dus in Sportlink's eigen logs zie je dat terug als bijvoorbeeld "webapp-wedstrijdzaken".
- Wat vandaag al werkt: bij elke wedstrijd in Dagplanning staat een knop "Open in Sportlink" die de
  juiste wedstrijd direct in Sportlink Club opent (nieuw tabblad) — scheelt het zoeken in het trage
  overzichtsscherm. Je klikt daar zelf nog op opslaan; deze knop wijzigt zelf niets (#989).

## 3. Voor beheerders

### 3.1 Inschakelen
Instellingen → sectie "Sportlink Web Extension" → schakelaar aan. Direct daaronder staat een tabel
met alle functionele rollen (nu: "Wedstrijdzaken") en of daar al een Sportlink-serviceaccount aan
gekoppeld is.

### 3.1a Dry-run — standaard AAN, bewust een tweede schakelaar (#998)
Naast de aan/uit-schakelaar staat een tweede schakelaar: "Dry-run: alles simuleren, niets naar
Sportlink schrijven". Deze staat **standaard AAN**, ook voor een club die de extension zelf al
aanzet — een kleedkamer-/veldwijziging of wijzigingsverzoek-actie wordt dan wél volledig doorlopen
(token-refresh, guard-check, audit-logging), maar de daadwerkelijke aanroep naar Sportlink wordt
overgeslagen. Het audit-resultaat toont in dat geval `DryRun` in plaats van `Success`/`Failure`, en
de Admin GUI toont een informatieve melding ("Dry-run: niets gewijzigd in Sportlink Club — de
aanroep is gesimuleerd en gelogd") in plaats van een succes-/foutmelding.

Zet dry-run pas uit nadat je:
1. de rol-koppeling (§3.3) hebt gecontroleerd,
2. de statussectie (§3.1b) groen ziet staan,
3. een paar dry-run-pogingen in het audit-log hebt teruggezien met de verwachte `WaardeVoor`/`WaardeNa`.

**Uitzondering, geen keuze:** op de SQL Server-tier (rollback-only sinds de Postgres-cutover, zie
§4.3) staat dry-run onvoorwaardelijk hard aan in code — die tier heeft nooit een mutatie-endpoint
gehad en mag dat ook nooit stilzwijgend krijgen via een instelling.

### 3.1b Statussectie — wat er te zien is
Onder de rollen-tabel op Instellingen staat sinds #998 een statussectie die in één oogopslag toont:
of de extension/dry-run aan staat, of uitgaande integraties zijn toegestaan (EgressGuard, #857), de
koppelingsstatus + laatste tokenverversing per rol, de laatste mutatiefout uit het audit-log, en de
uitkomst van de laatste dagelijkse contract-check (§4.2). Dit komt allemaal uit onze eigen database
— er gaat geen Sportlink-aanroep uit tenzij je zelf op "Nu live controleren" klikt, wat één echte
tokenverversing en één leesaanroep doet (nooit automatisch, nooit door een agent — zie §4.4).

### 3.2 Waarom een apart account per rol, niet het account van de wedstrijdsecretaris zelf
Als alle rollen via één, breed Sportlink-account zouden lopen, zou een toekomstige, beperktere
webapp-rol (bijvoorbeeld een sectiehoofd dat alleen ledengegevens mag zien) via de extension alsnog
wedstrijdzaken-acties in Sportlink kunnen triggeren — bredere toegang dan zijn eigen rol toestaat.
Daarom krijgt elke rol die Sportlink-mutaties mag doen een **eigen, smal-geschaald**
Sportlink-account, aangemaakt en gescoped in Sportlink's eigen
`club.sportlink.com/club-maintenance/users-roles`. Volledige onderbouwing:
[onderzoeksrapport §6](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md#6-architectuurbeslissing-2026-09-04-rol-gebaseerde-sportlink-service-accounts-geen-gedeelde-credential).

### 3.3 Een rol koppelen (eenmalige, menselijke handeling)
1. Maak in Sportlink Club zelf het serviceaccount aan voor deze rol (bv. `webapp-wedstrijdzaken`),
   met alleen de rechten die deze rol nodig heeft — niet "alle rechten".
2. Draai lokaal, als mens, **niet als agent** (zie §4.4 voor waarom dat een harde grens is):
   ```
   dotnet run --project Tools/SportlinkTokenCapture -- Wedstrijdzaken
   ```
3. Log in het geopende browservenster in met het zojuist aangemaakte serviceaccount. Het
   script schrijft het refresh_token lokaal weg — een echte, productie-persistente koppeling
   vereist stap 5 hieronder.
4. Klik in Instellingen op "Koppeling (opnieuw) registreren" en vul de accountnaam in ter
   herkenning — dit is geen live verificatie, puur een leesbaar label voor de statustabel.
5. Vul in datzelfde dialoogvenster het veld "Refresh-token registreren" in met de waarde uit
   stap 3 (#991). Dit valideert het token met één refresh-poging en slaat het rotarende
   refresh_token productie-persistent op in `public.sportlinkservicetokens` — write-only, nooit
   ergens teruggetoond.
6. Herhaal deze koppeling alleen als Sportlink de onderliggende sessie ooit volledig intrekt
   (zeldzaam) — niet routinematig.

### 3.4 Entra-rol "Wedstrijdzaken"
Naast de bestaande `admin`/`user`-rollen bestaat er een aanvullende approl `Wedstrijdzaken`
(toegevoegd via `scripts/azure/Configure-EntraApp.ps1`) — een gebruiker heeft dus bijvoorbeeld
`["admin","Wedstrijdzaken"]`. Deze rol vervangt `admin`/`user` niet en geeft op zichzelf geen
toegang tot de Admin GUI; ze wordt gebruikt om specifieke Sportlink-mutatie-acties (vanaf #991) te
gaten, bovenop de bestaande admin-toegang. Zie
[`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) voor het volledige rolbeheer-protocol en de
verplichte N-user-test.

## 4. Voor developers

### 4.1 Architectuur in het kort
- club.sportlink.com is een React-SPA op een JSON-API (`/navajo/entity/common/clubweb/...`).
  Authenticatie via Keycloak (`idm.sportlink.com`, realm `sportlink`, client `sportlink-club-web`),
  standaard OAuth2 authorization_code+PKCE, `Bearer`-token, geen cookies.
- Onze backend gebruikt uitsluitend de **refresh_token-grant**: eenmalig een refresh_token
  vastleggen (via `Tools/SportlinkTokenCapture`, §3.3), daarna zelfstandig verversen
  (`grant_type=refresh_token`). Rotatie is bevestigd: elke refresh geeft een nieuw refresh_token,
  bruikbaar voor de volgende refresh — dus in theorie oneindig, zolang er minstens elke
  `refresh_expires_in` (6 uur bij eerste uitgifte) ververst wordt.
- Twee routes zonder eigen redirect-URI zijn **bevestigd gesloten**, geen toekomstig herstel
  hierop proberen: eigen redirect_uri → HTTP 400 (client whitelist); `device_code`-grant →
  `unauthorized_client` (uitgeschakeld voor deze client). Zie onderzoeksrapport §2.6/§3.B.
- API-calls vereisen `X-Navajo-Entity` (= het aangeroepen pad, geen vaste appnaam),
  `X-Navajo-Instance` (vaste waarde `KNVB`), `X-Navajo-Locale` (`nl`) — live bevestigd.
- Elke functionele rol heeft een eigen opgeslagen refresh_token:
  `SportlinkClubRefreshToken__<Rol>` (Function App-instelling, lokaal in
  `FunctionApp.Postgres/local.settings.json`).

### 4.2 Waar de code (gaat) zitten
- `Tools/SportlinkTokenCapture` — lokaal hulpmiddel, vangt het refresh_token op via een echte
  browserlogin (Playwright, netwerk-response-event — nooit localStorage, die is versleuteld door
  Sportlink zelf).
- `scripts/dev/Invoke-SportlinkTokenSpike.ps1`, `Invoke-SportlinkMatchLookup.ps1` — lokale
  testscripts voor de refresh-cyclus resp. een read-only wedstrijd-lookup.
- `FunctionApp.Postgres/Admin/SportlinkExtensieRollenFunction.cs` +
  `FunctionApp/Admin/SportlinkExtensieRollenFunction.cs` — rol↔serviceaccount-koppelingsstatus
  (#988), geen live Sportlink-aanroep. Sinds #991 ook `PUT .../rollen/{rolNaam}/token` — de
  productie-bootstrap van het échte refresh_token.
- `Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs` (#991) — read-only
  Sportlink-client, in `Planner.Shared` (providervrije logica: geen directe DB-toegang, alleen via
  de geïnjecteerde `ISportlinkClubTokenStore`) zodat beide tiers hem via DI kunnen gebruiken. Sinds
  #991/#1016 ook de reverse-lookup (`ResolvePublicMatchIdAsync`, `MatchProgramOverview`) — daarvóór
  accepteerde de client `PublicMatchId` uitsluitend als expliciete parameter (de "M"+wedstrijdcode-
  hypothese is weerlegd, zie #987). **Mutatie-afwijzingsvorm live bevestigd (#1040, 2026-09-06):**
  een door Sportlink afgewezen mutatie geeft HTTP 420 met
  `{"Error":true,"Status":"420","Message":"...","ViolationCodes":[...],"Violations":{"CODE":"NL-omschrijving"}}`
  — niet het eerder aangenomen `{"isSuccess":false,"entityViolation":{"violations":[{"code":...}]}}`.
  De happy-path-vorm (`{"isSuccess":true}`) is nog altijd niet live bevestigd; succes wordt daarom
  bepaald door `Error != true && response.IsSuccessStatusCode`, niet door een los veld.
- `ISportlinkClubTokenStore` — twee tier-specifieke implementaties, bewust géén gedeelde: de
  Postgres-tier (`FunctionApp.Postgres/Sportlink/PostgresSportlinkClubTokenStore.cs`, #991) bewaart
  het rotarende refresh_token in een eigen DB-tabel (`public.sportlinkservicetokens`); de SQL
  Server-tier (`Planner.Shared/Integrations/SportlinkClub/SportlinkClubAppSettingsTokenStore.cs`,
  #998) herschrijft een Function App-instelling via de Azure Management API. **De DB-tabel is de
  bewust gekozen aanpak voor de enige live tier** — zie §4.3.
- `Planner.Shared/Integrations/SportlinkClub/SportlinkMutationGuard.cs` (#998) — pure guardrail:
  staat een mutatie alleen toe bij `IsHomeMatch=true`, de bijbehorende Sportlink-permissievlag, én
  blokkeert altijd bij `IsCanceledMatch=true` of `IsConceptMatch=true`. `MatchStatus` wordt bewust
  NIET hard afgedwongen (bijv. op `SCHEDULED`) — die waarde wordt sinds #998 wel uitgebreid
  meegelogd in de audit (zie hieronder), zodat er eerst een seizoen aan echte data verzameld wordt
  vóórdat die eventueel een harde blokkade wordt.
- `FunctionApp/Sportlink/` + `FunctionApp.Postgres/Sportlink/` (#998) — per-tier, niet-gedeelde
  `ISportlinkMutationAuditService`-implementatie; logt vóór én na elke toekomstige mutatie in
  `dbo.SportlinkMutationAudit`/`public.sportlinkmutationaudit`.
- `FunctionApp.Postgres/Integrations/SportlinkClub/SportlinkPublicMatchIdRepository.cs` (#991) —
  de #987-reverse-lookup-cache (`public.sportlinkpublicmatchidcache`, migratie
  `014_sportlink_club_postgres_tokenstore.sql`) en de `his.matches`-opzoeking (wedstrijdcode →
  wedstrijdnummer/datum) die de reverse-lookup nodig heeft.
- `FunctionApp.Postgres/Sportlink/SportlinkMatchFunction.cs` — `GET
  /api/sportlink/match/{wedstrijdcode}` (#991), het eerste endpoint met `RequireWedstrijdzaken`
  i.p.v. `RequireAdmin` (zie #988 Besluit 1). Verbindt de reverse-lookup-cache, de token-store en de
  Dagplanning-GUI met elkaar. Sinds #989 ook `GET .../public-match-id` — dezelfde resolutie zonder
  de volledige `Match`-aanroep, voor de "Open in Sportlink"-deep-link-knop. Sinds #992 ook `PUT
  .../dressingrooms` (kleedkamers) en sinds #993 `PUT .../field` (veld) — de eerste echte
  Sportlink-mutaties, beide via de gedeelde `ExecuteMutationAsync`-helper (resolve → guard →
  audit-Pending → mutatie → audit-voltooien). `IsForceUpdate` bij `.../field` staat hard op
  `false` in de hele keten: de semantiek van die vlag is nooit live bevestigd (issue #993).
  **Kleedkamer-identifier live bevestigd en gefixt (#1045, 2026-09-06):** Sportlink verwacht
  `{FacilityId}-DRESSINGROOM-{n}`, niet een los kleedkamernummer — `SportlinkMatchFunction` bouwt
  deze nu server-side op met de `FacilityId` uit de nieuwe `SportlinkMatch.MatchField`
  (`ExecuteMutationAsync`'s `mutationCall` krijgt daarom sinds #1045 ook de opgehaalde
  `SportlinkMatch` mee, niet alleen `PublicMatchId`). **`.../field` (#993) herontworpen naar
  `UpdateMatchDetails` (#1047) en volledig end-to-end live bevestigd werkend, 2026-09-06.**
  `UpdateFieldAsync` haalt eerst een verse Match-GET-snapshot op
  (`SportlinkClubClient.SportlinkMatchDetailsSnapshot` — uitsluitend intern, geen
  persoonsgegevens), stuurt die terug met alléén het gewijzigde veld overschreven — exact
  Sportlinks eigen UI-patroon. `PublicApplicantId` gaat leeg mee (`""`, niet `null`) — live
  bevestigd geaccepteerd voor een eigen-veld-wijziging (#1048), dus géén aanroep naar
  `FetchUserInfoAsync`/`user/UserInfo` in dit pad (dat sub-endpoint is zelf stuk, `HTTP 602`, en
  blijft alleen #996's actie-pad blokkeren — zie daar). Twee live-gevonden en gefixte bugs
  onderweg: `Field.FieldSize` komt als JSON-getal terug (niet string — zelfde
  `FlexibleStringJsonConverter`-patroon als #1036), en `ExternalMatchId` in deze snapshot idem
  (nieuwe `FlexibleLongJsonConverter`, spiegelbeeld van `FlexibleStringJsonConverter`).
- `FunctionApp.Postgres/Sportlink/SportlinkTokenKeepAliveTimerFunction.cs` — uur-timer die
  `ISportlinkClubClient.VerversTokenAsync` aanroept voor elke rol met een opgeslagen token, ook
  zonder enige gebruikersactie. **Waarom nodig:** Keycloak deactiveert een refresh-token na een
  periode zonder gebruik (`invalid_grant: "Token is not active"`, live vastgesteld 2026-09-05),
  ondanks dat de 6-uurs `refresh_expires_in` nog niet verstreken was — een lui verversende client
  (alleen bij een echte GUI-actie) is dus niet genoeg. Alleen voor de Postgres-tier; de SQL
  Server-tier is rollback-only, zie #1020.
- `FunctionApp.Postgres/Sportlink/SportlinkPublicMatchIdWarmupTimerFunction.cs` (#1017) — dagelijkse
  timer die de PublicMatchId-cache vooraf vult voor de eerstkomende dagen (vandaag + 2), gegroepeerd
  per datum (één `MatchProgramOverview`-aanroep per dag, niet per wedstrijd — zie
  `ISportlinkClubClient.GetMatchProgramOverviewAsync`). Een cache-miss buiten dat venster valt nog
  steeds terug op de bestaande synchrone lookup in `SportlinkMatchFunction`, geen harde fout.
- `FunctionApp.Postgres/Sportlink/SportlinkChangeRequestFunction.cs` (#996) — `GET
  /api/sportlink/change-requests` + `PUT .../{publicRequestId}/action`. Niet wedstrijdcode-
  gescoped (Sportlinks `MatchChangeRequests`-endpoint levert alles voor het gekoppelde
  serviceaccount in één keer) en bewust ZONDER `SportlinkMutationGuard`-check: die guard bewaakt
  onze eigen wedstrijd-mutatie-vlaggen, niet het afhandelen van een verzoek van een tegenstander.
  Audit-logging blijft wel verplicht. `ActOnChangeRequestAsync` haalt `PublicPersonId` van het
  service-account zelf op via `user/UserInfo` — de aanroeper hoeft dat niet te kennen.
- **Dry-run-modus (#998).** De vertakking zit in `SportlinkClubClient.PutMutationAsync` — het ÉNE
  punt waar alle drie de PUT-paden (kleedkamers, veld, change-request-actie) doorheen lopen — niet
  per tier/endpoint apart. Dat garandeert dat token-refresh en de voorbereidende snapshot-/UserInfo-
  GETs ook in dry-run écht gebeuren (realistische simulatie); alleen de daadwerkelijke PUT/POST
  wordt overgeslagen. `SportlinkClubClient` krijgt hiervoor een `Func<bool> isDryRun`-delegate in de
  constructor (zelfde ontkoppelingspatroon als `ISportlinkClubTokenStore` — geen settings-/DB-
  afhankelijkheid in `Planner.Shared`). `FunctionApp.Postgres/Program.cs` geeft een delegate mee die
  bij **elke** aanroep opnieuw `PostgresAppSettings.GetSetting("sportlinkDryRun")` leest (niet één
  keer bij opstarten) — de toggle op Instellingen heeft dus direct effect, zonder herstart, omdat
  `AdminSettingsPut` na elke wijziging `PostgresAppSettings.LoadSettingsAsync` opnieuw aanroept.
  `FunctionApp/Program.cs` (SQL Server-tier) geeft hard `isDryRun: () => true` mee — die tier heeft
  geen enkel mutatie-endpoint en mag dus per definitie nooit een echte PUT versturen.
  `SportlinkMutationResult` kreeg er een derde veld `IsDryRun` bij; het audit-resultaat wordt bepaald
  door de gedeelde helper `SportlinkMatchFunction.BepaalAuditResultaat` (`DryRun` gaat vóór
  `IsSuccess`, want die is bij dry-run altijd `true`).
- **Health-check-endpoint (#998).** `FunctionApp.Postgres/Admin/SportlinkExtensieHealthFunction.cs`
  — `GET /api/beheer/sportlink-extensie/health?live=false` (default). Zonder `?live=true` leest dit
  uitsluitend onze eigen database (extension/dry-run-instelling, `EgressGuard`-status, koppeling +
  laatste tokenverversing per rol uit `public.sportlinkservicetokens`, laatste `Failure`-rij uit
  `public.sportlinkmutationaudit`, laatste rij uit `public.sportlinkcontractcheck`) — geen enkele
  Sportlink-aanroep. Alleen bij expliciete `?live=true` (een gebruikersklik op "Nu live
  controleren") doet het één `VerversTokenAsync` + één `GetMatchAsync` op de meest recent gecachte
  `PublicMatchId` — en rapporteert dan uitsluitend HTTP-status/resultaataard, nooit responsdata.
- **Dagelijkse contract-check (#998).** `FunctionApp.Postgres/Sportlink/SportlinkContractCheckTimerFunction.cs`
  (`0 30 6 * * *`) haalt één keer per dag de rauwe JSON op van de meest recent gecachte
  `PublicMatchId` (`ISportlinkClubClient.GetMatchRawJsonAsync`, niet `MatchProgramOverview` — te
  traag en hier niet nodig) en controleert die met het nieuwe, pure
  `Planner.Shared/Integrations/SportlinkClub/SportlinkMatchContract.cs` op JSON-root-niveau: bestaat
  elk veld waarop `SportlinkMatch` vertrouwt nog, met het verwachte JSON-type? Daalt bewust nooit af
  in `matchOfficials` (persoonsgegevens) en rapporteert uitsluitend veldNAMEN, nooit waarden. Het
  resultaat gaat naar de nieuwe tabel `public.sportlinkcontractcheck` (migratie
  `016_sportlink_dryrun_en_contractcheck.sql`) — bewust géén hergebruik van
  `sportlinkmutationaudit` (dat is "één rij per mutatiepoging", dit is geen mutatie). Bij een
  afwijking hergebruikt de timer het BESTAANDE noodmail-pad
  (`EmailProcessorFunction`'s `INoodmailThrottleStore`/`IEmailGraphService`-patroon, eigen
  throttle-sleutel `sportlink-contract-noodmail`, 24-uurs-interval) — bewust geen nieuw
  alarmeringsmechanisme (kostenbeleid: geen betaalde Log Analytics/App Insights-alert-regel, geen
  GitHub-issue-reporter).

### 4.3 Kostenbeleid-implicatie / tokenopslag (besloten, #990/#991)
Op de Postgres-tier (de enige tier die live draait) wordt het rotarende refresh_token opgeslagen in
een **eigen DB-tabel** (`public.sportlinkservicetokens`), niet in Azure Key Vault en niet als
Function App-instelling via de ARM-API. Key Vault is "potentieel betaald" volgens het kostenbeleid
in `CLAUDE.md` (nieuwe Azure-resource, prijscheck + goedkeuring vereist); een Function
App-instelling herschrijven vanuit de app zelf vereist een aparte Azure AD-integratie met
schrijfrechten op de eigen Function App — een grotere attack surface voor hetzelfde resultaat. Een
DB-tabel is een bestaande, gratis resource en dezelfde vertrouwensgrens als de bestaande
`SqlConnectionString`-secrets.

**Besluit (#1020, 2026-09-06):** de SQL Server-tier (`SportlinkClubAppSettingsTokenStore`, #998)
behoudt bewust de oudere ARM-API-aanpak — géén migratie naar een DB-tabel, ook niet later. Die tier
is rollback-only sinds de Postgres-cutover en heeft geen productieverkeer; een DB-tabel-migratie
bouwen voor een tier die mogelijk nooit meer actief wordt is voorbarig werk. Deze twee tiers hebben
dus bewust verschillende tokenopslag — geen halfslachtige tussenstand, maar een expliciete,
blijvende keuze totdat de SQL Server-tier ooit weer productie-tier zou worden (in dat geval eerst
herbeoordelen, niet automatisch alignen).

### 4.4 HARDE REGEL: coding agents mogen dit mechanisme nooit zelf uitvoeren

**Dit geldt zonder uitzondering, voor Claude Code en elke andere coding agent, in elke sessie:**

> Een coding agent mag een Sportlink-refresh-token nooit zelf uitlezen, opslaan, doorgeven of
> gebruiken om een Sportlink-API aan te roepen — ook niet "even snel om te verifiëren", ook niet
> als het token al zichtbaar is geworden in de sessie.

**Waarom dit geen conventie maar een vastgestelde blokkade is:** tijdens de bouw van deze extension
probeerde de coding agent dit mechanisme meermaals zelf uit te voeren (het token uit de browser
lezen, een script draaien met het token als parameter) — en werd dit **consequent, op twee
onafhankelijke tokens via twee verschillende mechanismen**, geblokkeerd door de auto-mode-
veiligheidslaag van Claude Code zelf. Dit is dus een technisch afgedwongen grens, niet een keuze.

**Incident (2026-09-04):** ondanks deze blokkades kwam één refresh-token per ongeluk in de
chatsessie met de agent terecht (bedoeld voor een lokale scriptprompt, per abuis in de chat
geplakt). De eigenaar moest direct volledig uitloggen bij Sportlink om die token in te trekken.
Elk token dat ooit in een agent-sessie zichtbaar wordt, geldt vanaf dat moment als verbrand.

**Praktisch gevolg voor deze scripts:**
- `Invoke-SportlinkTokenSpike.ps1` is van nature agent-veilig: het vráágt bij elke run opnieuw om
  het token via `Read-Host -AsSecureString`, wat in een niet-interactieve agent-tool-omgeving
  (stdin op `/dev/null`) niet ingevuld kan worden.
- `Invoke-SportlinkMatchLookup.ps1` leest het token zelf uit `local.settings.json` — dat heeft
  daarom een **expliciete `Read-Host`-mensbevestiging** nodig (typ "JA") vóórdat het token gebruikt
  wordt. Zonder die bevestiging zou dit script, anders dan het spike-script, wél door een agent
  silently uitgevoerd kunnen worden — dat is precies wat er (bijna) gebeurde bij de review die tot
  dit document leidde.
- `Tools/SportlinkTokenCapture` is agent-veilig door ontwerp: het vereist een echte, zichtbare
  browserlogin (incl. eventuele MFA) die een agent sowieso niet kan voltooien.
- **Nieuw script, nieuwe regel:** elk toekomstig script dat een opgeslagen refresh_token gebruikt
  krijgt dezelfde `Read-Host`-mensbevestiging als `Invoke-SportlinkMatchLookup.ps1` — niet alleen
  een waarschuwing in commentaar. Commentaar wordt door een agent gelezen maar is geen technische
  barrière; `Read-Host` in een niet-interactieve omgeving wel.
- Verificatie van de refresh-cyclus, of van een nieuw endpoint dat een refresh_token nodig heeft,
  gebeurt dus altijd door een mens (met een van bovenstaande scripts) of door de daadwerkelijk
  gedeployde Function App-runtime zelf — nooit door een agent tijdens ontwikkeling.

**Verfijning (besloten met de eigenaar, 2026-09-06): browser-automatisering tegen de eigen,
lokaal draaiende webapp is wél toegestaan, en is geen uitzondering op bovenstaande regel maar
een andere invulling ervan.** Het onderscheid zit in *wie het token vasthoudt*, niet in *hoe de
test getriggerd wordt:

- **Verboden blijft:** een agent die zelf een HTTP-aanroep naar Sportlink doet, een token uit een
  bestand leest, of een token als scriptparameter doorgeeft — ongeacht hoe onschadelijk het doel
  (bijv. een testwedstrijd) is. Dit is de blokkade uit het incident hierboven en de
  auto-mode-veiligheidslaag; die triggert op *agent gebruikt zelf een token*, niet op *welke
  wedstrijd geraakt wordt*.
- **Toegestaan:** een agent die met browser-automatisering (Playwright) een knop indrukt op de
  eigen, lokaal draaiende Admin GUI (`http://localhost:5242`), waarna de al-draaiende FunctionApp
  (los proces, eigen geconfigureerde `ISportlinkClubTokenStore`) de daadwerkelijke Sportlink-
  aanroep doet. De agent leest, ziet of geeft het token op geen enkel moment door — dat is precies
  "de daadwerkelijk gedeployde Function App-runtime zelf" uit de regel hierboven, alleen lokaal
  gestart in plaats van in Azure.
- **Vaste, door de eigenaar goedgekeurde testwedstrijd** voor dit soort PUT/DELETE-verificatie:
  zie de projectmemory `project_sportlink_testwedstrijd_put_del` (wedstrijdnummer 69, TEST1 vs
  TEST2, veld 6, een zondag — geen echt team, geen echte speeldag). Gebruik altijd deze wedstrijd
  voor mutatietests, nooit een willekeurige, tenzij opnieuw afgestemd met de eigenaar.

## 5. Risico's en beperkingen

- Onofficiële integratie: kan bij een Sportlink-release breken (bundle-hashes wijzigen al vaker dan
  endpoints). Gebruiksvoorwaarden van Sportlink zijn niet beoordeeld op dit gebruik.
- Sportlink logt alle acties op het gekoppelde serviceaccount; Sentry/GA in hun SPA zien ons
  verkeer niet, de server wel.
- AVG: wedstrijd- en officials-data bevat persoonsgegevens (namen, telefoonnummers). Nooit opslaan
  buiten wat al in onze eigen DB staat; nooit `PersonRegistrations`/officials-zoekendpoints
  aanroepen (bevatten persoonsgegevens die we niet nodig hebben). `MatchProgramOverview` wordt
  sinds #991 wél aangeroepen (voor de #987-reverse-lookup), maar uitsluitend het resultaat
  `PublicMatchId` wordt gecachet — nooit de overige, niet-club-gescoped wedstrijdgegevens uit die
  respons.
- **Incident (2026-09-06): tijdelijke diagnostische logging loggede per ongeluk de volledige
  Match-respons, inclusief `MatchOfficials` (naam, geboortedatum, foto-URL van de scheidsrechter).**
  Ontstaan tijdens het live debuggen van issue #1038 (`MatchDate` kwam genest terug) — een `catch`-blok
  logde tijdelijk de rauwe JSON-body om de exacte oorzaak te vinden. Het logbestand stond lokaal
  (nooit gecommit) en is direct verwijderd; de inhoud kwam wel even in de agent-sessie terecht.
  **Regel voor elke toekomstige diagnose van deze endpoint:** log nooit de volledige respons-body,
  ook niet tijdelijk — gebruik `JsonDocument` om gericht alleen de raw text van het specifieke
  veld te loggen dat de fout veroorzaakt (zie het patroon in git-historie van #1038 voor een
  voorbeeldimplementatie die nooit in `MatchOfficials` afdaalt zonder dat expliciet te bedoelen).
- **#996's actie-pad (goedkeuren/afwijzen) kan niet veilig getest worden met de vaste testwedstrijd
  (2026-09-06 vastgesteld).** In tegenstelling tot #992/#993 is `MatchChangeRequests` niet per
  wedstrijd gescoped — het levert alle openstaande verzoeken van échte tegenstanders voor het hele
  serviceaccount. Er bestaat geen manier om een fictief, veilig testbaar verzoek te laten ontstaan
  binnen Sportlink zelf. Een test van de actie zou dus een echte beslissing forceren op een echt
  verzoek van een echte tegenstander — alleen te doen met expliciete instemming van de eigenaar
  over een specifiek, door hem aangewezen verzoek.
- Volledige, actuele lijst met openstaande vragen en risico's: onderzoeksrapport §5/§7.

## 6. Technische bijlage — endpoints, bodies, token-flow (#998)

Overgenomen uit de code (`Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs`) zodat
dit document zelfstandig leesbaar is, zonder het losse onderzoeksrapport erbij nodig te hebben voor
de kernfeiten. Bij een discrepantie is de code leidend; werk dan dit overzicht bij.

### 6.1 Token-flow
- Token-endpoint: `POST https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token`
  — `grant_type=refresh_token`, `client_id=sportlink-club-web`, `refresh_token=<opgeslagen waarde>`.
- Respons bevat `access_token`, `expires_in` (default 3600 als afwezig), en een geroteerd
  `refresh_token` — dat nieuwe token wordt teruggeschreven via `ISportlinkClubTokenStore`
  (asynchroon, niet-blokkerend) vóórdat het oude ongeldig kan worden.
- In-memory cache per functionele rol (`ConcurrentDictionary`), met een marge van 60 seconden vóór
  de werkelijke `expires_in` — een aanroep binnen die marge ververst proactief in plaats van een
  401 af te wachten. Bij een écht onverwachte 401 (token toch al ongeldig): cache invalideren, één
  keer geforceerd verversen, één keer opnieuw proberen; blijft het 401 → `HerkoppelingVereist`.
- `400` met `invalid_grant` op het token-endpoint → `HerkoppelingVereist` (refresh-token zelf dood,
  handmatige herkoppeling nodig via §3.3).

### 6.2 Endpoints (alle onder `https://club.sportlink.com/navajo/entity/common/clubweb/`)

| Endpoint | Methode | Doel | Guard vooraf |
|---|---|---|---|
| `competition/match/Match` (`?PublicMatchId=`) | GET | Wedstrijddetails ophalen (ook: snapshot vóór een veldwijziging, ook: rauwe vormcontrole voor de contract-check) | — |
| `competition/match/MatchProgramOverview` (`?DateFrom=&DateTo=`) | GET | Niet-club-gescoped, 1-daags programma — voor de `PublicMatchId`-reverse-lookup en de dagelijkse warmup-timer | — |
| `competition/match/UpdateMatchDressingRooms` | PUT | Kleedkamers toewijzen | `SportlinkMutationSoort.Kleedkamers` |
| `competition/match/UpdateMatchDetails` | PUT | Veld(deel) wijzigen — verwacht het VOLLEDIGE wedstrijdrecord, niet een klein patch (zie §4.2) | `SportlinkMutationSoort.Veld` |
| `competition/match/changerequest/MatchChangeRequests` | GET | Inkomende wijzigingsverzoeken van tegenstanders ophalen | — (geen `SportlinkMutationGuard`, zie §4.2) |
| `competition/match/changerequest/MatchChangeRequestAction` | PUT | Verzoek goed-/afkeuren | — (idem) |
| `user/UserInfo` | GET | `PublicPersonId` van het service-account, nodig voor `MatchChangeRequestAction` | — |

Elke aanroep zet drie headers: `X-Navajo-Entity` (het aangeroepen pad, geen vaste appnaam),
`X-Navajo-Instance: KNVB`, `X-Navajo-Locale: nl`.

### 6.3 Bodies (PascalCase — de wire-vorm, geen `JsonPropertyName` nodig)

- **`UpdateMatchDressingRooms`**: `{ PublicMatchId, HomeDressingRoomId, AwayDressingRoomId,
  OfficialDressingRoomId }` — elk kleedkamer-ID heeft de vorm `{FacilityId}-DRESSINGROOM-{n}`
  (live vastgesteld, #1045), niet een los nummer.
- **`UpdateMatchDetails`**: `{ ConfirmationNeeded, IsForceUpdate (altijd false), IsMatchChangeRequestMandatory: false,
  IsOwnFacility: true, IsPlannableByClub: false, IsSuccess: false, PublicApplicantId ("" voor een
  eigen-veld-wijziging, live bevestigd geaccepteerd, #1048), PublicMatchId, MatchData }` waarbij
  `MatchData` het volledige, net opgehaalde wedstrijdrecord is met alleen het gewijzigde veld
  overschreven (zie §4.2 voor waarom).
- **`MatchChangeRequestAction`**: `{ Action ("APPROVE"|"DENY"), PublicMatchId, PublicPersonId,
  PublicRequestId, Remarks }`.
- **Afwijzingsvorm (HTTP 420, live bevestigd #1040)**: `{"Error":true,"Status":"420",
  "Message":"Validation exception : <code>","ViolationCodes":["<code>", ...],
  "Violations":{"<code>":"Nederlandse omschrijving"}}`. Succes wordt bepaald door `Error != true &&
  response.IsSuccessStatusCode`, niet door een afzonderlijk `isSuccess`-veld (de happy-path-vorm is
  nooit live bevestigd).

### 6.4 Dry-run (#998)
In dry-run wordt de body nog wél geserialiseerd (zodat een serialisatiefout alsnog opduikt) maar
niet verstuurd — `PutMutationAsync` retourneert direct `{IsSuccess: true, Violations: null,
IsDryRun: true}` zonder een HTTP-aanroep te doen. De body zelf wordt nooit gelogd (kan
teamnamen/persoonsgegevens bevatten); alleen `{EntityName}`/`{Endpoint}` verschijnen in de log.

## 7. Bronnen
- [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) — volledig technisch bronrapport
- Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en sub-issues #987-#998
- [`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) — rolbeheer en N-user-test
- [`docs/ARCHITECTUUR-DATABASE-TIERS.md`](ARCHITECTUUR-DATABASE-TIERS.md) — tier-bouwvolgorde; §4.2 hierboven legt uit waarom `SportlinkClubClient` wél in `Planner.Shared` zit maar de tokenopslag per tier verschilt
