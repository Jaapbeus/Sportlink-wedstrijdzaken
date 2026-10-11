# HANDOFF — epic #1632 CA1502/CA1506 wegwerken (gestart en afgesloten 2026-10-11)

**Eigenaarsbesluit:** alle fases worden uitgevoerd; Claude bouwt, Codex reviewt; gestapelde PR-keten; de wijzigingen liften mee met de volgende release.

**Volgorde:** #1622 → #1623 → #1624 → #1625 → #1626 → #1627 → #1628 → #1629 → #1630 → #1631 → afsluiting (plafond 0, severity error).

**Stand:** #1622 in PR #1636 (review Codex); #1623 in PR #1638 (gestapeld op #1636); #1624 in PR #1640, gestapeld op #1638 (branch `feature/#1624-staging-parameterbinding`, geen versiebump); #1625 in PR (branch `feature/#1625-planner-shared-complexiteit`, gestapeld op #1640, bevat #1636; review Codex; analyzer-plafond 15 → 12); #1626 in PR (branch `feature/#1626-sportlink-client-complexiteit`, gestapeld op #1625; golden-tests vooraf; analyzer-plafond 12 → 9, interne duplicatie 622 → 610; eigenaarsbesluit V3: schrijfpaden gescheiden, alleen MatchData-opbouw gedeeld); #1627 in PR (branch `feature/#1627-template-antwoord-shared`, gestapeld op #1626; 52 golden-gevallen per tier vooraf, daarna `TemplateAntwoordBouwer` in Planner.Shared met per tier een adapter `TemplateAntwoordBron`; analyzer-plafond 9 → 8, tier-duplicatie 5216 → 5214; geen versiebump); #1628 in PR (branch `feature/#1628-verwerk-met-planner-cases`, gestapeld op #1627; 14 Postgres-integratietests en 9 SQL Server-tests vooraf op ongewijzigde code, daarna `VerwerkMetPlannerAsync` per verzoektype in `BerichtPipeline.Planner.cs` per tier plus `PlannerAntwoordJson` in Planner.Shared; analyzer-plafond 8 → 6, methodes-boven-grens 22 → 18, bestanden-boven-grens 13 → 12, tier-duplicatie 5214 → 5213; geen versiebump); #1629 in PR (branch `feature/#1629-emailprocessor-fasen`, gestapeld op #1628; `EmailProcessorFunction` per tier in partials `.Fasen.cs` (`VoerFase1LichtAsync`, `VoerFase2ZwaarAsync`, context-record `EmailRunContext`), `.Verwerking.cs` (`DetecteerLeermomentAsync`, `VerwerkBuitenScopeAsync`, `VoerReplyFlowMetTraceAsync`), `.Fouten.cs` en `.Noodmail.cs`; hoofdbestanden 1003/1081 → 184/194 regels; vier CA1506-meldingen weg, analyzer-plafond 6 → 2, bestanden-boven-grens 12 → 10, methodes-boven-grens 18 → 16; tier-duplicatie 5213 → 5287 VERHOOGD, eigenaarsgoedkeuring nodig; geen versiebump; #1631 in PR (branch `feature/#1631-sportlinkclubclient-domeinen`, gestapeld op #1629; `SportlinkClubClient` (103 typen) is een façade van 33 typen die delegeert naar `SportlinkWedstrijdLezer`, `SportlinkClubFormulierLezer`, `SportlinkWedstrijdDetails` en `SportlinkMutaties` rond de transportkern `SportlinkClubTransport` (enige route: `ExecuteWithTokenRetryAsync`, `SportlinkClubHttp.ZetSportlinkHeaders`), tokens in `SportlinkClubTokenBeheer`, mutatie-uitvoering in `SportlinkMutatieUitvoerder`; geen klasse boven 45 typen; interface, constructor en DI ongewijzigd; analyzer-plafond 2 → 1, interne duplicatie ongewijzigd 610; geen versiebump; let op: AGENTS.md noemt nog `SportlinkClubClient.ExecuteWithTokenRetryAsync`/`ZetSportlinkHeaders`, aanpassing vraagt een eigen opdracht). **#1630** in PR #1648 (gestapeld op develop na merge #1631): AdminApiClient vervangen door acht domeinclients rond `AdminApiTransport`; merge met develop gedaan, renderchecks in uitvoering, daarna review ronde 2.

**VOORGESTELDE EINDSTAND (2026-10-11, afsluit-PR #1652 voor issue #1651; NOG NIET GEMERGED, gestapeld op PR #1648 die ook open is; niets hiervan staat op `develop` of is gereleased):** analyzer-overtredingen 19 → 0 (nulmeting #1300). PR's: #1636, #1638, #1639, #1640, #1642, #1643, #1644, #1645, #1646, #1648, #1649 en de afsluit-PR #1652. Controleer deze status opnieuw bij de daadwerkelijke overdracht. Plafond `analyzer-overtredingen` staat op 0; CA1502/CA1505/CA1506 staan in `.editorconfig` op `error` (mutatiebewijs: een testloze methode met cyclomatische complexiteit 41 breekt de Release-build met `error CA1502`); `check-analyzer-complexiteit.sh` blijft als tweede slot en controleert sinds de review-verwerking alleen de sectie `[*.cs]` (de testsectie met `none` telt niet mee). AGENTS.md noemt nu `SportlinkClubTransport.ExecuteWithTokenRetryAsync` en `SportlinkClubHttp.ZetSportlinkHeaders` (werkinstructiewijziging: merge door de eigenaar).

**Gecombineerde verificatie op de eindstand (debugomgeving, versie 3.13.1.2, Postgres-tier):** `Test-DebugGo.ps1 -Offline -ZonderAI` = GO; rendercheck van 20 routes: allemaal HTTP 200, geen foutbanner, geen console-errors, versie in de header. E-mailtester-smoke beperkt tot route en foutafhandeling: lokaal is er geen OpenAI-sleutel, dus de dry-run (UI en `POST /api/test/email`) geeft een nette melding "IChatClient niet geconfigureerd" (HTTP 500 uit het endpoint, geen foutbanner); het scenario, het antwoord en de trace-stappen zijn dus NIET geverifieerd (vraagt een omgeving met sleutel).

**Open verificatie (eigenaar, vóór release):** (1) e-mailtester-DryRun met synthetische testdata (`docs/ARCHITECTUUR.md` §8.1.5) in een omgeving met geconfigureerde IChatClient: scenario, antwoord en trace vastleggen; (2) rendercheck van `/testdata/wedstrijden` (punt uit PR #1648, buiten de 20 gecontroleerde routes). Deze CI-run kan dat niet uitvoeren.

**Restpunten:** #1650 (fase-skelet en tier-duplicatie); #1641 (Build (PR) draait niet op gestapelde PR's: dispatch `build.yml` handmatig); eigenaarspunt: de 2001:1::/32-blokkade in `SsrfProtection` blijft als beleid. Release-moment en ingelogde check zijn aan de eigenaar.

**Lessen:** wedstrijdcode-reeksen moeten globaal uniek zijn in de Postgres-tests; claims over Stop-Debug altijd verifiëren met ps/lsof (de bewakingslus van Start-Debug blijft anders hangen); `build.yml` dispatchen op gestapelde PR's; een Roslyn-regel op `error` zetten kan pas als de teller 0 is, anders breekt elke PR.

---

# HANDOFF — #1547 veldbezetting volgt Sportlink (2026-10-06, op develop, nog niet gereleased)

**Wat er op develop staat (v3.11.1.5):** de Planning toont de veldbezetting zoals de Sportlink-veldplanner (veld, tijd,
duur = Sportlink-speelduur + 15, volledige wedstrijdnaam). Zeven oorzaken opgelost op beide databasetiers; uitleg,
bewijs en bewuste verschillen: `docs/ARCHITECTUUR-DATABASE-TIERS.md` §79. PR #1548 (kern) en #1555 (verwerking Codex-review
ronde 1 en 2 plus restpunten uit de verificatie) zijn gemerged; Codex-rondes zijn gebruikt (limiet twee).

**Bij de volgende release (productie):** Postgres-migratie `036` draait mee en herstelt bestaande schade: sleutel van
`his.matchdetails` naar `interncode`, 347 ontbrekende datums, 345 velden en **69** onterecht als verwijderd gemarkeerde
gespeelde wedstrijden (door de eigenaar goedgekeurde herstelset, R1-F1). Productie is read-only nagekeken: veilig.
Na de deploy: Planning van de eerstvolgende zaterdag naast de Sportlink-veldplanner leggen, en daarna vier weken door.

**Open (eigen issues):** #1558 reconciliatie op de SQL Server-tier ontbreekt; #1559 eigenaarsbesluit: moet Veld
optimalisatie ook Sportlinks speelduur volgen (nu nog Speeltijden, besluit #291); #1560 spookwedstrijden (thuisteam =
uitteam, in Sportlinks data-API maar niet in de veldplanner), "+15" bij toernooivorm (20 min, vrijdag) en de
acceptatie over vijf zaterdagen. Versie 3.11.1.5 (na #1557, dat 3.11.1.4 nam).

**Lessen:** zie memory `sportlink-wedstrijdcode-vs-interncode` (wedstrijdnummer is niet uniek, koppel via interncode) en
`lokale-sqlserver-tier-testen` (upgradeproef met bestaande data; een sqlcmd-batch met compilatiefout voert niets uit).

---

# HANDOFF — na release v3.11.0.0 (2026-10-04)

**Update 2026-10-04 avond (op develop, nog niet gereleased):** Dependabot NuGet/Actions getest en gemerged
(#1508/#1509; lokaal Azurite vereist nu `--skipApiVersionCheck`, staat in Start-Debug), log-injectie opgelost
(#1472), documentatie geactualiseerd en verouderde scripts verwijderd (#1525), SQL-configuratie als Secrets in de
workflows (#1528), afsluitende codereview (#1529, o.a. nette foutmelding bij 503 en een opgeruimd event-abonnement).
**#1237:** logbewaartermijn op 30 dagen gezet; restrisico oude tags vastgelegd in SECURITY.md (geen history-rewrite);
SQL-waarden als Secret gezet. **Open voor de eigenaar:** de GitHub Variables verwijderen (geblokkeerd voor Claude) —
eerst de zes, ná de volgende release ook de drie SQL-variabelen; teksten/revisies van oude issues; feedbackwidget op
productie doorlopen (lokaal niet volledig te testen zonder AI-dienst). Vervolg-issue #1533.

---

**Update 2026-10-04:** v3.11.0.0 live op Flex met **dotnet-isolated 10.0** (#1073, #1074, eigenaarsbesluit om niet 24 uur te wachten).
Na de overstap gevonden en als hotfix opgelost: **3.10.0.1** (#1512, host-opslag via managed identity op Flex) en
**3.10.0.2** (#1515, instellingen per instantie laden — Flex schaalt elke niet-HTTP-trigger apart). Contract-check
geeft geen vals alarm meer (#1518). Live: health ok, 401-controles, GUI zonder CSP-fouten, keep-alive en contract-check
geslaagd. Live Sportlink-verwijdertest geslaagd (#1458). **Les:** Application Insights is sterk gesampled — controleer
timers via traces of databaseregels, niet alleen via requests.

---

## Vorige stand — na release v3.10.0.0 (2026-10-03)

**Productie:** v3.10.0.0 live op de **Flex Consumption-app** (FLEX-05/06/09 afgerond, #1068 #1069 #1072).
De oude Linux Consumption-app is **gestopt, niet verwijderd** — bewaren tot minimaal 2027-01-03 (#1076).
Alle niet-HTTP-triggers draaien op de Flex-app; op de oude app staan ze uit. Deploy-secrets wijzen naar de Flex-app.

Release via `/release` (eerste run van de securitypoort, #1470): Security Gate groen, alerts 0/0/0,
security-review 0 HIGH / 0 MEDIUM / 6 LOW (5 opgelost, 1 geaccepteerd: versietekst kan als link renderen).
Lokale Playwright-acceptatie en -regressie groen; live: health ok, 401-controles, Admin GUI zonder CSP-fouten.

## Eigenaarsacties (open)
1. **PDF-export aanzetten voor de eigen club** (#1459, standaard uit): Instellingen → PDF-export. Vraagt een admin-login.
2. **Nieuwe begeleiders-CSV importeren** (#1360, kolom `Functie`).
3. ~~Live Sportlink-verwijdertest~~ — geslaagd op 2026-10-03 (#1458).
4. **Seizoensreset één keer op productie** (#1352, toegestaan) — vraagt een admin-login.
5. **Kostenalert op het gratis Flex-tegoed** (#1075): eurobudget (€ 5/mnd, 85%/100%) staat; een metric alert op het
   tegoed is betaald en wacht op akkoord.
6. **#1237** (besloten notitie), **#42/#43** (testperiode e-mailverwerking met dagelijkse review).

## Volgende ronde (Claude Code)
- ~~.NET 10~~ — live in v3.11.0.0.
- **#1473** RID-specifieke publish (nu onblokkeerd), **#1472** log-injectie (45 CodeQL-meldingen).

## #1561 (versie 3.11.1.6) — vervolg op de acceptatietest van 3.11.1.5
Migratie 037 (+ SQL Server-spiegelblok) herstelt de lege teamnaam bij gespeelde wedstrijden; Planning-blokken tonen
tijd en naam op één regel; elke wedstrijd, ook 35+/VR30+ (20 min), volgt speelduur + 15 (geen uitzondering,
zie #1563 voor de blokduur rechtstreeks uit Sportlink). Open: wedstrijden met thuisteam = uitteam (twee extra blokken op 10 oktober) en het
35+-blok verifiëren met Sportlink-screenshots van de toernooidagen, zie #1560.
