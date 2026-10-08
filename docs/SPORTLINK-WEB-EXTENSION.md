# Sportlink Web Extension

## Wat kun je ermee?

De Sportlink-koppeling brengt veelgebruikte wedstrijdacties naar de webapp. Je hoeft daardoor
voor die acties niet steeds zelf een Sportlink-scherm op te zoeken. De koppeling moet wel zijn
ingericht en gebruikt de rechten van het gekoppelde Sportlink-account.

| Actie | Huidige mogelijkheden |
|---|---|
| Wedstrijd bekijken | Details ophalen en de wedstrijd in Sportlink openen |
| Veld en kleedkamers | Toewijzen vanuit de webapp, als wedstrijd en rechten dat toestaan |
| Scheidsrechters | Officials toewijzen; de eerdere verplichte simulatie is opgeheven |
| Oefenwedstrijd | Aanmaken en verwijderen; verwijderen betreft een eigen clubwedstrijd, geen KNVB-wedstrijd |
| Datum, tijd of accommodatie | Validatiestap beschikbaar; de aparte bevestigingsstap voor een verplicht wijzigingsverzoek is nog niet gebouwd |
| Inkomende wijzigingsverzoeken | Ophalen en acties voor goedkeuren/afwijzen zijn gebouwd; de actie vereist een werkende `UserInfo`-lookup |

**Begin met dry-run.** De club-instelling `sportlinkDryRun` staat standaard aan. Dan worden
schrijfacties gesimuleerd; voorbereidende leesacties kunnen nog wel Sportlink benaderen. Met
dry-run uit kunnen de bevestigde mutaties echt schrijven. Je rol en de wedstrijdrechten blijven
bepalen wat mag. Een simulatie bewijst niet dat Sportlink de echte wijziging zal accepteren.

Automatische herlogin is optioneel. Met die inrichting kan de app een verlopen Sportlink-sessie
vernieuwen; zie [automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md). Voor dagelijkse bediening
kun je de [gebruikershandleiding](BEHEERDER-HANDLEIDING.md) volgen.

## Technische verdieping

De status hierboven volgt de gedeelde clientcode: `MatchOfficialsActionLiveBevestigd`,
`UpdateMatchDetailsChangeRequestLiveBevestigd`, `ClubMatchLiveBevestigd` en
`ClubMatchDeleteLiveBevestigd` staan alle vier op `true`. Dat beschrijft de code, niet een
live-test van jouw installatie. `MatchChangeRequestActionAsync` haalt de aanvrager op via
`FetchUserInfoAsync`; eerdere verificatie vond daar een fout. Ga bij inrichting dus na of die
actie met jouw account werkt.

Hieronder staan het protocol, de inrichting, beveiligingsgrenzen en de geschiedenis van de
implementatie. Oudere proefresultaten beschrijven die proef, niet de huidige stand van alle acties.
Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en het
[brononderzoek](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) geven de achtergrond.

> **HARDE REGEL VOOR CODING AGENTS — lees §4.4 vóór je iets met dit mechanisme aanraakt.** Een
> coding agent leest, kopieert, bewaart of gebruikt nooit zelf een Sportlink-refresh- of
> access-token, en doet nooit zelf een HTTP-aanroep naar `club.sportlink.com` of
> `idm.sportlink.com` — ook niet "even om te verifiëren", ook niet als het token al in de sessie
> zichtbaar is geworden. Volledige regel, de blokkade-onderbouwing, de ene toegestane invulling en
> het incident van 2026-09-04:
> [§4.4](#44-harde-regel-coding-agents-mogen-dit-mechanisme-nooit-zelf-uitvoeren).

## 1. Wat dit is

> **Authenticatie is gewijzigd in #1411.** Oude passages in dit historische featuredocument over
> token-capture, handmatige refresh-tokenregistratie, credentials elke dag handmatig vernieuwen of
> agentverboden voor de oude tokenflow zijn achterhaald. De actieve setup- en herstelprocedure is
> [`docs/SPORTLINK-AUTOLOGIN.md`](SPORTLINK-AUTOLOGIN.md); gebruik die als enige bron.

Een optionele uitbreiding die wedstrijdwijzigingen (kleedkamers, veld, scheidsrechters,
wijzigingsverzoeken) rechtstreeks vanuit deze webapp terugschrijft naar club.sportlink.com — in
plaats van dat de wedstrijdsecretaris dat apart, handmatig in Sportlink Club moet doen. Het is een
**onofficiële integratie**: Sportlink biedt hier geen publieke API voor, dit reverse-engineert de
JSON-API die hun eigen React-SPA gebruikt. Staat daarom standaard **UIT** per club
(`SportlinkExtensionEnabled` in Instellingen) en kan bij een Sportlink-release breken.

## 2. Voor gebruikers (wedstrijdsecretaris)

- Dit verandert vandaag nog niets aan hoe je werkt — de extension staat standaard uit, en zelfs
  wanneer een club hem aanzet, gebeurt er niets zonder dat jij op een knop klikt.
- Als je club de extension gebruikt: een beheerder configureert de automatische login voor het
  Sportlink-account via de beveiligde beheerpagina. De initiële setup en het herstel staan in
  [`docs/SPORTLINK-AUTOLOGIN.md`](SPORTLINK-AUTOLOGIN.md); routinematig handmatig tokens vernieuwen
  is niet nodig.
- Alles wat de extension straks doet, doet zij op naam van dat aparte account — niet op jouw eigen
  naam — dus in Sportlink's eigen logs zie je dat terug als bijvoorbeeld "webapp-wedstrijdzaken".
- Wat vandaag al werkt: bij elke wedstrijd op Planning en Veld optimalisatie (#1361) staat een knop "Open in Sportlink" die de
  juiste wedstrijd direct in Sportlink Club opent (nieuw tabblad) — scheelt het zoeken in het trage
  overzichtsscherm. Je klikt daar zelf nog op opslaan; deze knop wijzigt zelf niets (#989).

## 3. Voor beheerders

### 3.1 Inschakelen
De extensie heeft sinds #1122 een **eigen scherm**: Instellingen → kaart "Sportlink Web Extension"
→ **Openen**, of via het menu Instellingen → Sportlink Ext. (route
`/sportlink-extension-settings`, `BlazorAdmin/Pages/SportlinkExtensieInstellingen.razor`). Alle
schakelaars en knoppen uit deze paragraaf staan op dát scherm — op de Instellingen-pagina zelf
staat alleen nog de kaart met de Openen-knop.

Zet daar de schakelaar aan. Direct daaronder staat een tabel met alle functionele rollen (nu:
"Wedstrijdzaken") en of daar al een Sportlink-serviceaccount aan gekoppeld is.

**Niet beschikbaar bij de democlub.** Staat de clubkiezer op `ALLSTARS`, dan toont dit scherm
uitsluitend "Niet beschikbaar in testmodus (demo-club)." en laadt het niets
(`SportlinkExtensieInstellingen.razor.cs`, `_isTestmodus`). Wissel eerst naar de echte club.

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

**Op de SQL Server-tier stond dry-run tot #1266 onvoorwaardelijk hard aan in code**, met als
motivering dat die tier nooit een mutatie-endpoint had gehad en dat ook niet stilzwijgend via een
instelling mocht krijgen. #1266 bouwt die endpoints er wel op, want beide tiers zijn gelijkwaardig.
De veiligheidsrail zelf blijft: `AppSettings.SportlinkDryRun` staat standaard op 1 (dry-run aan) en
wordt fail-safe gelezen — faalt het lezen, dan blijft dry-run aan. Een club moet de schakelaar dus
bewust omzetten, precies zoals op de Postgres-tier.

### 3.1b Statussectie — wat er te zien is
Onder de rollen-tabel op het scherm Sportlink Web Extension staat sinds #998 een statussectie die in één oogopslag toont:
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
Deze oude procedure is verwijderd. Er is geen token-capturetool, handmatige token-upload of
legacy-opslag meer. Gebruik uitsluitend [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md)
voor de productie-initialisatie en het herstel. MFA blijft ingeschakeld; een bevoegde beheerder
stelt credentials en authenticator-instelsleutel in via de beveiligde beheerpagina.

### 3.4 Entra-rol "Wedstrijdzaken"
Naast de bestaande `admin`/`user`-rollen bestaat er een aanvullende approl `Wedstrijdzaken`
(toegevoegd via `scripts/azure/Configure-EntraApp.ps1`) — een gebruiker heeft dus bijvoorbeeld
`["admin","Wedstrijdzaken"]`. Deze rol vervangt `admin`/`user` niet en geeft op zichzelf geen
toegang tot de Admin GUI; ze wordt gebruikt om specifieke Sportlink-mutatie-acties (vanaf #991) te
gaten, bovenop de bestaande admin-toegang. Zie
[`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) voor het volledige rolbeheer-protocol en de
verplichte N-user-test.

**Herziening (#1376):** een `admin`-toewijzing is sinds #1376 op zichzelf voldoende voor de eerste
van de twee autorisatiepoorten die elk Sportlink-endpoint doorloopt
(`EasyAuthHelper.RequireWedstrijdzaken` staat sindsdien `Wedstrijdzaken` ÓF `admin` toe) — een
aparte `Wedstrijdzaken`-toewijzing is voor een volledige beheerder daar niet meer voor nodig. Dit
draait een eerder, expliciet besluit terug (zie `memory/wedstrijdzaken-rol-vereist-altijd-ook-admin.md`
in het projectgeheugen): tot #1376 golden `admin` en `Wedstrijdzaken` bewust als losse,
AND-gecombineerde Entra-toewijzingen, met als motivering ruimte te houden voor een toekomstige,
beperktere rol (bijv. een "sectiehoofd" dat wél admin is maar géén Sportlink-mutaties mag
triggeren — zie `docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md` §6).

**#1400 heeft ook de TWEEDE poort verruimd (fix van de #1379-bevinding).** Tot #1400 was de tweede,
erop volgende poort (`AdminEndpoint.ExecuteAsync` → `EasyAuthHelper.RequireAdmin`) ongewijzigd
sinds #1272 en vereiste onveranderd uitsluitend `admin` — een gebruiker met **alleen**
`user` + `Wedstrijdzaken` (geen `admin`) passeerde dus wél de eerste poort maar strandde op de
tweede (`403`). #1400 vervangt die tweede poort door `AdminEndpoint.ExecuteWedstrijdzakenOfAdminAsync`,
die dezelfde `Wedstrijdzaken`-ÓF-`admin`-regel gebruikt als de eerste — bewust getest
(`Sportlink_AlleenWedstrijdzaken_PasseertDeAdminPoort`, beide tiers). **Netto-effect: `Wedstrijdzaken`
is sinds #1400 een echt alternatief voor `admin`** voor de mutatie-endpoints (kleedkamers, veld,
scheidsrechters, wijzigingsverzoek datum/tijd/accommodatie) — niet alleen aanvullend. Een gewone
`user` zonder `Wedstrijdzaken` en zonder `admin` krijgt op deze mutatie-endpoints nog steeds `403`
(bewust getest: `Sportlink_AlleenUser_WordtGeweigerdOpDeWedstrijdzakenPoort`).

**Viewing is sinds #1400 losgekoppeld van de Wedstrijdzaken-rol.** `GET /api/sportlink/match/
{wedstrijdcode}` en `.../public-match-id` — het Sportlink-paneel in Planning — lopen niet meer via
de Wedstrijdzaken-poort, maar via `AdminEndpoint.ExecuteAuthenticatedAsync` (elke ingelogde rol,
`admin` + `user`): Planning en het Sportlink-paneel zijn generiek zichtbaar voor elke gebruiker.
Welke velden een niet-Wedstrijdzaken-viewer daadwerkelijk terugkrijgt blijft wél server-side
geredigeerd: `SportlinkMatchFunction.BepaalRolFeatureToestemmingenAsync` checkt expliciet of de
aanroeper zelf `admin` óf `Wedstrijdzaken` is (`EasyAuthHelper.IsInRole`) — een gewone `user` krijgt
`(false,false,false,false)` terug (`SportlinkRolFeatureToestemmingen`, inclusief de nieuwe
`MagWijzigen`-vlag) en dus geen scheidsrechter-relatiecodes en geen zichtbare wijzig-knoppen in
`SportlinkMatchPanel` (die blijven puur UX — de mutatie-endpoints zelf blijven de leidende controle).

### 3.5 Toegangsmatrix per rol (#1390, opvolger van #1341/epic #1338)
Een beheerder kan op **Instellingen → Rechten per rol** een matrix instellen: rijen zijn elk
menu-item/functie (inclusief de drie Sportlink-mutatieacties kleedkamers/scheidsrechter/veld),
kolommen zijn de vier instelbare rollen `user`/`Wedstrijdzaken`/`Sectiehoofd`/`Ledenadministratie`.
Bewust geen kolom voor `admin`: admin heeft dit altijd allemaal aan. Dit is de opvolger van de
oorspronkelijke drie losse Sportlink-toggles uit #1341 — zelfde databasetabel
(`rolfeatureinstellingen`/`dbo.RolFeatureInstellingen`), nu met veel meer rijen en rollen.

Vier dingen om te onthouden:

- **Server is leidend voor de drie Sportlink-mutatieacties, niet de UI-toggle.**
  `SportlinkMatchFunction.ExecuteMutationAsync` (beide tiers) wijst een uitgeschakelde actie af met
  HTTP 409, ook bij een directe API-aanroep buiten de Blazor-UI om, voor de rol `Wedstrijdzaken`.
  De UI verbergt de bijbehorende sectie in `SportlinkMatchPanel` alleen om een voorspelbare 409 te
  voorkomen.
- **De overige rijen (menu-zichtbaarheid, buiten Planning) zijn vandaag uitsluitend configuratie,
  geen handhaving.** Er bestaat nog geen endpoint waarmee een niet-admin-gebruiker zijn éigen
  rechten kan opvragen, en `BlazorAdmin/Services/AuthGate.cs` laat uitsluitend `admin`/`user` de
  app-shell in — een matrixrij uitzetten verbergt dus (nog) geen ander menu-item en blokkeert geen
  ander endpoint. **Uitzondering sinds #1400: Planning/het Sportlink-paneel viewen is hard-coded
  generiek open voor `admin`+`user`** (niet via deze matrix instelbaar — zie §3.4) en de drie
  Sportlink-mutatieacties zijn hard-coded `admin`-of-`Wedstrijdzaken` (de AND-gate-fix van #1379).
  Het uitbreiden van de autorisatiewrapper naar de twee nieuwe rollen `Sectiehoofd`/
  `Ledenadministratie` blijft een apart, nog openstaand vervolgtraject.
- **`Sectiehoofd` en `Ledenadministratie` zijn vandaag instelbare rijen, geen toewijsbare rollen.**
  Ze bestaan nog niet als Entra-approl (§3.4 hierboven beschrijft alleen `Wedstrijdzaken`) — een
  gebruiker kan deze rol dus nog niet daadwerkelijk krijgen. Toevoegen als Entra-approl is een
  aparte infrastructuurwijziging via `scripts/azure/Configure-EntraApp.ps1`, die expliciete
  bevestiging van de eigenaar vereist.
- **`admin` is altijd toegestaan** — geen rij nodig, geen UI-optie om admin te beperken.
  **Ontbrekende instelling = uitgeschakeld (fail-closed).** Een club die deze pagina nog nooit
  heeft geopend, heeft dus alles standaard uitgeschakeld voor de vier instelbare rollen — een
  beheerder moet elke rij bewust aanzetten.

## 4. Voor developers

### 4.1 Architectuur in het kort
- club.sportlink.com is een React-SPA op een JSON-API (`/navajo/entity/common/clubweb/...`).
  Authenticatie via Keycloak (`idm.sportlink.com`, realm `sportlink`, client `sportlink-club-web`),
  standaard OAuth2 authorization_code+PKCE, `Bearer`-token, geen cookies.
- Onze backend gebruikt de **refresh_token-grant** tussen volledige logins. Bij een nieuwe login verkrijgt onze backend een
  verse sessie; tussen volledige logins wordt de refresh-token-grant gebruikt. Zie de actuele
  coördinatie en herstelregels in [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md).
- Twee routes zonder eigen redirect-URI zijn **bevestigd gesloten**, geen toekomstig herstel
  hierop proberen: eigen redirect_uri → HTTP 400 (client whitelist); `device_code`-grant →
  `unauthorized_client` (uitgeschakeld voor deze client). Zie onderzoeksrapport §2.6/§3.B.
- API-calls vereisen `X-Navajo-Entity` (= het aangeroepen pad, geen vaste appnaam),
  `X-Navajo-Instance` (vaste waarde `KNVB`), `X-Navajo-Locale` (`nl`) — live bevestigd.
- Elke functionele rol heeft credentials en roterende tokens in de encrypted opslag; actuele
  configuratie staat in [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md).

### 4.2 Waar de code (gaat) zitten

> **Tier-scope (#1266).** De hele extensie bestaat sinds #1266 op **beide** database-tiers. Elk
> `FunctionApp.Postgres/…`-pad hieronder heeft een tegenhanger op hetzelfde relatieve pad onder
> `FunctionApp/` (SQL Server): alle tien `/api/sportlink/*`-routes, alle vier
> `/api/beheer/sportlink-extensie/*`-routes en alle drie de timers (keep-alive, warmup,
> contract-check), plus de opruimtimer van het audit-log. De resterende verschillen zitten in
> database-specifieke implementaties van store/lease en de **audit-service-implementatie**
> (per tier, niet gedeeld) en de **autorisatie-wrapper** (zie het kader verderop, #1272). Een
> nieuw endpoint hoort op beide tiers tegelijk: `scripts/ci/check-tier-pariteit.sh` vergelijkt de
> HTTP-routes van beide tiers en laat de build falen bij een route die er maar op één staat
> (uitzonderingen met reden in `scripts/ci/tier-pariteit-allowlist.txt`; daar staan nu alleen de
> twee sync-trigger-naamsvarianten). Let op wat die guard **niet** ziet: hij vergelijkt routes,
> geen implementatiekeuzes — de encrypted tokenopslag en de audit-service vallen er dus buiten. De eerder
> gedocumenteerde premisse dat de SQL Server-tier "rollback-only" zou zijn, is ingetrokken — beide
> tiers zijn gelijkwaardig.

> **Sinds de review van #1122 gelden twee vaste plekken** (zie ook AGENTS.md, "Architectuurinvarianten", en de normatieve regels hieronder):
> - `FunctionApp.Postgres/Sportlink/SportlinkEndpointSupport.cs` en zijn tegenhanger
>   `FunctionApp/Sportlink/SportlinkEndpointSupport.cs` — toggle+EgressGuard-controle,
>   statusvertaling, rolnaam, audit-afronding (`RondMutatieAfAsync`), timer-preamble
>   (`ClientVoorTimer`). Alle Sportlink-Functions en -timers van die tier gebruiken hem. Sinds
>   #1266 is de tier-onafhankelijke *beslislogica* daarvan verhuisd naar
>   `Planner.Shared/Integrations/SportlinkClub/SportlinkEndpointCore.cs` (o.a.
>   `BepaalAuditResultaat`, `IsDryRunActief`, `WarmupVooruitkijkDagen`). Sinds **#1271** is ook de
>   *orkestratie zelf* (routeparameter/DI-plumbing, de vertaling naar `IActionResult`) gedeeld, in
>   `Planner.Endpoints/Sportlink/SportlinkEndpointSupportCore.cs` — een apart project omdat deze
>   laag wél op ASP.NET Core en de Azure Functions Worker leunt, iets wat `Planner.Shared` bewust
>   niet doet (zelfde grens als `ThemeCore` #1248 en `FeedbackCore` #1130). Tier-specifieke stukken
>   (instellingenlezer, `EgressGuard`, `EasyAuthHelper`/`AdminEndpoint`) gaan als delegate mee; de
>   twee `SportlinkEndpointSupport`-bestanden zijn nu een dun omhulsel om die gedeelde orkestratie,
>   zodat een tierwissel niet stilzwijgend ander gedrag oplevert. Bewust **niet** meeverhuisd:
>   `ISportlinkMutationAuditService` bestaat nog als twee identieke interfaces (één per
>   tier-namespace) — dat samenvoegen raakt `Program.cs` van beide tiers en is een aparte afweging.
>   De drie `*Function.cs`-bestanden die de rest van de bij #1271 gemeten duplicatie vormen
>   (`SportlinkMatchFunction.cs`, `SportlinkClubMatchFunction.cs`,
>   `SportlinkChangeRequestFunction.cs`) zijn nog niet naar deze vorm geport.
> - `BlazorAdmin/Shared/SportlinkMatchPanel.razor(.cs)` — het paneel per wedstrijd op Planning en
>   Veld optimalisatie (#1361; vóór die splitsing Dagplanning);
>   `BlazorAdmin/Models/SportlinkActieStatus.cs` — status van één actie plus de ene vertaling van
>   mutatieresultaat naar melding (`Verwerk`); `BlazorAdmin/Shared/Melding.razor` toont hem. De
>   vier extensie-pagina's hebben een code-behind en geen `@code`.
> - **Normatieve regels (harde regels; de samenvatting staat in `AGENTS.md`).** Een nieuw
>   Sportlink-endpoint of een nieuwe Sportlink-timer roept altijd `SportlinkEndpointSupport` aan; een
>   eigen kopie van de toggle+EgressGuard-controle, de statusvertaling, de rolnaam of de
>   audit-afronding is een architectuurschending — dat was precies de toestand vóór #1122 (zes kopieën
>   van de toggle-check, drie van de statusvertaling). Elke Sportlink-aanroep in `Planner.Shared` loopt
>   via `SportlinkClubClient.ExecuteWithTokenRetryAsync` en `ZetSportlinkHeaders`, nooit via een eigen
>   token-refresh/401-retry of eigen Navajo-headers. In Blazor hebben de extensie-pagina's
>   (`Planning`, `Wijzigingsverzoeken`, `OefenwedstrijdAanmaken`, `SportlinkExtensieInstellingen`)
>   **geen `@code`-blok**: de logica staat in een code-behind (`<Pagina>.razor.cs`, `public partial
>   class`, `[Inject]` in plaats van `@inject`). De status van een actie (bezig/melding/fout/dry-run)
>   is altijd een `SportlinkActieStatus`, met `Verwerk(...)` als de ene plek die een mutatieresultaat
>   naar een melding vertaalt, en `<Melding Status="..." />` toont hem. Een nieuwe
>   `Dictionary<long, bool> _xBezig` of een `@code`-blok in zo'n pagina is een architectuurschending.
> - In `Planner.Shared`: `SportlinkClubClient.ExecuteWithTokenRetryAsync` is het ene
>   token-refresh/401-retry-pad voor lezen én schrijven; `ZetSportlinkHeaders` de ene plek voor de
>   Navajo-headers; `TokenEndpoint`/`ClientId` zijn publiek. Nieuwe volledige sessies lopen via de
>   automatische-loginprovider.
- ~~`Tools/SportlinkTokenCapture`~~ — verwijderd in #1411; één automatische-loginroute is leidend.
- ~~token-gerelateerde `scripts/dev/Invoke-Sportlink*`-spikes~~ — verwijderd in #1411.
- `FunctionApp.Postgres/Admin/SportlinkExtensieRollenFunction.cs` +
  `FunctionApp/Admin/SportlinkExtensieRollenFunction.cs` — rol↔serviceaccount-koppelingsstatus
  (#988), geen live Sportlink-aanroep, op beide tiers. De oude `PUT
  .../rollen/{rolNaam}/token`-route is verwijderd in #1411.
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
  beide tiers gebruiken `ISportlinkAutoLoginStore` met encrypted credentials en tokens; de
  oude tokenstores en handmatige capture/uploadroute zijn verwijderd. Zie
  [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md).
- `Planner.Shared/Integrations/SportlinkClub/SportlinkMutationGuard.cs` (#998) — pure guardrail:
  staat een mutatie alleen toe bij `IsHomeMatch=true`, de bijbehorende Sportlink-permissievlag, én
  blokkeert altijd bij `IsCanceledMatch=true` of `IsConceptMatch=true`. `MatchStatus` wordt bewust
  NIET hard afgedwongen (bijv. op `SCHEDULED`) — die waarde wordt sinds #998 wel uitgebreid
  meegelogd in de audit (zie hieronder), zodat er eerst een seizoen aan echte data verzameld wordt
  vóórdat die eventueel een harde blokkade wordt.
> **Autorisatie: `Wedstrijdzaken` óf `admin`, plus `admin`/`user` voor de GUI-laag, op beide tiers
> (#1272, herzien bij #1376).** Elk Sportlink-endpoint loopt via één gedeelde vorm:
> `SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync` doet eerst `RequireWedstrijdzaken` en
> daarna `AdminEndpoint.ExecuteAsync` (met zijn eigen `RequireRole(req, "admin", "user")`). Beide
> tiers gebruiken dezelfde wrapper. Sinds #1376 is `RequireWedstrijdzaken` zelf een OR:
> `RequireRole(req, "Wedstrijdzaken", "admin")` — de facto resulterende toegang:
> - `admin` (met of zonder aparte `Wedstrijdzaken`-toewijzing) → toegestaan.
> - `user` + `Wedstrijdzaken` (geen `admin`) → toegestaan, ongewijzigd sinds #988/#991.
> - alléén `user`, of geen van beide rollen → geweigerd, ongewijzigd.
>
> Tot #1272 gaf de Postgres-tier `requireRole:` mee aan `AdminEndpoint.ExecuteAsync`, waar het de
> admin-controle *verving*. Dat sprak §3.4 hierboven tegen ("bovenop de bestaande admin-toegang")
> en leverde een recht op dat alleen buiten de applicatie om bruikbaar was: `App.razor` poort de
> hele Admin GUI op `admin` of `user`, dus iemand met alléén `Wedstrijdzaken` kon de interface niet
> laden maar de mutatie-endpoints wél rechtstreeks aanroepen. De parameter is verwijderd, niet
> alleen ongebruikt gelaten — een optionele parameter die stilzwijgend een autorisatiecontrole
> vervangt, wordt vanzelf een tweede keer gebruikt.

- `FunctionApp/Sportlink/` + `FunctionApp.Postgres/Sportlink/` (#998) — per-tier, niet-gedeelde
  `ISportlinkMutationAuditService`-implementatie; logt vóór én na elke toekomstige mutatie in
  `dbo.SportlinkMutationAudit`/`public.sportlinkmutationaudit`. Bewaartermijn sinds #1114: default
  365 dagen, instelbaar via `AppSettings.SportlinkMutationAuditBewaarDagen`, maandelijks opgeruimd
  door `CleanupSportlinkMutationAuditFunction` op beide tiers (zie `SECURITY.md`).
- `FunctionApp.Postgres/Integrations/SportlinkClub/SportlinkPublicMatchIdRepository.cs` (#991) —
  de #987-reverse-lookup-cache (`public.sportlinkpublicmatchidcache`, migratie
  `014_sportlink_club_postgres_tokenstore.sql`) en de `his.matches`-opzoeking (wedstrijdcode →
  wedstrijdnummer/datum) die de reverse-lookup nodig heeft.
- `FunctionApp.Postgres/Sportlink/SportlinkMatchFunction.cs` — `GET
  /api/sportlink/match/{wedstrijdcode}` (#991), het eerste endpoint met `RequireWedstrijdzaken`
  **bovenop** `RequireAdmin` (#1272 — tot dan stond hier "i.p.v.", wat §3.4 tegensprak). Verbindt de reverse-lookup-cache, de token-store en de
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
  **Sinds #994 ook `PUT .../officials` (sinds #1319 live bevestigd, was scaffolding/code-gelockt):** officials
  (scheidsrechter/AR1/AR2) toewijzen via `AssignOfficialsAsync`/`PutMatchOfficialsAsync`
  (`competition/match/official/MatchOfficialsAction`). Endpoint én requestbody
  (`OfficialsToBeAssigned: [{OfficialPosition, PersoonId}]`) zijn NOOIT live gezien — zie
  `SportlinkOfficialToewijzing.cs`. `VerrijkOfficialsResultaat` is de taakspecifieke uitbreiding op
  de generieke responsparser: als één official in de respons een `ValidationDescription` heeft,
  wordt `IsSuccess=false` gezet (Sportlinks "opgeslagen met fouten"), ook al is de HTTP-status 200.
  De Blazor-UI biedt bewust alleen een losse tekstinvoer per positie (relatiecode/persoons-ID) —
  géén zoekfunctie, geen namen (AVG, §5).
  **Sinds #1340 wordt die tekstinvoer voorafgevuld** met de HUIDIGE relatiecode (indien Sportlink
  er al één had), op dezelfde manier als #1339's FieldId/FieldSize-prefill: `GET
  .../sportlink/match/{wedstrijdcode}` geeft nu ook `scheidsrechterRelatieCode`/`ar1RelatieCode`/
  `ar2RelatieCode` terug (`SportlinkMatch.MatchOfficials`, Planner.Shared) — uitsluitend die twee
  velden uit `matchOfficials`, nooit naam/geboortedatum/foto-URL (zie het incident hieronder in
  §5). Genuld door de server als de rol geen `ScheidsrechterFeatureToegestaan` heeft (dezelfde
  #1341-gate als de toewijs-actie zelf, `SportlinkRolFeature.VoegToestemmingenToe`). **Het exacte
  JSON-veldnaam voor de relatiecode in `matchOfficials` is, net als `OfficialsToBeAssigned`
  hierboven, NOOIT live geverifieerd** — zie de TODO bij `SportlinkMatchOfficial.RelatieCode` en
  §8 hieronder voor de nog niet bevestigde AVG-vraag die bij deze prefill hoort.
- **Sinds #995 ook `PUT .../change-request` — wijzigingsverzoek datum/tijd/accommodatie, ALLEEN
  stap 1 (valideren), sinds #1319 live bevestigd (was code-gelockt):** dit is de enige mutatiesoort die een ECHTE tegenstander
  raakt (Sportlink stuurt bij bevestiging een goedkeuringsverzoek naar de tegenstander) — zie het
  `NIET VERDER BOUWEN`-markeringscomment op `SportlinkClubClient.RequestMatchChangeAsync` en
  `ISportlinkClubClient`. Zelfde onderliggende endpoint als `.../field`
  (`competition/match/UpdateMatchDetails`), maar bewust GEEN parametrisering van
  `PutMatchDetailsAsync` — een eigen `PutMatchDetailsChangeRequestAsync`/
  `BuildMatchChangeRequestBody` kopieert de structuur (verse snapshot ophalen → envelope met
  alléén `MatchDate`/`StartTime`/`FacilityId`/`MatchChangeRequestRemarks` overschreven), zodat een
  wijziging aan dit nog-onbevestigde pad #993's live-bevestigde veld-wijziging nooit kan raken.
  `ParseMatchChangeValidatie` leest Sportlinks (ONBEVESTIGDE) `ConfirmationNeeded`-veld defensief
  uit (`ValidationResultMessages` kan kale strings of objecten met `Message`/`Description`
  bevatten) — maar wordt in de praktijk nooit aangeroepen zolang de lock actief is: een dry-run
  slaat de echte HTTP-aanroep over, dus is er nooit een respons om te parsen. `SportlinkMatchChangeValidatie`/
  `SportlinkMatchChangeRequestResult` staan bewust LOS van het gedeelde `SportlinkMutationResult` —
  dat generieke type blijft ongewijzigd voor alle andere mutaties. `SportlinkMutationGuard` kreeg
  een nieuwe soort `DatumTijdAccommodatie` die uitsluitend op `IsHomeMatch` + de generieke niet-
  afgelast/niet-concept-checks controleert (geen specifieke `IsXxxAllowed`-vlag bestaat hiervoor bij
  Sportlink — TODO in de guard). De Blazor-UI toont het formulier alleen bij `IsHomeMatch` en biedt
  bewust GEEN bevestigknop, ook geen disabled-variant (dat zou een niet-gebouwde stap 2 suggereren).
- **Sinds #1339 prefill van het huidige veld + veld-dropdown op onze eigen veldnaam bij `GET
  .../match/{wedstrijdcode}`.** `SportlinkMatch.Field` (nieuw, spiegelt het al langer intern
  gebruikte `SportlinkClubClient.SportlinkFieldRaw`) geeft het huidige `FieldId`/`FieldSize` van de
  wedstrijd mee — dit stond al live bevestigd in dezelfde Match-GET-respons (2026-09-06, #1047),
  maar werd tot nu toe alleen intern gebruikt bij een veldwijziging, nooit teruggegeven aan de UI.
  `SportlinkMatchFunction.BouwPaneelResponse` (beide tiers) voegt daar `veldOpties`
  (per actief club-veld een VOORSTEL-`FieldId`) en `subpositieOpties` (per subpositie een
  voorgestelde `FieldSize`) aan toe — berekend door `SportlinkFieldIdBuilder`
  (`Planner.Shared`), géén Sportlink-gegeven. **Dit is een voorstel, geen bevestigde resolutie:**
  het patroon `"{FacilityId}-OUTDOOR_FIELD-{VeldNummer}"` is bevestigd voor precies één
  combinatie (de vaste testwedstrijd, veld 6). Of Sportlinks eigen veldnummering voor élke club
  exact gelijk loopt aan onze `VeldNummer`-kolom is NIET bevestigd — de Blazor-tekstvelden blijven
  daarom altijd bewerkbaar, de dropdown vult ze alleen voor. Géén nieuwe Sportlink-aanroep,
  géén nieuwe database-tabel — de veld-dropdown gebruikt de bestaande `public.velden`/`dbo.Velden`
  via een nieuwe leesquery in `SportlinkClubMatchRepository.GetActieveVeldenAsync`.
- `FunctionApp.Postgres/Sportlink/SportlinkTokenKeepAliveTimerFunction.cs` — uur-timer die
  `ISportlinkClubClient.VerversTokenAsync` aanroept voor elke rol met een opgeslagen token, ook
  zonder enige gebruikersactie. **Waarom nodig:** Keycloak deactiveert een refresh-token na een
  periode zonder gebruik (`invalid_grant: "Token is not active"`, live vastgesteld 2026-09-05),
  ondanks dat de 6-uurs `refresh_expires_in` nog niet verstreken was — een lui verversende client
  (alleen bij een echte GUI-actie) is dus niet genoeg. Bestond tot #1266 alleen op de Postgres-tier;
  sinds #1266 staat de tegenhanger in `FunctionApp/Sportlink/SportlinkTokenKeepAliveTimerFunction.cs`
  (zelfde uur-cron). Eén tierverschil, bewust: die tier bewaart refresh-tokens in Function
  App-instellingen (#1020), dus "welke rollen zijn gekoppeld?" is daar een vraag aan
  `ISportlinkClubTokenStore` in plaats van aan een DB-tabel.
- `FunctionApp.Postgres/Sportlink/SportlinkPublicMatchIdWarmupTimerFunction.cs` (#1017) — dagelijkse
  timer die de PublicMatchId-cache vooraf vult voor de eerstkomende dagen (vandaag + 2), gegroepeerd
  per datum (één `MatchProgramOverview`-aanroep per dag, niet per wedstrijd — zie
  `ISportlinkClubClient.GetMatchProgramOverviewAsync`). Een cache-miss buiten dat venster valt nog
  steeds terug op de bestaande synchrone lookup in `SportlinkMatchFunction`, geen harde fout.
  SQL Server-tegenhanger sinds #1266:
  `FunctionApp/Sportlink/SportlinkPublicMatchIdWarmupTimerFunction.cs`. De horizon (vandaag + 2)
  staat als `SportlinkEndpointCore.WarmupVooruitkijkDagen` in `Planner.Shared`, zodat een tierwissel
  niet stilzwijgend een ander venster oplevert.

  **#1387 (voorheen wél een harde fout bij een cache-miss):** de synchrone fallback-lookup gebruikte
  tot deze fix de globale `HttpClient`-timeout van 15 seconden — tegen de gedocumenteerde 12+
  seconden latency van `MatchProgramOverview` een marge van bijna nul. Iedere extra vertraging
  (Sportlink-belasting, netwerk) liet de aanroep timeouten, wat via `SportlinkClubCallStatus.NetwerkFout`
  naar een generieke HTTP 502 "Sportlink is momenteel niet bereikbaar" leidde — ononderscheidbaar van
  een échte Sportlink-fout. Sinds #1387: `SportlinkClubClient` bepaalt per endpoint een eigen,
  per-aanroep timeout (`ReverseLookupCallTimeout` = 30s voor uitsluitend `MatchProgramOverview`,
  `DefaultCallTimeout` = 15s voor de rest — losgekoppeld van de `HttpClient`-brede timeout, die nu de
  .NET-default van 100s is als buitenste net); elke **lees**aanroep krijgt bovendien één begrensde
  retry bij een timeout/netwerkfout of een 5xx van Sportlink zelf (nooit bij een 4xx — dat is een
  inhoudelijke afwijzing). `SportlinkEndpointCore.VertaalStatusNaarFout` geeft een timeout/netwerkfout
  sindsdien ook een eigen tekst en HTTP 504 in plaats van de generieke 502-tekst, zodat een operator
  kan zien of Sportlink zelf een fout gaf of dat de aanroep gewoon (nog) te traag was.

  **#1417 — retry-beleid per aanroeptype (correctie op #1387).** De transiënte retry gold tot #1417
  voor élke aanroep, dus ook voor PUT/POST. Dat is onveilig: een timeout of gateway-5xx *nádat*
  Sportlink de aanvraag al verwerkt heeft, is voor de client niet te onderscheiden van "nooit
  aangekomen". Herhalen zou dan een tweede oefenwedstrijd aanmaken (`CreateClubMatchAsync`, POST;
  het verwijderpad van #1440 staat nog hard op dry-run) of een wijzigingsverzoek tweemaal bij een echte tegenstander
  afleveren (`RequestMatchChangeAsync`). Sinds de fix voor #1417 kiest elke call site van
  `ExecuteWithTokenRetryAsync` expliciet een `RetryBeleid` — bewust zonder default:

  | Beleid | Aanroepen | Transiënte retry (timeout/5xx) | 401-re-auth-retry |
  |---|---|---|---|
  | `Lezen` | alle GET's (`GetMatchAsync`, `GetMatchProgramOverviewAsync`, `GetChangeRequestsAsync`, picklists, …) en het token-refreshpad | **één keer**, na 2 s | ja |
  | `Mutatie` | `UpdateDressingRoomsAsync`, `UpdateFieldAsync`, `AssignOfficialsAsync`, `RequestMatchChangeAsync`, `ActOnChangeRequestAsync`, `CreateClubMatchAsync`, `DeleteClubMatchAsync` (#1440) | **nooit** | ja — een 401 is een expliciete afwijzing vóór verwerking, dus herhalen met een vers token is veilig |

  Een mutatie die op een netwerkfout strandt, krijgt via `VertaalStatusNaarFout(status, isMutatie: true)`
  (gebruikt door `BepaalMutatieAfronding`) de melding `MutatieNetwerkFoutMelding`: "controleer eerst
  in Sportlink of de actie al is doorgevoerd" — niet het "probeer opnieuw"-advies van het leespad.
  Tegelijk is een inconsistentie in `PutMutationAsync` gedicht: een 5xx *mét* parseerbare JSON-body
  gaf `Status = Ok`/`IsSuccess = false` terug, een 5xx zonder JSON al `SportlinkFout`; nu is elke
  5xx uniform `SportlinkFout` mét statuscode. Tests: `CreateClubMatchAsync_TimeoutOpPost_GeenTweedePost`,
  `RequestMatchChangeAsync_Http502OpPut_GeenTweedePut`, `AssignOfficialsAsync_TimeoutOpPut_GeenTweedePut`,
  `UpdateDressingRoomsAsync_Http503_GeenTransienteRetryOpMutatie` (voorheen bewees die test het
  omgekeerde) en `UpdateDressingRoomsAsync_401OpMutatie_ReAuthRetryBlijftBestaan`.
- `FunctionApp.Postgres/Sportlink/SportlinkChangeRequestFunction.cs` (#996) — `GET
  /api/sportlink/change-requests` + `PUT .../{publicRequestId}/action`. Niet wedstrijdcode-
  gescoped (Sportlinks `MatchChangeRequests`-endpoint levert alles voor het gekoppelde
  serviceaccount in één keer) en bewust ZONDER `SportlinkMutationGuard`-check: die guard bewaakt
  onze eigen wedstrijd-mutatie-vlaggen, niet het afhandelen van een verzoek van een tegenstander.
  Audit-logging blijft wel verplicht. `ActOnChangeRequestAsync` haalt `PublicPersonId` van het
  service-account zelf op via `user/UserInfo` — de aanroeper hoeft dat niet te kennen.
  **#1111:** de GET verrijkt elk verzoek met `Wedstrijd` (`SportlinkWedstrijdContext`: nummer,
  teams, datum, tijd, accommodatie) via `public.sportlinkpublicmatchidcache` → `his.matches`
  (`SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync`) — bewust NIET via
  extra velden uit `MatchChangeRequests`: die zijn nooit met een netwerktrace bevestigd, en zo'n
  trace maakt een agent nooit (§4.4). Geen cache-treffer = `null`, het verzoek blijft staan. **#1439
  (contract bevestigd uit Sportlinks eigen frontend-bundle):** de respons is
  `{ "ChangeRequests": [...] }`; het statusveld heet `ChangeRequestStatus` (niet `requestStatus` —
  `RequestStatus` bleef daardoor altijd leeg) met de waarden APPROVED, CONFIRM_AWAY, CONFIRM_HOME,
  CONFIRM_UNION, DENIED, MATCH_FINALIZED, REVOKED; `IsIncomingRequest` (bool) scheidt inkomend van
  uitgaand. `SportlinkChangeRequestStatusGroep` vertaalt dit naar Sportlinks vier filtergroepen:
  Openstaand (CONFIRM_*), Akkoord (APPROVED, MATCH_FINALIZED), Afgewezen, Ingetrokken; onbekende
  waarden worden `UNKNOWN`. De Blazor-pagina filtert standaard op Openstaand, toont per filter de
  secties Inkomend en Uitgaand, en biedt goedkeuren/afwijzen alleen bij openstaand + inkomend.
  **#1464:** de lijstrespons draagt per verzoek ook `ExternalMatchId` (wedstrijdnummer, als getal),
  `HomeTeam.TeamName` en `AwayTeam.TeamName` — Sportlinks eigen tabelkolommen, uit dezelfde bundle.
  Die gaan als `ExternalMatchId`/`Thuisteam`/`Uitteam` mee en vullen de kolommen wanneer de eigen
  context (`Wedstrijd`) ontbreekt; eigen context blijft voorrang houden.
- `FunctionApp.Postgres/Sportlink/SportlinkClubMatchFunction.cs` (#997) — `POST
  /api/sportlink/club-match` (aanmaken, sinds #1319 live bevestigd, was code-gelockt) + `GET
  .../club-match/picklists` (Teams + Location, read-only, echt aangeroepen). **POST — geen guard
  mogelijk vóór aanmaak (eigen-DB-checks i.p.v. Sportlink-permissievlag):** structureel anders dan
  `SportlinkMatchFunction`/`SportlinkChangeRequestFunction` — er is vooraf GEEN bestaande wedstrijd,
  dus geen `PublicMatchId`, geen `wedstrijdcode`, geen `SportlinkMatch` om te guarden. In plaats van
  `SportlinkMutationGuard` gelden alleen ONZE EIGEN regels: de `sportlinkExtensionEnabled`-toggle +
  `EgressGuard.ExternalIntegrationsAllowed()` (zelfde patroon als
  `SportlinkChangeRequestFunction`'s eigen toggle/egress-check, om een vergelijkbare reden — geen
  mutatie op onze eigen wedstrijdgegevens). Audit-model past niet 1-op-1: `SportlinkMutationAuditEntry`
  vereist een verplicht, niet-leeg `PublicMatchId`-veld dat bij het aanmaken nog niet bestaat — de
  Pending-rij gebruikt daarom de placeholder `"NIEUW"`, met een gegenereerde GUID in `CorrelationId`
  om Pending- en Voltooid-rij te koppelen. Het écht opslaan van het teruggekregen `PublicMatchId`
  vereist een uitbreiding van `ISportlinkMutationAuditService.VoltooiAsync` (een extra optionele
  parameter, raakt beide tiers) — bewust NIET gebouwd in deze ronde (`// TODO` in de broncode),
  niet nodig zolang dit pad toch altijd `"DryRunLocked"` teruggeeft. **Bewust NIET gebouwd (toekomstig
  werk):** uitslag vastleggen (`ClubMatchScore`), de drie overige
  ondersteunende endpoints (`ClubMatchDefaults`, `PickListsMatchInformation`,
  `codetable/AgeClassList`), en een "vrij tijdslot"-concept in de Dagplanning-Gantt — het formulier
  (`BlazorAdmin/Pages/OefenwedstrijdAanmaken.razor`) staat los van de Gantt.
  **Herzien in #1116 — de server vertaalt, de gebruiker kiest alleen.** Het formulier stuurt nog
  slechts `TeamNaam` (keuzelijst uit `public.teams`), `Tegenstander` (vrije tekst), `VeldNummer`
  (keuzelijst uit `public.velden`), datum/tijd/duur en een optionele omschrijving; de picklist-knop
  en de vrije ID-velden zijn weg. `SportlinkClubMatchRepository` koppelt de teamnaam via de
  **gevalideerde aliassen** (`public.teamaliassen` → `his.teams.teamcode`, gevuld door
  `TeamCanonicalisatieService` na elke sync) aan het team-ID dat de Sportlink-dataservice zelf
  hanteert — bewust géén eigen naamlogica (docs/ARCHITECTUUR-TEAMRESOLUTIE.md, regel 1 en 4).
  Lokale data: `thuisteamid` in `his.matches` is voor 103 van de 108 eigen thuisteams exact die
  `teamcode`; 98 van de 104 actieve teams krijgen zo precies één ID, geen enkel team een
  dubbelzinnig ID; de 6 zonder ID zijn teams waarvan alleen de lokale schrijfwijze bekend is
  (bijv. `JO13-2` naast het aparte canonieke team `O13-2JM`). Bij 0 of >1 verschillende ID's
  blijft `PublicHomeTeamId` leeg met een waarschuwing. De leeftijdscategorie van het team gaat mee
  als `AgeClassCode`. `FacilityId` komt uit de club-instelling `accommodatie`,
  op naam opgezocht in `PickListsLocation` (read-only GET, per club één uur in-memory gecachet;
  eerst exact, anders één unieke gedeeltelijke match — meerdere treffers → leeg). Elke mislukte
  vertaling wordt een `Waarschuwing` in de respons, geen fout: het pad is toch code-gelockt en de
  beheerder moet zien wat er (gesimuleerd) mee zou gaan. **Open vraag, pas te beantwoorden met de
  netwerktrace:** hanteert Sportlink Club voor `PublicHomeTeamId` hetzelfde numerieke team-ID als
  de dataservice, of een publiek string-ID zoals `PublicMatchId` (`M...`)? Het diagnostische
  `GET .../club-match/picklists` blijft daarvoor bestaan. Blijkt het een ander ID, dan is het
  alternatief een eenmalige koppel-sync die `teams` een kolom `sportlinkpublicteamid` geeft — bewust
  níet vooruit gebouwd (migratie + handmatige productie-ronde voor kolommen die niemand kan vullen).
- **ClubMatch-contract live bevestigd (#1427, 01-10-2026).** De eerste echte aanroep vanuit deze
  app gaf HTTP 602 binnen ~20 ms: de aangenomen body (datum+tijd samengevoegd, dataservice-teamcode
  als `PublicHomeTeamId`, tegenstander als team-ID) klopte niet. "Live bevestigd" bij #1319 betekende
  voor dit pad alleen dat de code-lock was opgeheven (zie de nuance bij #1380). De eigenaar heeft
  daarna met een console-script in Sportlink Club zelf (alleen vorm en voorbeeldwaarden, geen tokens of
  headers met geheimen) vastgelegd wat Sportlinks eigen formulier doet:
  1. vier read-only GETs: `ClubMatchDefaults` (o.a. het eerstvolgende `ExternalMatchId`, standaardteam,
     -veld, -leeftijdscategorie en -spelactiviteit), `PickListsTeams` (`{ ClubTeams: [{ Id: "T…",
     TeamName, ExternalSportId, SportTag, … }] }`), `PickListsLocation?SearchClubId=`
     (`{ Facilities: [{ FacilityId, NormalizedName, IsDefault, Fields: [{ SubFacilityId, Name, … }] }] }`)
     en `PickListsMatchInformation` (`{ Activities: [{ IdTag, Description }], AgeClasses: [{ Id,
     Description }] }`);
  2. de POST met `HomeTeam`/`AwayTeam` als tekst, het eigen `T…`-team-ID als `PublicHomeTeamId` én
     `PublicAwayTeamId`, `MatchDate` + `StartTime` apart, `SportIdTag`, `IsHomeMatch`,
     `SubFacilityId`, `FieldSize` "1.0" en `FieldOffset` "0" — respons HTTP 200 met `PublicMatchId`.

  De vertaling staat in `Planner.Shared/Integrations/SportlinkClub/ClubMatchAanvraagBouwer.cs`
  (team op naam, alleen een unieke treffer; veld op naam, want `SubFacilityId` volgt geen vast patroon
  — live is "veld 5" `…-OUTDOOR_FIELD-6`; leeftijdscategorie `JO10` → "Onder 10 (M)"; spelactiviteit =
  `ExternalSportId/SportTag` van het team). Wat niet af te leiden is valt terug op
  `ClubMatchDefaults` met een waarschuwing; een onbekend team of veld is een 400. Vrije tekst gebruikt
  het standaardteam — exact wat Sportlinks eigen formulier doet.
- **Wedstrijd aanmaken — verbeteringen (#1437).** Gebouwd op beide gebouwde tiers:
  1. **Velddeel.** `FieldSize` komt uit de keuze Heel/Half/Kwart/Achtste veld (`1.0`/`0.5`/`0.25`/`0.125`,
     `FieldOffset` blijft `0`); `ClubMatchVelddeel` (Planner.Shared) is de enige plek met die waarden.
     **AANNAME, nog niet live bevestigd:** alleen `"1.0"` is met een echte aanmaak vastgesteld (#1427).
     De notatie voor een deel van het veld is daaruit geëxtrapoleerd, en `FieldOffset 0` als "eerste
     deel" idem. Eerst een echte proefwedstrijd met half veld aanmaken (dry-run uit) en in Sportlink
     Club controleren of het veld goed bezet is, vóór dit als bevestigd geldt.
  2. **Wedstrijdnummer.** Niet meer Sportlinks voorstel (`ClubMatchDefaults.ExternalMatchId`) maar een
     eigen nummer `YYMMDD` + tweecijferig volgnummer per speeldag en club (26100201, 26100202, …) uit de
     tabel `wedstrijdnummerteller` (zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` §77). Het nummer wordt pas
     gereserveerd als validatie en bouw geslaagd zijn, vlak vóór de Sportlink-aanroep; ook een
     dry-run verbruikt een nummer, en een door Sportlink afgewezen aanmaak laat een gat achter. Meer dan
     99 per dag → 400. Maakt iemand in Sportlink Club zelf een wedstrijd aan met hetzelfde nummer, dan
     voorkomt deze teller dat niet — hij kent alleen onze eigen aanmaken.
  3. **Spelactiviteit** is een clubinstelling (`SportlinkSpelactiviteit` in AppSettings, scherm
     "Sportlink Web Extension"): de omschrijving ("Veld - Zaterdag") of de IdTag
     ("SOCCER-VE-AL/SATURDAY"), hoofdletterongevoelig. Gevuld én gevonden wint altijd; leeg is het
     oude gedrag (het team, anders Sportlinks standaard); gevuld maar niet gevonden is het oude gedrag
     met een waarschuwing.
  4. **Leeftijdscategorie** is een keuzelijst met Sportlinks eigen lijst; de voorinvulling is die van het
     gekozen team (`AgeClassOmschrijving` → Sportlink-`Id`), anders "— Sportlink-standaard —". De
     gekozen `AgeClassCode` moet in de lijst staan en wint van de categorie van het team.
  5. **`GET /api/sportlink/club-match/formulier`** (Wedstrijdzaken-poort) levert die voorinvulling:
     per team Sportlink-categorie, duur en velddeel uit de speeltijden, plus Sportlinks lijst. De
     speeltijden-API is admin-only, vandaar een eigen endpoint. Is Sportlink niet bereikbaar, dan komen de
     teamgegevens zonder lijst terug (`SportlinkBeschikbaar: false`). De samenstelling staat in
     `ClubMatchEndpointCore.BouwFormulierAsync`; de tiers doen alleen de queries.
  6. **Pagina.** Team en Tegenstander eerst, dan datum/tijd/duur, veld/velddeel, leeftijdscategorie en
     omschrijving. Een team kiezen vult duur, leeftijdscategorie en velddeel voor (aanpasbaar);
     **Leegmaken** zet alles terug (`OefenwedstrijdFormulierState`); na een echte aanmaak staat er een
     link "Open wedstrijd in Sportlink Club" (`https://club.sportlink.com/competition-affairs/match-details/{PublicMatchId}`).
- **Dry-run-modus (#998).** De vertakking zit in `SportlinkClubClient.PutMutationAsync` — het ÉNE
  punt waar alle **zes** mutatiepaden doorheen lopen (kleedkamers, veld, officials,
  wijzigingsverzoek, change-request-actie en de ClubMatch-POST) — niet per tier/endpoint apart. Dat garandeert dat token-refresh en de voorbereidende snapshot-/UserInfo-
  GETs ook in dry-run écht gebeuren (realistische simulatie); alleen de daadwerkelijke PUT/POST
  wordt overgeslagen. `SportlinkClubClient` krijgt hiervoor een `Func<bool> isDryRun`-delegate in de
  constructor (zelfde ontkoppelingspatroon als `ISportlinkClubTokenStore` — geen settings-/DB-
  afhankelijkheid in `Planner.Shared`). `FunctionApp.Postgres/Program.cs` geeft een delegate mee die
  bij **elke** aanroep opnieuw `PostgresAppSettings.GetSetting("sportlinkDryRun")` leest (niet één
  keer bij opstarten) — de toggle op Instellingen heeft dus direct effect, zonder herstart, omdat
  `AdminSettingsPut` na elke wijziging `PostgresAppSettings.LoadSettingsAsync` opnieuw aanroept.
  `FunctionApp/Program.cs` (SQL Server-tier) gaf tot #1266 hard `isDryRun: () => true` mee, op grond
  van de inmiddels ingetrokken premisse dat die tier geen mutatiepaden zou krijgen. Sinds #1266 leest
  hij dezelfde instelling, via dezelfde gedeelde, fail-safe regel
  (`SportlinkEndpointCore.IsDryRunActief`): alles behalve een expliciet geladen `"0"` blijft dry-run.
  `SportlinkMutationResult` kreeg er een derde veld `IsDryRun` bij; het audit-resultaat wordt bepaald
  door de gedeelde helper `SportlinkEndpointCore.BepaalAuditResultaat` in `Planner.Shared`
  (`DryRun` gaat vóór `IsSuccess`, want die is bij dry-run altijd `true`).
  `SportlinkEndpointSupport.BepaalAuditResultaat` en `SportlinkMatchFunction.BepaalAuditResultaat`
  zijn op beide tiers nog slechts doorgeefluiken naar die ene regel.
- **Code-niveau `forceDryRun`-lock (#994), onafhankelijk van de instelling hierboven.** Naast
  `sportlinkDryRun` (een bewuste, per-club instelling voor BEVESTIGDE mutaties) bestaat sinds #994
  een tweede, harde vergrendeling voor een mutatie waarvan de requestbody nooit met een
  netwerktrace bevestigd is (de eerste: officials toewijzen, #994; ook gebruikt door #995's
  wijzigingsverzoek — `UpdateMatchDetailsChangeRequestLiveBevestigd` — en #997's
  oefenwedstrijd-aanmaak — `ClubMatchLiveBevestigd`).
  `PutMutationAsync` kreeg een `forceDryRun`-parameter (`if (forceDryRun || _isDryRun())`) — de club
  kan dit NIET uitzetten via Instellingen, ongeacht de stand van `sportlinkDryRun`. Elke
  mutatiemethode voor zo'n onbevestigd endpoint geeft `forceDryRun: !XyzLiveBevestigd` mee, met een
  bijbehorende constante die bij een onbevestigd endpoint op `false` staat. De vier huidige
  `LiveBevestigd`-constanten staan inmiddels op `true`; onderstaande uitleg beschrijft het mechanisme —
  grep-baar en pas door een mens (nooit een agent, §4.4) op `true` te zetten in een aparte,
  reviewbare PR ná een live trace. De log-regel bij deze tak vermeldt expliciet
  "code-lock, body niet live bevestigd" (anders dan de generieke dry-run-logregel), en
  `SportlinkMutationResult`/`SportlinkMutatieResultaatDto` kregen er een vierde veld
  `IsForcedDryRun` bij — het audit-resultaat wordt dan `"DryRunLocked"` (gaat vóór `"DryRun"` in
  `BepaalAuditResultaat`). `PutMutationAsync` kreeg ook een optionele `verrijkResultaat`-delegate
  zodat een taakspecifieke responsparser (zie `AssignOfficialsAsync`/`VerrijkOfficialsResultaat`
  hieronder) het generieke resultaat nog kan bijstellen zonder de gedeelde PUT-uitvoering te
  dupliceren.
- **Health-check-endpoint (#998).** `FunctionApp.Postgres/Admin/SportlinkExtensieHealthFunction.cs`
  — `GET /api/beheer/sportlink-extensie/health?live=false` (default). Zonder `?live=true` leest dit
  uitsluitend onze eigen database (extension/dry-run-instelling, `EgressGuard`-status, koppeling +
  laatste login-/tokenactiviteit per rol uit `public.sportlinkautologin`, laatste `Failure`-rij uit
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

### 4.3 Kostenbeleid-implicatie / tokenopslag (#1411)

Beide database-tiers gebruiken dezelfde architectuur: de database bewaart alleen authenticated-
encrypted credentials en refresh-tokenmateriaal in `public.sportlinkautologin` of
`dbo.SportlinkAutoLogin`. De AES-256-GCM-sleutel staat als secretinstelling buiten de database.
De oude split tussen Postgres-databaseopslag en SQL Server Function App-instellingen is vervallen.
Zie [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md) voor sleutelbeheer, setup en herstel.

### 4.4 HARDE REGEL: coding agents mogen dit mechanisme nooit zelf uitvoeren

> Deze regel is historisch vervangen voor #1411. De eigenaar gaf expliciet toestemming voor de
> geïsoleerde implementatie- en loginproef; de actuele operationele grenzen en secretregels staan
> in [`docs/SPORTLINK-AUTOLOGIN.md`](SPORTLINK-AUTOLOGIN.md). De beschrijving hieronder gaat over
> het oude refresh-tokenproces en is geen actuele instructie.

**Dit geldt zonder uitzondering, voor Claude Code en elke andere coding agent, in elke sessie:**

> Een coding agent mag een Sportlink-refresh- of access-token nooit zelf uitlezen, opslaan,
> doorgeven of gebruiken om een Sportlink-API aan te roepen, en doet nooit zelf een HTTP-aanroep
> naar `club.sportlink.com` of `idm.sportlink.com` — ook niet "even snel om te verifiëren", ook
> niet als het token al zichtbaar is geworden in de sessie.

**Waarom dit geen conventie maar een vastgestelde blokkade is:** tijdens de bouw van deze extension
probeerde de coding agent dit mechanisme meermaals zelf uit te voeren (het token uit de browser
lezen, een script draaien met het token als parameter) — en werd dit **consequent, op twee
onafhankelijke tokens via twee verschillende mechanismen**, geblokkeerd door de auto-mode-
veiligheidslaag van Claude Code zelf. Dit is dus een technisch afgedwongen grens, niet een keuze.

**Incident (2026-09-04):** ondanks deze blokkades kwam één refresh-token per ongeluk in de
chatsessie met de agent terecht (bedoeld voor een lokale scriptprompt, per abuis in de chat
geplakt). De eigenaar moest direct volledig uitloggen bij Sportlink om die token in te trekken.
Elk token dat ooit in een agent-sessie zichtbaar wordt, geldt vanaf dat moment als verbrand.

**Praktisch gevolg voor deze scripts** (de drie `Invoke-Sportlink*`-spikes zijn in #1411 verwijderd;
de regels hieronder blijven gelden voor elk toekomstig script dat een opgeslagen token gebruikt):
- ~~`Invoke-SportlinkTokenSpike.ps1`~~ was van nature agent-veilig: het vróeg bij elke run opnieuw om
  het token via `Read-Host -AsSecureString`, wat in een niet-interactieve agent-tool-omgeving
  (stdin op `/dev/null`) niet ingevuld kan worden.
- ~~`Invoke-SportlinkMatchLookup.ps1`~~ las het token zelf uit `local.settings.json` — dat had
  daarom een **expliciete `Read-Host`-mensbevestiging** nodig (typ "JA") vóórdat het token gebruikt
  werd. Zonder die bevestiging had dit script, anders dan het spike-script, wél door een agent
  silently uitgevoerd kunnen worden — dat is precies wat er (bijna) gebeurde bij de review die tot
  dit document leidde.
- ~~`Tools/SportlinkTokenCapture`~~ is verwijderd in #1411. De backend automatic-login is de
  ondersteunde flow; zie [`docs/SPORTLINK-AUTOLOGIN.md`](SPORTLINK-AUTOLOGIN.md).
- **Nieuw script, nieuwe regel:** elk toekomstig script dat een opgeslagen refresh_token gebruikt
  krijgt dezelfde `Read-Host`-mensbevestiging als destijds `Invoke-SportlinkMatchLookup.ps1` — niet alleen
  een waarschuwing in commentaar. Commentaar wordt door een agent gelezen maar is geen technische
  barrière; `Read-Host` in een niet-interactieve omgeving wel.
- Verificatie van de refresh-cyclus, of van een nieuw endpoint dat een refresh_token nodig heeft,
  gebeurt dus altijd door een mens (met een van bovenstaande scripts) of door de daadwerkelijk
  gedeployde Function App-runtime zelf — nooit door een agent tijdens ontwikkeling.

**Incident (2026-09-26): passieve leak via de harness' eigen file-diff-melding, geen agent-actie
nodig.** Tijdens een lokale acceptatietest had de agent `FunctionApp.Postgres/local.settings.json`
eerder in de sessie gelezen voor een ongerelateerde controle (`AllowExternalIntegrations`). Toen de
mens daarna, volgens de voormalige §3.3, de token-capturetool draaide en het verse refresh_token
wegschreef, toonde de coding-agent-harness bij de eerstvolgende beurt automatisch een
wijzigingsmelding met de **volledige tokenwaarde** — zonder dat de agent het bestand opnieuw las,
opvroeg of er zelfs maar naar vroeg. Dit is fundamenteel anders dan de twee incidenten hierboven
(die vereisten allebei een actieve stap: geplakt worden, of een `catch`-blok dat expliciet logt) —
hier volstond alleen dat het bestand ooit, voor iets heel anders, door de agent was gelezen.

**Praktisch gevolg:**
- Zodra een coding agent een bestand met geheimen (`local.settings.json`, `.env`, of vergelijkbaar)
  ook maar één keer in een sessie heeft gelezen, geldt elke latere wijziging aan dat bestand in
  diezelfde sessie als een leak-risico — ongeacht wie of wat die wijziging veroorzaakte. Zie §3.3
  stap 3 voor hoe dit in de praktijk te vermijden is (mens haalt de waarde zelf op, buiten elke
  agent-tool-aanroep om).
- Komt een token toch zo in een sessie terecht: exact dezelfde regel als bij elk ander incident op
  deze pagina — vanaf dat moment geldt het als verbrand. Capture opnieuw (§3.3 stap 2), registreer
  het nieuwe token, en doe dat bij voorkeur in een venster/sessie waar de agent dit bestand nog niet
  heeft aangeraakt. Is dat niet haalbaar (de agent heeft het bestand al gelezen), dan blijft de
  volgende wijziging alsnog zichtbaar worden — dat is een aanvaarde restrisico van deze harness-
  functionaliteit, geen reden om de koppelstap over te slaan.
- Dit generaliseert voorbij Sportlink: elk project met een "dit geheim mag een agent nooit zien"-grens
  heeft dezelfde blinde vlek zolang het bestand ooit is gelezen — het risico zit in het
  bestandsvolg-mechanisme van de harness, niet in een keuze van de agent zelf.

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
- **Uitgezonderd van deze toestemming — twee paden die niet op de testwedstrijd te scopen zijn:**
  het actie-pad van #996 (`PUT /api/sportlink/change-requests/{publicRequestId}/action`) en het
  wijzigingsverzoek van #995 (`PUT /api/sportlink/match/{wedstrijdcode}/change-request`).
  `MatchChangeRequests` is niet per wedstrijd gescoped en #995 raakt per definitie een échte
  tegenstander; een klik daar forceert dus een echte beslissing op een echt verzoek (zie §5). Een
  agent klikt die knoppen nooit, ook niet lokaal, ook niet in dry-run.

## 5. Risico's en beperkingen

- **Menu-zichtbaarheid (#1122):** "Wijzigingsverzoeken" en "Wedstrijden" (menu-item voor het scherm
  "Oefenwedstrijd aanmaken", #1321) staan alleen in het menu als de extensie aan staat
  (`ClubSelectorService.SportlinkExtensionEnabled`, gevuld door
  NavMenu bij laden/clubwissel en bijgewerkt door de instellingenpagina na opslaan; sinds #1578
  via de eigen gebeurtenis `OnSportlinkExtensionChange`, zodat pagina's er niet op herladen). Een directe
  URL werkt nog wel; de API antwoordt dan 409 "Sportlink Web Extension staat uit."

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
- **#994's officials-toewijzing was scaffolding, geen live-getest pad — sinds #1319 wél.** Endpoint
  en requestbody waren gereverse-engineerd, nooit met een netwerktrace gezien — vandaar de
  code-niveau `forceDryRun`-lock (§4.2/§6.4) die ongeacht `sportlinkDryRun` altijd simuleerde. De
  eigenaar heeft `MatchOfficialsActionLiveBevestigd` op 27-09-2026 na een live netwerktrace op
  `true` gezet (#1319): deze mutatie volgt vanaf nu gewoon de club-instelling `sportlinkDryRun`,
  net als elke andere bevestigde mutatie.
- **#995's wijzigingsverzoek is de enige mutatie die een ECHTE tegenstander raakt — en zelfs stap 1
  (valideren) kan al het gevaarlijke moment zijn.** Sportlink werkt naar verluidt in twee stappen
  (valideren → bevestigen), maar dat is niet met een netwerktrace geverifieerd. Als Sportlinks
  eerste PUT in werkelijkheid geen "dry validate" blijkt te zijn maar al het verzoek verstuurt, is
  deze scaffolding-stap zelf al de gevaarlijke actie. **De eigenaar heeft
  `UpdateMatchDetailsChangeRequestLiveBevestigd` op 27-09-2026 na een live netwerktrace op `true`
  gezet (#1319)** — de code-niveau `forceDryRun`-lock die dit softwarematig afving, is dus
  ingetrokken; deze mutatie volgt nu de gewone club-instelling `sportlinkDryRun`. Stap 2
  (bevestigen) blijft bewust NIET gebouwd: geen endpoint, geen client-methode, geen UI-knop — dat is
  een aparte, ongewijzigde scope-beslissing, los van deze lock.
- **#996's actie-pad (goedkeuren/afwijzen) kan niet veilig getest worden met de vaste testwedstrijd
  (2026-09-06 vastgesteld).** In tegenstelling tot #992/#993 is `MatchChangeRequests` niet per
  wedstrijd gescoped — het levert alle openstaande verzoeken van échte tegenstanders voor het hele
  serviceaccount. Er bestaat geen manier om een fictief, veilig testbaar verzoek te laten ontstaan
  binnen Sportlink zelf. Een test van de actie zou dus een echte beslissing forceren op een echt
  verzoek van een echte tegenstander — alleen te doen met expliciete instemming van de eigenaar
  over een specifiek, door hem aangewezen verzoek.
- **#997's oefenwedstrijd-aanmaak had de meeste onbekenden van alle #986-sub-issues — de
  code-niveau `forceDryRun`-lock is sinds #1319 opgeheven (`ClubMatchLiveBevestigd = true`),
  de onderliggende onzekerheid over endpoint/requestbody niet.** Deze mutatie respecteert nu de
  gewone club-instelling `sportlinkDryRun` in plaats van altijd te simuleren, maar endpoint,
  volledige requestbody, en de exacte respons-veldnamen van de twee aangesloten picklists blijven
  grotendeels gereverse-engineerd en zijn niet apart met een eigen netwerktrace bevestigd — anders
  dan bij #994/#995 hierboven is bij #997 geen losse trace gedocumenteerd. Uitslag vastleggen
  (`ClubMatchScore`) is bewust niet aangesloten. **Verwijderen (`ClubMatchDelete`, #1440) is gebouwd en sinds #1458 live**
  (`ClubMatchDeleteLiveBevestigd = true`) — zie de alinea "#1440" in §6.4.
  Een toekomstige koppeling tussen een zelf-geplande oefenwedstrijd in
  `planner.geplandewedstrijden` (kolom `sportlinkwedstrijdcode`, momenteel ongebruikt voor dit doel)
  en het door Sportlink teruggegeven `PublicMatchId` is bewust niet gebouwd in deze ronde — zie de
  PR-beschrijving van #997 voor een eerste verkenning van dat aanknopingspunt.
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
| `competition/match/UpdateMatchDetails` (idem, andere velden overschreven) | PUT | Wijzigingsverzoek datum/tijd/accommodatie — **sinds #1319 live bevestigd, ALLEEN stap 1 (#995)**, zie §4.2/§5 | `SportlinkMutationSoort.DatumTijdAccommodatie` |
| `competition/match/official/MatchOfficialsAction` | PUT | Officials toewijzen — **sinds #1319 live bevestigd (#994)**, zie §4.2 | `SportlinkMutationSoort.Officials` |
| `competition/match/changerequest/MatchChangeRequests` | GET | Inkomende wijzigingsverzoeken van tegenstanders ophalen | — (geen `SportlinkMutationGuard`, zie §4.2) |
| `competition/match/changerequest/MatchChangeRequestAction` | PUT | Verzoek goed-/afkeuren | — (idem) |
| `user/UserInfo` | GET | `PublicPersonId` van het service-account, nodig voor `MatchChangeRequestAction` | — |
| `competition/match/clubmatch/ClubMatch` | **POST** | Oefenwedstrijd aanmaken — **sinds #1319 live bevestigd (#997)**, zie §4.2. Geen guard mogelijk vóór aanmaak (er is nog geen wedstrijd) — alleen eigen-DB-checks i.p.v. een Sportlink-permissievlag | — (eigen toggle/EgressGuard i.p.v. `SportlinkMutationGuard`, zie §4.2) |
| `competition/match/clubmatch/PickListsTeams` | GET | Picklist teams voor het aanmaak-formulier (#997) — read-only, echt aangeroepen | — |
| `competition/match/clubmatch/PickListsLocation` | GET | Picklist locaties voor het aanmaak-formulier (#997) — read-only, echt aangeroepen | — |
| `competition/facilityoccupation/FacilityOccupation` (`?FacilityId=&GameDate=&IsSeasonStartAllowed=`) | GET | De Sportlink-veldplanner voor één accommodatie en dag (#1563): veld, starttijd en blokduur (`Duration + Interval`, met `StartUpInterval`/`FollowUpInterval`). Alleen `ScheduledMatches` wordt gelezen. **Live bevestigd 2026-10-07** met de lokale autologin; voedt `GET /api/planner/veldbezetting` — zie `docs/ARCHITECTUUR-PLANNER.md`. Alleen lezen, nooit de bijbehorende PUT | — |
| `competition/match/clubmatch/ClubMatchDelete` (`?PublicMatchId=`, geen body) | **DELETE** | Clubwedstrijd verwijderen (#1440). Live sinds #1458 (`ClubMatchDeleteLiveBevestigd = true`, respons `{PublicMatchId, IsSuccess}`), volgt `sportlinkDryRun`, zie §6.4. Alleen aangeboden op het resultaat van "Wedstrijd aanmaken" | `SportlinkMutationSoort.Verwijderen` (`IsKernelMatch = false`, fail-closed, plus `IsHomeMatch`) |
| `competition/match/clubmatch/ClubMatchScore` | — | **Bewust NIET aangesloten (#997)** — uitslag vastleggen, buiten scope | — |
| `competition/match/clubmatch/ClubMatchDefaults`, `PickListsMatchInformation`, `codetable/AgeClassList` | — | **Bewust NIET aangesloten (#997)** — drie extra onbevestigde endpoints tegelijk is te veel gok in één ronde | — |
| `competition/match/MatchRemarks` | — | **Bewust NIET aangesloten** — opmerking bij een wedstrijd; wel in het bronrapport (§2.4), maar er is geen functionele vraag naar en het pad is nooit live gezien | — |

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
- **`MatchOfficialsAction` (#994, ONBEVESTIGD)**: `{ PublicMatchId, OfficialsToBeAssigned: [
  { OfficialPosition, PersoonId } ] }` — de elementstructuur is nooit met een netwerktrace gezien,
  afgeleid uit `OfficialPosition` zoals dat terugkomt in `GET .../MatchOfficials`. Respons:
  `{ Officials: [ { ..., ValidationDescription } ] }` — één niet-lege `ValidationDescription` zet
  `IsSuccess=false`, ook bij HTTP 200 (zie `VerrijkOfficialsResultaat`).
- **`UpdateMatchDetails` voor het wijzigingsverzoek (#995, ONBEVESTIGD, ALLEEN stap 1)**: zelfde
  envelope als de veld-wijziging hierboven, maar met `MatchDate`/`StartTime`/`FacilityId` (uit de
  invoer, anders uit de snapshot) en `MatchChangeRequestRemarks` (de verplichte toelichting,
  altijd uit de invoer) overschreven — `IsMatchChangeRequestMandatory: true` en een lege
  `PublicApplicantId` zijn hier eigen, ONBEVESTIGDE aannames (#993's veld-wijziging gebruikt
  `false` resp. een live-bevestigde lege string, maar voor een EIGEN-veld-wijziging, geen verzoek
  aan een tegenstander). Respons: `{ ConfirmationNeeded: null | { ValidationResultMessages: [...],
  HasBlockingMessages } }` — nooit met een netwerktrace gezien; elementen van
  `ValidationResultMessages` kunnen kale strings of objecten met een `Message`-/`Description`-veld
  zijn, en de positie van `HasBlockingMessages` (genest of toplevel) is evenmin bevestigd — zie
  `SportlinkClubClient.ParseMatchChangeValidatie` voor de defensieve aanpak. Deze parser wordt in de
  praktijk nooit aangeroepen zolang de code-lock actief is.
- **Afwijzingsvorm (HTTP 420, live bevestigd #1040)**: `{"Error":true,"Status":"420",
  "Message":"Validation exception : <code>","ViolationCodes":["<code>", ...],
  "Violations":{"<code>":"Nederlandse omschrijving"}}`. Succes wordt bepaald door `Error != true &&
  response.IsSuccessStatusCode`, niet door een afzonderlijk `isSuccess`-veld (de happy-path-vorm is
  nooit live bevestigd).
- **`ClubMatch` (#997; contract live bevestigd bij #1427)**: zie de #1427-alinea in §4.2 voor de
  vier voorbereidende GETs en de exacte body. `PutMutationAsync` stuurt dit endpoint als POST
  (optionele `HttpMethod`-parameter, default `Put`). Een afwijzing zonder `Violations` (zoals de
  602 van vóór #1427) krijgt Sportlinks `Message` als violation.

### 6.4 Dry-run (#998) en de code-niveau forceDryRun-lock (#994)
In dry-run wordt de body nog wél geserialiseerd (zodat een serialisatiefout alsnog opduikt) maar
niet verstuurd — `PutMutationAsync` retourneert direct `{IsSuccess: true, Violations: null,
IsDryRun: true}` zonder een HTTP-aanroep te doen. De body zelf wordt nooit gelogd (kan
teamnamen/persoonsgegevens bevatten); alleen `{EntityName}`/`{Endpoint}` verschijnen in de log.

Sinds #994 kan dezelfde tak ook bereikt worden door een `forceDryRun: true`-parameter, ONAFHANKELIJK
van de club-instelling `sportlinkDryRun` — voor een mutatie waarvan de requestbody nog niet live
bevestigd is (het eerste voorbeeld: `AssignOfficialsAsync`). In dat geval krijgt het resultaat ook
`IsForcedDryRun: true` mee (audit-resultaat `"DryRunLocked"`) en gebruikt de logregel expliciet de
tekst "code-lock, body niet live bevestigd" — zo is in de Function-log meteen te zien of een
gesimuleerde mutatie kwam door de club-instelling of door deze harde, niet-instelbare lock.

Bij #995 werd dezelfde lock gebruikt voor `RequestMatchChangeAsync`. Sinds #1319 staat
`UpdateMatchDetailsChangeRequestLiveBevestigd` op `true`: de validatiestap volgt nu de
club-instelling `sportlinkDryRun`. Die stap kan een echte Sportlink-aanroep doen. De aparte
bevestigingsstap voor een verplicht wijzigingsverzoek bestaat nog niet en vereist een toekomstige
implementatie en beslissing. Een eventuele nieuwe lock ontgrendelen blijft voorbehouden aan de
eigenaar na een handmatige proef, nooit aan een agent (§4.4).

**#1440/#1458 — verwijderen van een clubwedstrijd (`DeleteClubMatchAsync`).** Derde lock van deze
soort, door de eigenaar opgeheven in #1458 (besluit 2026-10-03, na live trace):
`ClubMatchDeleteLiveBevestigd = true`. Methode (`DELETE`) en parameter (`PublicMatchId` als
querystring, geen body) komen uit Sportlinks publieke frontend-bundle; de succesrespons is live
vastgesteld: een JSON-object `{ "PublicMatchId": ..., "IsSuccess": true }`. De aanroep volgt vanaf
nu de club-instelling `sportlinkDryRun` (bij dry-run verlaat er geen DELETE de client;
`SportlinkClubMatchDeleteTests` bewijst beide takken en de responsvorm).
- **Guard** `SportlinkMutationSoort.Verwijderen`: "zoals Sportlink" alleen een expliciete
  `IsKernelMatch = false` (clubwedstrijd) mag weg; ontbreekt het veld, dan weigert hij (409). Er is
  geen aanvullende eis op het competitietype. De algemene `IsHomeMatch`-regel van
  `SportlinkMutationGuard` geldt voor alle mutaties en bleef gehandhaafd.
- **Geen beperking tot via de webapp aangemaakte wedstrijden** (eigenaarsbesluit 2026-10-03): de
  server-guard hierboven volstaat. De UI biedt de knop wel alleen aan op het resultaat van een échte
  aanmaak op "Wedstrijd aanmaken", altijd met een bevestigstap met de waarschuwing "Verwijderen is
  definitief; de tegenstander kan een melding krijgen.", zonder form-element (#1436).
- **Fail-closed audit (#1458):** ontbreekt de mutatie-auditservice in DI, dan weigert elk
  mutatie-endpoint (aanmaken, verwijderen, wedstrijdmutaties, wijzigingsverzoek-acties) met HTTP
  503 vóór de Sportlink-aanroep, op beide tiers (`SportlinkEndpointSupportCore.AuditNietBeschikbaarFout`).
- **Audit**: elke poging (ook een geblokkeerde) krijgt een rij met het echte `PublicMatchId`, actie
  `DeleteClubMatch` en in `WaardeVoor` een snapshot zonder persoonsgegevens (wedstrijdnummer, datum,
  status, `IsKernelMatch`, accommodatie).
- **Respons**: een 2xx zonder body telt nog steeds als geslaagd (`IsLegeSuccesRespons`, voor het geval
  Sportlink dat ooit doet); een 420 met `Violations` is een afwijzing.

**#1320 (eigenaar-gestuurde productieproef met trace van validatie en bevestiging)** bouwde de
diagnostiek-UI rond diezelfde, ongewijzigde code-lock — de lock zelf is met dit issue niet
aangeraakt, alleen wat de eigenaar ervoor en erna ziet:
- Vóór de aanroep toont het scherm een expliciete waarschuwing met een aparte, tweede
  bevestigknop ("Ja, verstuur de validatie-PUT naar Sportlink") — de eerste knop start dus nog
  niets, hij toont alleen de waarschuwing.
- Na de aanroep toont het scherm een leesbare trace: tijdstip (UTC, serverzijdig), HTTP-methode/
  endpoint/status, en de vier toplevel-booleans (`IsSuccess`, `IsMatchChangeRequestMandatory`,
  `IsOwnFacility`, `IsForceUpdate`) plus `ConfirmationNeeded`/`HasBlockingMessages` — stuk voor stuk
  al gedistilleerde, PII-vrije velden uit `SportlinkMatchChangeValidatie`. Nooit de ruwe request-/
  responsebody: die kan tokens of overige velden bevatten die niet bedoeld zijn voor een scherm.
- De eigenaar kan een korte testnotitie vastleggen bij die specifieke poging
  (`PUT /api/sportlink/audit/{id}/notitie`, zie `docs/API.md`) — gekoppeld aan het audit-record-ID
  van diezelfde aanroep, scoped op `ClubCode` zodat een audit-rij van een andere club nooit
  gewijzigd kan worden. Nieuwe kolom `SportlinkMutationAudit.Notitie`/`sportlinkmutationaudit.notitie`
  (migratie 028 op de Postgres-tier, idempotente `ALTER TABLE` in `Script.PostDeployment1.sql` op de
  SQL Server-tier).
- Stap 2 (bevestigen) is met #1320 bewust **niet** gebouwd — dezelfde reden als hierboven: de
  werkelijke bevestigingsvorm voor een verplicht wijzigingsverzoek is nooit met een netwerktrace
  vastgesteld, en #1320 mag dat contract niet verzinnen. De eerste échte productietrace (na
  Aanpak-stap 1 van #995 én het omzetten van de constante door de eigenaar zelf) moet dat gat
  vullen.

**Status sinds #1319 (27-09-2026):** de eigenaar heeft, buiten elke agent-sessie om, alle drie de
constanten (`MatchOfficialsActionLiveBevestigd`, `UpdateMatchDetailsChangeRequestLiveBevestigd`,
`ClubMatchLiveBevestigd`) op `true` gezet na een live netwerktrace. `forceDryRun` is voor deze drie
mutaties dus vanaf nu altijd `false` — elke aanroep volgt de gewone club-instelling
`sportlinkDryRun`, precies zoals elke andere bevestigde mutatie (`UpdateDressingRoomsAsync`,
`UpdateFieldAsync`). `IsForcedDryRun`/"DryRunLocked" komen voor deze drie mutaties dus niet meer
voor. Stap 2 (bevestigen) van #995 blijft niettemin bewust niet gebouwd — dat is een aparte,
ongewijzigde scope-beslissing, los van deze lock.

## 7. Bronnen
- [`docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md`](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) — volledig technisch bronrapport
- Epic [#986](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/986) en sub-issues #987-#998
- [`docs/ENTRA-AUTH-BEHEER.md`](ENTRA-AUTH-BEHEER.md) — rolbeheer en N-user-test
- [`docs/ARCHITECTUUR-DATABASE-TIERS.md`](ARCHITECTUUR-DATABASE-TIERS.md) — tier-bouwvolgorde; §4.2 hierboven legt uit waarom `SportlinkClubClient` wél in `Planner.Shared` zit maar de tokenopslag per tier verschilt

## 8. Openstaande DPO-vraag — relatiecode-prefill (#1340), VOORSTEL nog niet bevestigd door eigenaar

> **Dit is een VOORSTEL, GEEN besluit.** De eigenaar heeft op 2026-09-26 expliciet gevraagd om de
> implementatie van #1340 te starten zonder op formele bevestiging van deze drie vragen te
> wachten ("ik test dit zelf op mijn acceptatieomgeving — laat een duidelijke TODO staan, maar je
> mag beginnen met coderen"). Dat is toestemming om de smalle technische scope te bouwen, GEEN
> toestemming om deze paragraaf als beantwoord te behandelen. Behandel de relatiecode tot een
> expliciete owner-bevestiging als een indirect persoonsgegeven (AVG) en werk deze sectie pas bij
> nadat de eigenaar zelf reageert — nooit door een aanname van een agent.

**Context.** #1340 herziet de AVG-grens uit §5/§6 hierboven (`matchOfficials` bevat naam,
geboortedatum en foto-URL, live bevestigd tijdens het incident van 2026-09-06) door PRECIES ÉÉN
veld alsnog toe te staan: de relatiecode — een intern Sportlink-identificatienummer, geen naam —
van scheidsrechter/AR1/AR2, uitsluitend als prefill in `SportlinkMatchPanel`'s bestaande
tekstinvoervelden (die al vóór #1340 relatiecodes accepteerden als INVOER voor de #994-toewijzing,
zie hierboven). Er verandert niets aan wat de app OPSLAAT: de relatiecode wordt bij elke paneel-
weergave live bij Sportlink opgehaald (`GET .../Match`) en nergens in onze eigen database
bewaard — precies zoals de rest van dit paneel werkt (§5: "nooit opslaan buiten wat al in onze
eigen DB staat").

### Vraag 1 — rechtsgrond

**VOORSTEL:** gerechtvaardigd belang van de club bij correcte wedstrijdorganisatie (AVG art. 6 lid
1 sub f) — dezelfde grondslag die al impliciet gold voor de relatiecode als INVOERVELD bij de
#994-toewijzing (die bestond al vóór #1340; #1340 voegt alleen prefill toe, geen nieuwe
verwerkingsdoel). Dit is een aanname, geen onderbouwde toets: een volledige
gerechtvaardigd-belang-afweging (doel, noodzakelijkheid, belangenafweging tegen de official) is
niet gemaakt en hoort bij de owner-bevestiging.

### Vraag 2 — bewaartermijn

**VOORSTEL:** geen aparte bewaartermijn nodig, want de relatiecode wordt niet bewaard — zie
"Context" hierboven. Bij elke weergave van het paneel haalt de server een verse `GET .../Match`-
respons op; er is geen cache, geen kolom, geen tabel die de relatiecode vasthoudt. Zodra Sportlink
zelf de toewijzing wijzigt of verwijdert, verandert de eerstvolgende prefill mee. Als dit voorstel
klopt, is er geen "bewaartermijn" in de AVG-zin — wel blijft gelden dat de relatiecode nooit
alsnog in een cache, log-tabel of exportbestand terecht mag komen zonder dat deze vraag opnieuw
gesteld wordt.

### Vraag 3 — audit-logging (`RondMutatieAfAsync`)

**Gecontroleerd, geen aanname:** het NIEUWE leespad van #1340 (`GET
/api/sportlink/match/{wedstrijdcode}`, `SportlinkMatchFunction.Get`) roept
`ISportlinkMutationAuditService`/`RondMutatieAfAsync` helemaal niet aan — dat gebeurt uitsluitend
in `ExecuteMutationAsync`, de gedeelde stap onder de vier PUT-mutatie-endpoints (kleedkamers, veld,
officials, wijzigingsverzoek). De relatiecode-prefill wordt dus **niet** gelogd door dit issue.

**Wat al vóór #1340 bestond en ongewijzigd blijft:** de bestaande `PUT .../officials`-mutatie
(#994) logt via `ExecuteMutationAsync` wél een `WaardeNa` met de door de beheerder ingevoerde
relatiecode (`OfficialToewijzingDto.PersoonId`, geserialiseerd met `JsonConvert.SerializeObject`)
in de audit-tabel. Dat is geen nieuw gedrag van #1340 — het bestond al sinds #994 — maar volgt uit
dezelfde constatering die de openstaande DPO-vraag stelt: als een relatiecode een indirect
persoonsgegeven is, valt die bestaande `WaardeNa`-kolom onder dezelfde AVG-regels als de rest van
de audit-tabel (bewaartermijn, toegangscontrole, eventueel een verwijderverzoek). **Dit is een
bestaande situatie die #1340 blootlegt, geen regressie die #1340 veroorzaakt** — maar de
eigenaar-bevestiging op vraag 1/2 hierboven zou logisch ook voor deze bestaande kolom moeten
gelden, niet alleen voor de nieuwe prefill.

### Samenvatting voor de eigenaar

| Vraag | Voorstel | Status |
|---|---|---|
| Rechtsgrond | Gerechtvaardigd belang (art. 6 lid 1 sub f) | VOORSTEL, niet bevestigd |
| Bewaartermijn | Geen — relatiecode wordt nooit opgeslagen, alleen live doorgegeven | VOORSTEL, niet bevestigd |
| Audit-logging (nieuw leespad #1340) | Gecontroleerd: gebeurt niet | Feitelijk vastgesteld, geen aanname |
| Audit-logging (bestaand schrijfpad #994) | Bestond al, valt onder dezelfde AVG-vraag | Feitelijk vastgesteld — vraagt alsnog om dezelfde bevestiging |
