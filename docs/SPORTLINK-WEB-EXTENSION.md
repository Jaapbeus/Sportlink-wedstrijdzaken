# Sportlink Web Extension

> **Status: gedeeltelijk gebouwd, nog geen enkele Sportlink-mutatie live.** Epic
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
  `014_sportlink_club_matchid_cache.sql`) en de `his.matches`-opzoeking (wedstrijdcode →
  wedstrijdnummer/datum) die de reverse-lookup nodig heeft.
- `FunctionApp.Postgres/Sportlink/SportlinkMatchFunction.cs` (#991) — `GET
  /api/sportlink/match/{wedstrijdcode}`, het eerste endpoint met `RequireWedstrijdzaken` i.p.v.
  `RequireAdmin` (zie #988 Besluit 1). Enige plek die de reverse-lookup-cache, de token-store en de
  Dagplanning-GUI met elkaar verbindt.

### 4.3 Kostenbeleid-implicatie / tokenopslag (besloten, #990/#991)
Op de Postgres-tier (de enige tier die live draait) wordt het rotarende refresh_token opgeslagen in
een **eigen DB-tabel** (`public.sportlinkservicetokens`), niet in Azure Key Vault en niet als
Function App-instelling via de ARM-API. Key Vault is "potentieel betaald" volgens het kostenbeleid
in `CLAUDE.md` (nieuwe Azure-resource, prijscheck + goedkeuring vereist); een Function
App-instelling herschrijven vanuit de app zelf vereist een aparte Azure AD-integratie met
schrijfrechten op de eigen Function App — een grotere attack surface voor hetzelfde resultaat. Een
DB-tabel is een bestaande, gratis resource en dezelfde vertrouwensgrens als de bestaande
`SqlConnectionString`-secrets.

**Bekende inconsistentie (niet blokkerend):** de SQL Server-tier (`SportlinkClubAppSettingsTokenStore`,
#998) gebruikt nog wél de ARM-API-aanpak — die tier is rollback-only en heeft geen productieverkeer,
dus dit is bewust niet in dezelfde PR meegenomen. Zie #1020 voor het align/deprecate-vervolg.

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
- Volledige, actuele lijst met openstaande vragen en risico's: onderzoeksrapport §5/§7.

## 6. Bronnen
- [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) — volledig technisch bronrapport
- Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en sub-issues #987-#998
- [`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) — rolbeheer en N-user-test
- [`docs/ARCHITECTUUR-DATABASE-TIERS.md`](ARCHITECTUUR-DATABASE-TIERS.md) — tier-bouwvolgorde; §4.2 hierboven legt uit waarom `SportlinkClubClient` wél in `Planner.Shared` zit maar de tokenopslag per tier verschilt
