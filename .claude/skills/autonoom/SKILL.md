---
name: autonoom
description: "Ontwikkel toegewezen issues in eigen worktrees, laat de andere agent reviewen en bewaak CI. Merge uitsluitend met aparte eigenaarsautorisatie; reserveer de gedeelde debugomgeving vóór gebruik."
disable-model-invocation: false
argument-hint: "[--dry-run] [--features] [--release]"
---

> **Gezamenlijke agentregels zijn leidend (CLAUDE.md/AGENTS.md).** Deze skill geldt voor Codex
> en Claude Code. Werk uitsluitend aan de toegewezen taak in de eigen geverifieerde worktree;
> claim of wijzig geen taak, branch, worktree of services van een andere actieve sessie.
> Behoud `source:` als herkomst; registreer implementer, reviewer, fase en sessie afzonderlijk.
> Vóór merge: wederzijdse review van de huidige head-SHA, relevante checks én afzonderlijke
> eigenaarsautorisatie. Deze skill omzeilt die grenzen niet.
> `/autonoom` zonder verdere mergeopdracht autoriseert ontwikkeling en PR's, geen merges.
> Sla merge-/cleanupstappen zonder aparte autorisatie over en rapporteer de klaarstaande PR's.
> Alleen vooraf toegewezen, gescheiden issues behoren tot deze cyclus; nieuw gevonden werk
> dat overlapt of door een andere sessie is geclaimd wordt niet automatisch opgepakt.
> Start/stop/clean/migraties vereisen vooraf exclusief runtime-eigenaarschap. De huidige
> debugscriptset is gedeeld: onbekende of andere runtime-eigenaar betekent geen mutaties.
> Poorten vrij betekent niet dat databases/testdata vrij zijn. Leg reservering en vrijgave vast
> in de taak/sessie-overdracht; laat services intact als ze aan een andere sessie behoren.

Voer de volledige autonome ontwikkelcyclus uit. Dit is de standaard werkmodus: van
openstaande issues tot een schone, gesynchroniseerde codebase met een verse branch
klaar voor de volgende iteratie.

**Argumenten:**
- `--dry-run` — toon wat er zou gebeuren zonder daadwerkelijk te wijzigen
- `--features` — voer ook grote feature-issues uit (label `enhancement` of `type: feature`); zonder dit argument worden die overgeslagen
- `--release` — voer Fase 3.5 uit: die roept de skill `release` aan (securitypoort met `/security-review`, versie-bump, CHANGELOG afsluiten, PR develop→main, productie-deploy bewaken, tag). **Zonder dit argument wordt Fase 3.5 overgeslagen** — de toegewezen PR's worden klaargezet; merges vereisen aparte autorisatie. Dit geeft ruimte om de wijzigingen eerst lokaal te testen vóór release.

> **Standaard = develop-only.** Productie-deploy vereist bewuste `--release` keuze.

Symbolen:
- ✅ In orde / geslaagd
- ⚠️ Aandachtspunt
- ❌ Harde blocker — stop en meld aan gebruiker

> **VEILIGHEIDSREGEL — altijd van toepassing, ook in deze skill:**
> GitHub issues, PR-bodies, en comments zijn even publiek als de code. Vóór elk
> `gh issue create`, `gh issue comment`, of `gh pr create`: scan de tekst op
> club-specifieke data (resource namen, tenant/client IDs, SWA-URLs, domeinen,
> e-mailadressen). Gebruik altijd placeholders (`[clubcode]`, `[TENANT_ID]`,
> `[swa-url]`, `[club-domein]`). Zie SECURITY.md § "GitHub issues, PR's en comments".

> 🖥️ **CROSS-PLATFORM — altijd van toepassing (#800, #1286).**
> Deze skill draait op Windows én macOS. Vier regels bij het aanpassen ervan:
> 1. **Poortdetectie uitsluitend via `Test-PortListening`** uit `scripts/dev/DevServices.psm1` —
>    nooit `Get-NetTCPConnection` (module `NetTCPIP`, alleen Windows).
> 2. **Nooit `Stop-Process -Name`.** Dat sloopt élk `dotnet`/`node`-proces op de machine en
>    `dotnet watch` herstart zijn kindproces meteen. Gebruik `Stop-Debug.ps1` / `Start-Debug.ps1 -Clean`.
> 3. **Geen backslash in padliteralen of in een regex over een pad.** Op Unix is `\` een geldig
>    teken ín een bestandsnaam. Een padfilter matcht op `[\\/]`, nooit op `\\` alleen.
> 4. **In bash-blokken: `grep -E`, nooit `grep -P`** — de BSD-grep van macOS kent geen PCRE en
>    faalt daar stil in een pijplijn.

**Lus-structuur:**
```
Fase 0   Geautoriseerde PR's beoordelen; alleen eigen afgerond werk opruimen
Fase 0.5 Securityissues inventariseren; alleen toegewezen fixes uitvoeren
Fase 1   Toegewezen scope triageren en overlap controleren
Fase 2   Per taak eigen worktree, implementatie, checks en wederzijdse review
Fase 2b  Nieuwe feiten beoordelen binnen de toegewezen scope
Fase 3   Eigen branch en remote stand controleren zonder gedeelde checkout te wijzigen
Fase 3.5 Release alleen bij expliciete --release-opdracht, via skill release
Fase 4   Volgende toegewezen taak via S0; anders huidige werkstatus behouden
Fase 5   Debug alleen met exclusieve runtime-reservering
```

Securityblokkades worden altijd gemeld; ze geven geen toestemming andermans taak over te nemen.
Een nog open `awaiting-release`-issue betekent niet dat implementatie opnieuw moet worden gedaan.

---

## FASE 0 — VOORBEREIDING: PR's mergen + opruimen

### 0a. Open PR's met groene CI samenvoegen

```powershell
gh pr list --state open --json number,title,headRefName,statusCheckRollup
```

Voor elke expliciet voor merge geautoriseerde PR (andere PR's alleen rapporteren):
1. Haal CI-status op: `gh pr checks <nr>`
2. Is de wederzijdse review op de huidige SHA afgerond, merge geautoriseerd, Security Gate ✅
   en zijn alle toepasselijke checks ✅ (skipped alleen waar toegestaan)? → merge:
   ```powershell
   gh pr merge <nr> --merge --delete-branch
   ```
3. Is Security Gate ❌? → sla over, noteer als blocker.
4. Zijn sommige checks nog pending? → sla over (later in de cyclus controleren).

Rapporteer: welke PR's gemerged, welke overgeslagen en waarom.

### 0b. Merged branches opruimen (lokaal)

Fetch remote-refs zonder gedeelde checkouts te veranderen. Verwijder alleen eigen,
bevestigd gemergede branches/worktrees zonder actieve sessie of onverwerkt werk, volgens S0.
Geen globale branchcleanup; een verdwenen remote is geen bewijs dat lokaal werk overbodig is.

### 0c. Remote main bijwerken zonder checkout

```powershell
git fetch origin main develop
```

Lees productie-inhoud via `git show origin/main:<pad>`; wissel de hoofd-checkout niet.

### 0d. Deploy-workflow op main verifiëren

```powershell
$run = (gh run list --branch main --workflow deploy.yml --limit 1 --json databaseId,conclusion | ConvertFrom-Json)[0]
```

- `conclusion = "success"` → ✅ productie is live en gezond
- `conclusion = "failure"` → ❌ HARDE BLOCKER — productie is kapot, fix eerst
- `conclusion = "in_progress"` → wacht max 3 minuten en controleer opnieuw

Verplichte per-job check als de run recent (< 1 uur) is:
```powershell
gh run view $run.databaseId --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
```

Alle jobs `success` of `skipped`? → ✅

Toon: versienummer uit health endpoint: `Invoke-RestMethod http://localhost:7094/api/health`

---

## FASE 0.5 — SECURITY GATE (hard stop)

Dit is geen optionele stap. De skill loopt pas verder als deze fase volledig groen is.

### Stap: haal alle open security issues op

```powershell
gh issue list --state open --label "security" --json number,title,labels,body
```

### Als er open security issues zijn

Beoordeel alle open securityissues. Implementeer alleen vooraf toegewezen issues zonder
scope-overlap via Fase 2; securityprioriteit verleent geen eigenaarschap over andermans werk.
Een issue met `status: awaiting-release` blijft terecht open en start geen herimplementatielus.

Niet autonoom oplosbaar, niet toegewezen of door een andere sessie beheerd? Rapporteer de
blokkade aan de eigenaar. Geen nieuwe werkclaim of wijziging van andermans labels. Laat de
release geblokkeerd zolang de toepasselijke securitypoort niet groen is; ga niet zelf andermans
fixes implementeren om die blokkade op te heffen.

### Als er geen open security issues zijn

✅ Security Gate geslaagd — ga verder naar Fase 1.

---

## FASE 1 — ISSUE-TRIAGE: wat kan nu?

### 1a. Open issues ophalen

```powershell
gh issue list --state open --limit 50 --json number,title,labels,body
```

### 1b. Classificeer elk issue

Analyseer elk issue en deel in:

**UITVOERBAAR-KLEIN** (standaard — altijd uitvoeren):
- Labels: `bug`, `fix`, `security`, `chore`, `docs` — of kleine verbetering zonder nieuw scherm/endpoint
- Criterium: technisch helder, geen beslissing van eigenaar vereist, alle data aanwezig in codebase
- Past binnen de gratis Azure-stack (geen nieuwe betaalde resources)
- Scope: ≤ ~3 bestanden, ≤ ~1 dag werk, geen nieuwe tabel/pagina nodig

**UITVOERBAAR-GROOT** (alleen met `--features`):
- Labels: `enhancement`, `type: feature`, `epic`
- Meerdere nieuwe bestanden, nieuwe DB-tabel, nieuwe Blazor-pagina, of nieuwe API-endpoints
- Technisch helder en volledig gespecificeerd — maar groot genoeg om expliciet te plannen
- **Zonder `--features`**: sla over, voeg geen label toe (eigenaar kiest zelf wanneer)

**WACHT OP EIGENAAR** (sla over, voeg label toe):
- Al gelabeld `wacht op: eigenaar` / `waiting-for-owner` → altijd overslaan
- Architectuurregel onduidelijk, AVG/security afweging nodig, budget/infra wijziging vereist
- Issue is te vaag om te implementeren zonder aanvullende specificatie

**GEBLOKKEERD** (sla over):
- Afhankelijk van een ander issue dat nog niet klaar is
- CI rood op main (zie Fase 0d)

Toon de indeling voordat je begint met implementeren. Toon ook welke grote features overgeslagen worden en waarom.

### 1c. Label wacht-issues

Voor elk toegewezen "wacht op eigenaar" issue dat nog geen passend label heeft:
```powershell
gh issue edit <nr> --add-label "waiting-for-owner"
```

Voeg een kort comment toe met de open vraag:
```powershell
gh issue comment <nr> --body "⏸️ Wacht op eigenaar — [open vraag hier]"
```

---

## FASE 2 — IMPLEMENTATIE: uitvoerbare issues

Verwerk de uitvoerbare issues in volgorde van prioriteit (laagste nummer eerst, tenzij
er afhankelijkheden zijn).

Voor elk issue — volg de autonome ontwikkelcyclus uit CLAUDE.md:

### Per-issue stappen

**Stap A — Implementeer**
- Controleer taakclaim, scope-overlap en eigen branch/worktree volgens S0 vóór wijzigingen.
- Lees het volledige issue: `gh issue view <nr>`
- Implementeer alle betrokken lagen tegelijk: DB → API → Blazor GUI (nooit één laag alleen)
- Controleer: ClubCode discriminator, UTC in DB, GUI synchroon met code, geen club-specifieke strings

**Stap B — Verificatielus (max 3 iteraties)**
```
a. dotnet build FunctionApp.Postgres/FunctionApp.Postgres.csproj -c Debug
   → fouten? Fix, terug naar a.
   Dit is de tier die in productie draait (#1060). Raak je ook de SQL Server-tier aan, bouw
   dan óók: dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug

b. ALLEEN als de BlazorAdmin dev server niet draait: dotnet build BlazorAdmin/BlazorAdmin.csproj
   → fouten? Fix, terug naar a.
   ⚠️ NOOIT terwijl `/startdebug` (dotnet watch op :5242) al draait of vlak nadat die gestart is —
   een tweede compilatiepas geeft een tweede set content-hash fingerprints naast de draaiende
   server → 404 op framework-JS → "An unhandled error has occurred. Reload". Draait de server
   al (`lsof -nP -iTCP:5242 -sTCP:LISTEN` / `Get-NetTCPConnection -LocalPort 5242`)? Sla deze
   stap over — stap a dekt de compileerbaarheid van de FunctionApp-lagen al.

c. ./scripts/dev/Test-App.ps1
   → exit 1? Fix, terug naar a.
```

Als FunctionApp C# gewijzigd is → stop FunctionApp en herstart (geen hot reload op de
isolated worker):
```powershell
./scripts/dev/Stop-Debug.ps1
./scripts/dev/Start-Debug.ps1      # pollt zelf /api/health en faalt met exit 1 als de host niet opkomt
```

**Stap C — Documentatie bijwerken (verplicht vóór commit)**

Loop onderstaande twee categorieën na. Lees elk relevant bestand, vergelijk met de gemaakte wijziging, en update wat niet meer klopt. Verouderde informatie is erger dan geen informatie.

**Categorie 1 — Technische documentatie** (voor ontwikkelaars en agents):

| Bestand | Bijwerken bij |
|---|---|
| `CLAUDE.md` | Architectuurregel, buildproces, conventie of deployment-constraint gewijzigd |
| `FunctionApp/CLAUDE.md` | Endpoint, datamodel, API-veld of FunctionApp-configuratie gewijzigd |
| `docs/API.md` | Endpoint toegevoegd, gewijzigd of verwijderd |
| `docs/openapi.yaml` | Idem — sync met API.md |
| `docs/ARCHITECTUUR-PLANNER.md` | Planner-logica, pipeline of kanaalstrategie gewijzigd |
| `docs/ENTRA-AUTH-BEHEER.md` | Auth-configuratie, Easy Auth, Entra of rollen gewijzigd |
| `docs/VERIFICATIE-SCRIPTS.md` | Testscript, schema-controle of endpoint-verificatie gewijzigd |
| `docs/MONITORING.md` | Alerting, KQL-queries of escalatiematrix gewijzigd |
| `docs/EMAIL-VERWERKING.md` | Email-pipeline, kanalen of AI-verwerking gewijzigd |
| `docs/VERSIONING.md` | Release-proces of semver-afspraken gewijzigd |
| `SECURITY.md` | Security-beleid, AVG-regels of secrets-protocol gewijzigd |

**Categorie 2 — Gebruikers-handleidingen** (voor beheerders die de Admin GUI gebruiken):

| Bestand | Bijwerken bij |
|---|---|
| `docs/BEHEERDER-HANDLEIDING.md` | **Altijd** als er een scherm, instelling, knop, workflow of tekst in de GUI gewijzigd is |
| `docs/DEVELOPER-SETUP.md` | Lokale setup of configuratiestappen gewijzigd |
| `README.md` | Publieke beschrijving, architectuuroverzicht of quick-start gewijzigd |

**CHANGELOG.md — altijd bijwerken:**
Voeg een entry toe onder `## [Unreleased]` in de juiste sectie (`### Added`, `### Fixed`, `### Changed`, `### Security`). Schrijf voor de gebruiker, niet de developer: "Beheerders kunnen nu X" in plaats van "Methode Y aangepast".

**Stap D — Commit + PR**
```powershell
git add <specifieke bestanden>   # NOOIT git add -A of git add .
git commit -m "fix(#<nr>): ..."   # of feat(#<nr>): voor features

git push -u origin <branch-naam>
gh pr create --draft --base develop --title "..." --body-file <pr-body>
# Alleen urgente productiefix vanuit origin/main: --base main. Volg daarna de backportregel.
```

**Stap E — CI bewaken**
```powershell
gh pr checks <pr-nr> --watch
```

**Als CI rood (Security Gate ❌ of buildfouten na 3 fixpogingen):**
Stop, behoud eigen branch/worktree en maak de PR niet ready. Rapporteer fout, checks,
werkstatus en open beslissing aan de eigenaar. Geen `checkout main`, branchverwijdering,
remote-delete of terugdraaien van andermans werk. Een rode Security Gate blokkeert merge.

**Stap F — POORT 1: Pre-merge GO/NO-GO (vóór elke merge naar main)**

> **main IS productie** — elke merge triggert een automatische deploy naar Azure.
> Dit is een harde checklist, geen aanbeveling.

Features gaan naar `develop`; deze productiechecklist geldt alleen voor afzonderlijk
geautoriseerde hotfix/release-merges naar `main`. Voor iedere merge: huidige head-SHA
reviewen door de andere agent en bevindingen verwerken. Controleer vóór productie bovendien:

```
□ CI volledig groen (alle jobs ✅ of skipped — inclusief Security Gate)
□ Geen hardcoded club-identifiers: scan op resourcenamen, tenant/client IDs,
  Azure URLs, SWA-URLs, domeinnamen
    → gh pr diff <nr> | grep -iE "(func-|swa-|\.azurewebsites\.|\.database\.windows\.|[0-9a-f]{8}-[0-9a-f]{4})"
    → Resultaat leeg? ✅ | Iets gevonden? ❌ fix eerst
□ Geen persoonsgegevens in gewijzigde code/docs/comments (namen, e-mailadressen)
□ CHANGELOG [Unreleased] bevat een entry voor dit issue
□ Geen nieuwe betaalde Azure-resources toegevoegd (kosten check)
□ API-responses bevatten geen velden die e-mail/telefoon/naam lekken naar de client
  (controleer gewijzigde Function-bestanden — zoek op: EmailAdres, Telefoonnummer,
   naam-velden in SQL-queries die als JSON teruggaan)
```

Alle vakjes ✅, wederzijdse review afgerond en merge expliciet geautoriseerd? → **GO: merge**
```powershell
gh pr merge <pr-nr> --merge --delete-branch
```

Één of meer ❌? → **NO-GO: niet mergen**
- Fix het probleem op de branch en push opnieuw
- Of behoud de draft-PR en wacht op de eigenaar (zie CI-rood procedure hierboven)

Na geslaagde merge:
```powershell
# Wacht op deploy-workflow + verplichte per-job check
gh run list --branch main --workflow deploy.yml --limit 1 --json databaseId | ConvertFrom-Json
gh run watch <run-id> --exit-status
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
# Alle jobs success/skipped? → ✅ code staat live
```

> **Nooit hier zelf `gh issue close` aanroepen (#1295).** Dat sluiten hoort bij een version-tag op
> `main` en gebeurt automatisch via `close-released-issues.yml` — die workflow verwijdert ook de
> status-labels, wat een handmatige `gh issue close` niet doet en dan een `status:
> awaiting-release`-label op een gesloten issue achterlaat. Rapporteer dit issue pas als gesloten
> zodra die workflow na een release-tag daadwerkelijk groen is gedraaid
> (`gh run list --workflow close-released-issues.yml --limit 1 --json conclusion`) — conform de
> hotfix-uitzondering in CLAUDE.md, "Issue-lifecycle: awaiting-release". Is er nog geen release-tag
> gepland? Dan blijft het issue open met `status: awaiting-release` totdat die er komt.

### Één branch per batch of per issue?

- Meerdere kleine gerelateerde issues (zelfde laag, zelfde pagina): één branch, één PR
- Grote feature of architectuurwijziging: eigen branch
- Branch en worktree volgens S0: Codex `codex/<nr>-<slug>`, Claude Code `feature/#<nr>-<slug>`; één implementer per batch.

---

## FASE 2b — HERCHECK: zijn er nieuwe issues bijgekomen?

Na het afronden van Fase 2 (toegewezen uitvoerbare scope afgerond), **altijd** opnieuw controleren of er nieuwe issues zijn aangemaakt — bijv. door Dependabot, security scans, CI-alerts, of door de eigenaar tijdens de implementatie.

### Stap: haal alle open issues opnieuw op

```powershell
gh issue list --state open --limit 50 --json number,title,labels,createdAt,body
```

Vergelijk de resultaten met de lijst uit Fase 1:
- Zijn er nieuwe issues? → inventariseer; implementeer alleen binnen de vooraf toegewezen, niet-overlappende scope.
- Zijn er issues die eerder "wacht op eigenaar" waren maar nu genoeg context hebben? → herclassificeer
- Nieuwe securityissues/alerts hebben hoge prioriteit; rapporteer ze en voer alleen toegewezen, niet-overlappende taken uit.

### Herhaallus (zolang er uitvoerbare issues zijn)

```
HERHAAL:
  1. Voer gh issue list uit
  2. Classificeer (zie Fase 1b)
  3. Zijn er toegewezen, niet-overlappende uitvoerbare issues? → voer Fase 2 uit voor die scope
  4. Is de toegewezen scope afgerond? → STOP lus, ga naar Fase 3

MAX ITERATIES: onbeperkt, maar na > 5 rondes zonder voortgang → meld aan gebruiker
```

**Loopterm:** ga pas verder wanneer de vooraf toegewezen, gescheiden scope is afgerond.
Rapporteer resterende open issues; neem andermans werk niet over om de globale lijst leeg te maken.

---

## FASE 3 — SYNC CHECK: lokaal = online

Na afronding van de toegewezen PR's en groene CI (merges alleen indien geautoriseerd):

### 3a. Remote stand lezen

```powershell
git fetch origin main develop
```

### 3b. Versie vergelijken

Lees de csproj-versie via `git show origin/main:FunctionApp/fa-dev-sportlink-01.csproj` voor
productie, en `origin/develop` voor integratie. De lokale :7094-health is een debugversie,
geen bewijs van productie. Wijzig geen gedeelde checkout om deze vergelijking te doen.

### 3c. Ongepushte eigen commits?

Vergelijk de eigen branch met zijn upstream. Push uitsluitend de eigen toegewezen branch;
nooit direct naar `main` of `develop`. Ontbreekt een upstream, leg die vast bij de eigen push.

### 3d. Deploy-workflow opnieuw controleren na merges

```powershell
gh run list --branch main --workflow deploy.yml --limit 1
```

Wacht op groen als er net een merge was. Verplichte per-job check (zie Fase 0d).

---

## FASE 3.5 — POORT 2: RELEASE (alleen met `--release`)

> **⚠️ DEZE FASE WORDT ALLEEN UITGEVOERD ALS `--release` IS MEEGEGEVEN.**
>
> Zonder `--release`: sla Fase 3.5 volledig over en ga direct naar Fase 4.
> Meld dan aan de gebruiker:
> "✅ Toegewezen ontwikkelscope afgerond — rapporteer afzonderlijk welke PR's klaarstaan en welke geautoriseerd zijn gemerged.
> Start `/release` (of `/autonoom --release`) als je klaar bent om naar productie te gaan."

Met `--release`: **roep de skill `release` aan** (`Skill({ skill: "release" })`) en volg die
volledig. Hier staat bewust geen eigen kopie van de releasestappen meer (#1470).

> Tot #1470 stond hier een eigen releaseprocedure. Die was uit de pas gelopen met de werkelijkheid:
> een rechtstreekse push naar `develop`, een versiebump in twee van de drie csproj's, geen
> OpenAPI-versie, en een database-check op de SQL Server-tier. Bovendien ontbrak `/security-review`.
> Eén procedure op één plek voorkomt dat dit opnieuw gebeurt.

Poort 2 is geslaagd als `/release` zijn eindrapport zonder ❌ afsluit. Een STOP uit de
securitypoort van `/release` is ook een STOP voor deze cyclus: ga niet door naar Fase 4, en meld
de stopconditie aan de eigenaar.

---

## FASE 4 — VOLGENDE TAAK

Maak geen generieke iteratiebranch en wissel nooit de gedeelde checkout. Voor een volgende
vooraf toegewezen taak: controleer overlap en maak een nieuwe eigen worktree vanaf
`origin/develop` volgens S0, met issue- en sessie-identificatie. Zonder volgende opdracht:
behoud de huidige worktree en rapporteer de eindstand.

---

## FASE 5 — DEBUG STARTEN

### 5a. Check lopende services

```powershell
# Poortdetectie uitsluitend via DevServices.psm1 — Get-NetTCPConnection zit in de module
# NetTCPIP en bestaat alleen op Windows (#800, #1286).
Import-Module ./scripts/dev/DevServices.psm1 -Force
$p   = Get-DebugPorts
$fa  = Test-PortListening -Port $p.FunctionApp
$bl  = Test-PortListening -Port $p.BlazorAdmin
$az  = Test-PortListening -Port $p.Azurite
```

### 5b. Start wat ontbreekt

Als alle drie al draaien → health-check en klaar:
```powershell
Invoke-RestMethod http://localhost:7094/api/health
```

Als iets mist én deze sessie de exclusieve runtime-eigenaar is → start opnieuw.
Bij andere/onbekende eigenaar: rapporteer de ontbrekende check en laat services intact:
```powershell
# Nooit Stop-Process -Name: dat sloopt élk dotnet/node-proces op de machine, en 'dotnet watch'
# herstart zijn kindproces meteen — poort 5242 is dan direct weer bezet (CLAUDE.md).
./scripts/dev/Start-Debug.ps1 -Clean   # stopt, cleant de stale fingerprints en start opnieuw

# Start-Debug pollt zelf tot de services gereed zijn; een vaste Start-Sleep is niet nodig.
Invoke-RestMethod http://localhost:7094/api/health
(Invoke-WebRequest http://localhost:5242/).StatusCode
```

Na elke FunctionApp C#-wijziging in Fase 2 → FunctionApp opnieuw starten (geen hot reload).
BlazorAdmin hot reload via `dotnet watch` — draait automatisch bij Stap 2B als Start-Debug al liep.

---

## EINDRAPPORT

```
## Autonome cyclus — resultaat

### Fase 0 — Voorbereiding
| PR gemerged | #... |
| Branches opgeruimd | ... |
| main up-to-date | ✅/❌ |
| Deploy-workflow | ✅/❌ |

### Fase 0.5 — Security Gate
| Open security issues bij start | #... of geen |
| Opgelost | #... of n.v.t. |
| Geblokkeerd (wacht op eigenaar) | #... of geen |
| Status | ✅ Schoon / ❌ GESTOPT — eigenaar actie vereist |

### Fase 1 — Issues
| Uitgevoerd (klein) | #... |
| Uitgevoerd (groot, --features) | #... of n.v.t. |
| Overgeslagen groot (geen --features) | #... |
| Overgeslagen (wacht op eigenaar) | #... |
| Geblokkeerd | #... |

### Fase 2 — Implementatie
[per issue: wat gedaan, PR-URL, CI-status, gesloten ja/nee]

### Teruggedraaid
[per issue: reden, branch verwijderd, comment geplaatst, label gezet]

### Fase 2b — Hercheck
| Herlus-rondes | n |
| Nieuwe issues gevonden | #... of geen |

### Fase 3 — Sync
| Lokaal = online | ✅/⚠️ |
| Versie consistent | ✅/⚠️ |

### Fase 4 — Iteratie-branch
| Branch | feature/v{x.y}-iteratie |

### Fase 5 — Debug
| FunctionApp :7094 | ✅/❌ |
| BlazorAdmin :5242 | ✅/❌ |
| Azurite :10000   | ✅/❌ |
```

Sluit af met: aanbevolen volgende actie voor de gebruiker (bijv. testen van geïmplementeerde features, of een issue dat wacht op hun beslissing).

---

## Escaleer naar gebruiker bij (en alleen bij)

- Security Gate blijft rood na fixpoging
- Deploy-workflow op main gefaald (productie kapot)
- > 3 build-iteraties zonder voortgang
- Architectuurkeuze met meerdere gelijkwaardige paden
- AVG/CISO-blokkade die codekeuze vereist
- Issue vereist betaalde Azure-resource
- Versie-bump beslissing (MAJOR of MINOR zonder duidelijk issue)
