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
3. Log in het geopende browservenster in met het zojuist aangemaakte serviceaccount.
4. Klik in Instellingen op "Koppeling (opnieuw) registreren" en vul de accountnaam in ter
   herkenning — dit is geen live verificatie, puur een leesbaar label voor de statustabel.
5. Herhaal deze koppeling alleen als Sportlink de onderliggende sessie ooit volledig intrekt
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
  (#988), geen live Sportlink-aanroep.
- `Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs` (#991) — read-only
  Sportlink-client, in `Planner.Shared` (providervrije logica, geen SQL) zodat beide tiers hem via
  DI kunnen gebruiken, zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` §2 voor die uitzonderingsregel.
  Accepteert `PublicMatchId` uitsluitend als expliciete parameter — geen automatische afleiding uit
  wedstrijdcode/wedstrijdnummer (die hypothese is weerlegd, zie #987/#1016). Nog niet aangesloten op
  een GUI-scherm of een schrijvend endpoint.
- `Planner.Shared/Integrations/SportlinkClub/SportlinkMutationGuard.cs` (#998) — pure guardrail:
  staat een mutatie alleen toe bij `IsHomeMatch=true` én de bijbehorende Sportlink-permissievlag.
- `FunctionApp/Sportlink/` + `FunctionApp.Postgres/Sportlink/` (#998) — per-tier, niet-gedeelde
  `ISportlinkMutationAuditService`-implementatie; logt vóór én na elke toekomstige mutatie in
  `dbo.SportlinkMutationAudit`/`public.sportlinkmutationaudit`.

### 4.3 Kostenbeleid-implicatie
Opslag van het refresh_token als Azure Function App-instelling (gekozen) versus Key Vault staat nog
open — Key Vault is "potentieel betaald" volgens het kostenbeleid in `CLAUDE.md`, dus vereist een
prijscheck en expliciete goedkeuring vóór aanmaak, mocht die keuze ooit omgezet worden.

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
  buiten wat al in onze eigen DB staat; nooit `MatchProgramOverview`/`PersonRegistrations`
  aanroepen (traag, en bevat persoonsgegevens die we niet nodig hebben).
- Volledige, actuele lijst met openstaande vragen en risico's: onderzoeksrapport §5/§7.

## 6. Bronnen
- [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) — volledig technisch bronrapport
- Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en sub-issues #987-#998
- [`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) — rolbeheer en N-user-test
- [`docs/ARCHITECTUUR-DATABASE-TIERS.md`](ARCHITECTUUR-DATABASE-TIERS.md) — waarom `SportlinkClubClient` in `Planner.Shared` hoort
