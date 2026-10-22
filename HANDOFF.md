# HANDOFF — #1774 Planning: stille terugval op de databasekopie (stand 2026-10-22)

Branch `feature/#1774-planning-sportlink-status` (worktree `.claude/worktrees/1774-fable`, versie 3.15.0.1), implementer Claude Code, reviewer Codex.

**Gedaan:** `GET /api/planner/veldbezetting` geeft een envelop met `SportlinkVeldplannerStatus`; Planning-pagina en HTML/PDF tonen de reden van een terugval; log noemt dezelfde reden (een mislukte locatielijst is geen "accommodatie onbekend" meer); een mislukte periodieke herlogin gaat door met het refresh-token; de contract-check-noodmail benoemt een verlopen koppeling; per aanroep worden de aantallen gekoppeld / niet in Sportlink / alleen in Sportlink gelogd. Builds, alle database-onafhankelijke testsuites en de CI-guards groen; CSP-publish-check groen.

**Niet uitgevoerd:** browser-rendercheck en `Test-App.ps1` met live services (gedeelde debugomgeving niet gereserveerd); live aanroep van het nieuwe JSON-contract. Codex-review beoordeelt dit; na de release de Planning naast de Sportlink-veldplanner leggen.

**Open voor de eigenaar (survey buiten de repo):** bewaarde kopie van de Sportlink-veldplanner (nieuwe tabel, migratie) zodat de terugval de laatste veldplanner toont in plaats van de dagelijkse `/programma`-kopie; alarmering bij `HerkoppelingVereist` buiten de dagelijkse noodmail; een 9-uurs herlogin die faalt na drie pogingen blijft stil tot de volgende noodmail.

---

# HANDOFF — Sprint A #1607 en nazorg (stand 2026-10-12 ~15:00)

Geverifieerd met `gh pr list`/`gh pr view`/`gh issue view` en `git log origin/develop`; niets hiervan is gereleased (`develop` op c2d60aba, versie 3.13.4.9).

**Gemerged op `develop` vandaag (aanvulling op eerdere lijst):** #1670 (#1669: merge naar develop haalt `turn:` weg), #1681 (#1678 verhardingen), #1684 (#1672 auth-gate), en eerder #1659, #1660, #1661, #1662, #1663, #1667, Dependabot #1657/#1658 (#1668).

**Open PR's (versies 3.13.4.10 t/m .14 zijn verdeeld; bij elke volgende merge CHANGELOG/csproj/openapi-conflicten oplossen en het plafond in `scripts/ci/codekwaliteit-plafonds.txt` optellen):**

| PR | Issue(s) | Stand | Wie aan zet |
|---|---|---|---|
| #1682 | #1677 | Codex ronde 2 schoon op 7b996b49; vervolgpunten in #1689 | eigenaar merget |
| #1685 | #1673 + #1674 | Codex ronde 2 schoon op 925e290e; bevat een index-migratie op beide tiers (schema) | eigenaar merget |
| #1680 | #1671 | herontwerp na onafhankelijke verificatie (head 9abf7ed9, draft); Codex-rondes 1-2 gingen over oudere heads | eigenaar beslist: Codex ronde 3 of de Opus-verificatie als finale review |
| #1679 | #1675 | verwerkt na onafhankelijke verificatie (head 66a5f505, draft); Codex-rondes 1-2 over oudere heads; open keuze: strenge hostregel (alleen dezelfde host met/zonder www) | eigenaar (zelfde besluit als #1680) |
| #1683 | #1676 | verwerkt na onafhankelijke verificatie (head 68450976, draft); open eigenaarspunt: de twee tiers zijn bewust tekstueel uiteengehaald om het duplicatieplafond te ontwijken | eigenaar (zelfde besluit) |
| #1687 | #1686 | Codex ronde 1 had één punt (race in de helper): verwerkt; ronde 2 aan te vragen op de nieuwe head | Codex (ronde 2), daarna eigenaar |

**Openstaande eigenaarsbesluiten:**
1. Verruiming van de zelfmerge-regel: uitzonderingslijst (auth, workflows, CI-guards, `source: owner`) en de tegenspraak met "een CI-run merget niet" (voorstel in de chat van 2026-10-12).
2. Hostregel #1679 en tier-uiteenhaling #1683 (zie tabel).
3. Rondelimiet voor #1679/#1680/#1683: Codex ronde 3, of de Opus-verificatie als finale review.
4. R-01, R-02, R-04, R-13 en R-15 uit het besloten dossier (alleen de nummers; inhoud nooit publiek) en de GitHub-repo-instellingen (R-10).
5. Na de release: live 3-user-test van de auth (rolgrens en auth-gate), MSAL-login met `form-action` in productie, e-mailtester-DryRun met AI, en de keuze voor oude opgeslagen favicon-/logo-waarden (#1679).
6. Kleine issues: #1688 (clubafkorting in een dev-script), #1689 (restpunten clubcode-toetsing).

**Modelroutering:** Fable-limiet ~95% op 2026-10-12: werk en verificatie met Opus; Fable alleen voor orkestratie; bij naderende limiet geen nieuwe agents en hervatten vanaf dit bestand op de resettijd (onbekend: vraag de eigenaar of lees `/usage`).

**Lessen:** vóór elke reviewaanvraag alle `turn:`-labels lezen en in één `gh issue edit` vervangen; een onafhankelijke (read-only) verificatie vond vier blokkerende punten die twee Codex-rondes misten: Codex' "schoon" is geen bewijs op de huidige head; gebruik `gh pr view --json statusCheckRollup` (alle checks compleet en groen) in plaats van een vroeg terugkerende `gh pr checks --watch`.

---

# HANDOFF — nazorg na v3.13.2.0 (2026-10-12, orchestratorsessie; alles op `develop`, nog niet gereleased)

**Gemerged op `develop` (2026-10-12):** #1659 (#1559: optimalisatie volgt Sportlinks blokduur, geen app-buffer voor Sportlink-wedstrijden; v3.13.3.0), #1660 (#1597: widgetlabel `via: feedback-widget`, oude label verwijderd en vijf historische issues gemigreerd; v3.13.3.1), #1663 (#1650: tier-onafhankelijke delen van `EmailProcessorFunction` naar `Planner.Shared/Email`, tier-duplicatie 5287 → 5171; de volledige fase-orkestratie achter een poort blijft bij #1271 wegens per-tier domeintypes), #1662 (#1647 hoofdmenu in groepen met rechten per rol + #1599 instellingen één keer per paginaopening; nieuw endpoint `GET /api/planner/menu-toegang`; v3.13.4.0).

**Open PR's voor de eigenaar:** #1661 (#1641: `Build (PR)`/Security Scan zonder `branches`-filter, issue-beurt-prompt en zelfmerge-regel eisen beide check-runs op de head; squash-merge aanbevolen; eigenaarsbesluiten: bot-dispatch vs. escalatie, besloten notitie per SECURITY.md) en #1667 (#1664/#1665/#1666: issue-beurt-poort eist actuele exclusieve beurt, stopt bij meerdere PR-koppelingen, contextlimiet in UTF-8-bytes; v3.13.3.2 — bij merge na #1662 hernummeren naar 3.13.4.1). Beide raken `.github/workflows/claude-issue-turn.yml` (samenloop handmatig oplossen); de workflow werkt pas na release naar `main`.

**Eigenaarspunten na release:** (1) aanwijzing van `GET /api/planner/menu-toegang` als `ExecuteAuthenticatedAsync`-route (AGENTS.md); (2) standaardstand van de menu-matrix zonder opgeslagen rij (hoofditems aan voor alle rollen, WZ Instellingen/Admin alleen admin); (3) live 3-user-test van het menu; (4) blokvergelijking Planning vs. Veld optimalisatie (#1560) en bevestiging van de aanname "Sportlink-leidend = SportlinkSpeelduur > 0, blokduur speelduur + 15, app-buffer 0"; (5) e-mailtester-DryRun mét AI (uit de vorige HANDOFF).

**Lessen:** een `pull_request`-run start niet bij een merge-conflict (bewezen op tien heads, #1641); de bot-run van de issue-beurt verwerkt niet altijd (workflow-PR's, architect-issues) — eerst `gh run list --workflow claude-issue-turn.yml` en de branch-head controleren; een "waarom werkt dit niet"-uitleg in workflow-commentaar is publicatie (alleen het triggercontract benoemen); `Stop-Debug.ps1` crasht in deze omgeving op `AssemblyName.Parse` en stopt de `-Bewaak`-bewakers niet.

---

# HANDOFF — epic #1632 CA1502/CA1506 wegwerken (gestart 2026-10-11; code op `develop`, afsluit-PR #1652 open)

**Eigenaarsbesluit:** alle fases worden uitgevoerd; Claude bouwt, Codex reviewt; gestapelde PR-keten; de wijzigingen liften mee met de volgende release.

**Volgorde:** #1622 → #1623 → #1624 → #1625 → #1626 → #1627 → #1628 → #1629 → #1630 → #1631 → afsluiting (plafond 0, severity error).

**Stand:** #1622 in PR #1636 (review Codex); #1623 in PR #1638 (gestapeld op #1636); #1624 in PR #1640, gestapeld op #1638 (branch `feature/#1624-staging-parameterbinding`, geen versiebump); #1625 in PR (branch `feature/#1625-planner-shared-complexiteit`, gestapeld op #1640, bevat #1636; review Codex; analyzer-plafond 15 → 12); #1626 in PR (branch `feature/#1626-sportlink-client-complexiteit`, gestapeld op #1625; golden-tests vooraf; analyzer-plafond 12 → 9, interne duplicatie 622 → 610; eigenaarsbesluit V3: schrijfpaden gescheiden, alleen MatchData-opbouw gedeeld); #1627 in PR (branch `feature/#1627-template-antwoord-shared`, gestapeld op #1626; 52 golden-gevallen per tier vooraf, daarna `TemplateAntwoordBouwer` in Planner.Shared met per tier een adapter `TemplateAntwoordBron`; analyzer-plafond 9 → 8, tier-duplicatie 5216 → 5214; geen versiebump); #1628 in PR (branch `feature/#1628-verwerk-met-planner-cases`, gestapeld op #1627; 14 Postgres-integratietests en 9 SQL Server-tests vooraf op ongewijzigde code, daarna `VerwerkMetPlannerAsync` per verzoektype in `BerichtPipeline.Planner.cs` per tier plus `PlannerAntwoordJson` in Planner.Shared; analyzer-plafond 8 → 6, methodes-boven-grens 22 → 18, bestanden-boven-grens 13 → 12, tier-duplicatie 5214 → 5213; geen versiebump); #1629 in PR (branch `feature/#1629-emailprocessor-fasen`, gestapeld op #1628; `EmailProcessorFunction` per tier in partials `.Fasen.cs` (`VoerFase1LichtAsync`, `VoerFase2ZwaarAsync`, context-record `EmailRunContext`), `.Verwerking.cs` (`DetecteerLeermomentAsync`, `VerwerkBuitenScopeAsync`, `VoerReplyFlowMetTraceAsync`), `.Fouten.cs` en `.Noodmail.cs`; hoofdbestanden 1003/1081 → 184/194 regels; vier CA1506-meldingen weg, analyzer-plafond 6 → 2, bestanden-boven-grens 12 → 10, methodes-boven-grens 18 → 16; tier-duplicatie 5213 → 5287 VERHOOGD, eigenaarsgoedkeuring nodig; geen versiebump; #1631 in PR (branch `feature/#1631-sportlinkclubclient-domeinen`, gestapeld op #1629; `SportlinkClubClient` (103 typen) is een façade van 33 typen die delegeert naar `SportlinkWedstrijdLezer`, `SportlinkClubFormulierLezer`, `SportlinkWedstrijdDetails` en `SportlinkMutaties` rond de transportkern `SportlinkClubTransport` (enige route: `ExecuteWithTokenRetryAsync`, `SportlinkClubHttp.ZetSportlinkHeaders`), tokens in `SportlinkClubTokenBeheer`, mutatie-uitvoering in `SportlinkMutatieUitvoerder`; geen klasse boven 45 typen; interface, constructor en DI ongewijzigd; analyzer-plafond 2 → 1, interne duplicatie ongewijzigd 610; geen versiebump; let op: AGENTS.md noemt nog `SportlinkClubClient.ExecuteWithTokenRetryAsync`/`ZetSportlinkHeaders`, aanpassing vraagt een eigen opdracht). **#1630** in PR #1648 (gestapeld op develop na merge #1631): AdminApiClient vervangen door acht domeinclients rond `AdminApiTransport`; merge met develop gedaan, renderchecks in uitvoering, daarna review ronde 2.

**STAND 2026-10-11 (bij overdracht gecontroleerd tegen PR-status):** alle tien code-PR's zijn gemerged op `develop`: #1636 (#1622), #1638 (#1623), #1640 (#1624), #1642 (#1625), #1643 (#1626), #1644 (#1627), #1645 (#1628), #1646 (#1629), #1648 (#1630, gemerged 14:31) en #1649 (#1631); plus #1639 (#1637, zelfmerge-regel). `develop` meet 0 analyzer-overtredingen (nulmeting #1300: 19). **Nog open:** uitsluitend deze afsluit-PR #1652 (issue #1651: plafond-toelichting, severity `error`, AGENTS.md Sportlink-route, docs, deze HANDOFF); merge door de eigenaar. Niets hiervan is gereleased (awaiting-release).

**Gecombineerde verificatie op de eindstand (debugomgeving, versie 3.13.1.2, Postgres-tier):** `Test-DebugGo.ps1 -Offline -ZonderAI` = GO; rendercheck van 20 routes: allemaal HTTP 200, geen foutbanner, geen console-errors, versie in de header. E-mailtester-smoke beperkt tot route en foutafhandeling: lokaal is er geen OpenAI-sleutel, dus de dry-run (UI en `POST /api/test/email`) geeft een nette melding "IChatClient niet geconfigureerd" (HTTP 500 uit het endpoint, geen foutbanner); het scenario, het antwoord en de trace-stappen zijn dus NIET geverifieerd (vraagt een omgeving met sleutel).

**Verificatiestand:** rendercheck van 21 routes + layout geleverd (20 routes in PR #1648 en #1652; `/testdata/wedstrijden` apart op PR #1648, comment 14:2x, SHA 0dcb52c7: HTTP 200, 224 rijen ALLSTARS-testdata, geen DI-/console-fouten). **Releasepunt eigenaar (vóór `/release` R6–R7, gedelegeerd besluit in PR #1652):** e-mailtester-DryRun met synthetische testdata (`docs/ARCHITECTUUR.md` §8.1.5) in de omgeving met geconfigureerde IChatClient: scenario, antwoord en trace vastleggen. Lokaal is die smoke bewust niet mogelijk (geen AI-sleutel); uitstel met restrisico, geen geslaagde smoke.

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
