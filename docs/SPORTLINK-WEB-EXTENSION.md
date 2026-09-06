# Sportlink Web Extension

> **Status: gedeeltelijk gebouwd. Het read-only Match-endpoint (#991) is 2026-09-06 lokaal live
> geverifieerd tegen een echte testwedstrijd** (zie §4.4/#1036/#1038 voor de daarbij gevonden en
> gefixte bugs: `ExternalMatchId` kwam als JSON-getal terug in plaats van string (#1036), en
> `MatchDate` kwam genest terug (`{Date, StartTime, DateTime}`) in plaats van als losse ISO-string
> (#1038)). Kleedkamers (#992),
> veld (#993) en inkomende wijzigingsverzoeken (#996) zijn gebouwd en CI-groen; live-verificatie
> van de daadwerkelijke schrijfacties volgt. #994/#995/#997 zijn bewust nog niet gebouwd: de
> exacte requestvorm is niet live vastgesteld (zie de betreffende issues). Epic
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
  hypothese is weerlegd, zie #987).
- `ISportlinkClubTokenStore` — twee tier-specifieke implementaties, bewust géén gedeelde: de
  Postgres-tier (`FunctionApp.Postgres/Sportlink/PostgresSportlinkClubTokenStore.cs`, #991) bewaart
  het rotarende refresh_token in een eigen DB-tabel (`public.sportlinkservicetokens`); de SQL
  Server-tier (`Planner.Shared/Integrations/SportlinkClub/SportlinkClubAppSettingsTokenStore.cs`,
  #998) herschrijft een Function App-instelling via de Azure Management API. **De DB-tabel is de
  bewust gekozen aanpak voor de enige live tier** — zie §4.3.
- `Planner.Shared/Integrations/SportlinkClub/SportlinkMutationGuard.cs` (#998) — pure guardrail:
  staat een mutatie alleen toe bij `IsHomeMatch=true` én de bijbehorende Sportlink-permissievlag.
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
- Volledige, actuele lijst met openstaande vragen en risico's: onderzoeksrapport §5/§7.

## 6. Bronnen
- [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) — volledig technisch bronrapport
- Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en sub-issues #987-#998
- [`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) — rolbeheer en N-user-test
- [`docs/ARCHITECTUUR-DATABASE-TIERS.md`](ARCHITECTUUR-DATABASE-TIERS.md) — tier-bouwvolgorde; §4.2 hierboven legt uit waarom `SportlinkClubClient` wél in `Planner.Shared` zit maar de tokenopslag per tier verschilt
