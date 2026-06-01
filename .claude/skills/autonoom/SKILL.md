---
description: Volledige autonome ontwikkelcyclus — PR's mergen, branches opruimen, lokaal/online synchen, open issues uitvoeren, iteratie-branch aanmaken, debug starten.
disable-model-invocation: false
argument-hint: [--dry-run] [--features] [--release]
---

Voer de volledige autonome ontwikkelcyclus uit. Dit is de standaard werkmodus: van
openstaande issues tot een schone, gesynchroniseerde codebase met een verse branch
klaar voor de volgende iteratie.

**Argumenten:**
- `--dry-run` — toon wat er zou gebeuren zonder daadwerkelijk te wijzigen
- `--features` — voer ook grote feature-issues uit (label `enhancement` of `type: feature`); zonder dit argument worden die overgeslagen
- `--release` — voer Fase 3.5 uit: versie-bump, CHANGELOG afsluiten, PR develop→main, tag aanmaken en productie-deploy bewaken. **Zonder dit argument stopt de cyclus na Fase 2b** — alle issues zijn geïmplementeerd op develop, maar er wordt niets naar productie gepusht. Dit geeft ruimte om de wijzigingen eerst lokaal te testen vóór release.

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

**Lus-structuur (belangrijk):**
```
Fase 0  (voorbereiding: PR's mergen, branches opruimen, main synchen)
  → Fase 0.5 — SECURITY GATE (hard stop — nooit overslaan)
       Open security issues aanwezig?
         → Oplosbaar autonoom? → Oplossen (lus totdat leeg)
         → Niet oplosbaar?     → Escaleer naar eigenaar, STOP HIER
       Nul open security issues? → ✅ verder naar Fase 1
  → Fase 1 (issue-triage: klein uitvoerbaar / groot / wacht / geblokkeerd)
    → Fase 2 (implementatie: klein zonder vlag, groot met --features)
      ↘ Fase 2b (hercheck nieuwe issues)
         → Zijn er nieuwe uitvoerbare issues? → terug naar Fase 2
         → Geen uitvoerbare issues meer?
           → Fase 3 (sync lokaal = online)
             → Fase 3.5 — POORT 2 (alleen met --release; anders: stop + melding)
               → Fase 4 (nieuwe iteratie-branch)
                 → Fase 5 (debug starten)
```

**Garanties:**
- Fase 0.5 wordt ALTIJD uitgevoerd, ook als `--features` niet is meegegeven
- De skill verlaat Fase 0.5 pas als er **nul open security issues** zijn
- Fase 3/4/5 worden pas bereikt als de volledige lus Fase 1 → 2 → 2b leeg is

---

## FASE 0 — VOORBEREIDING: PR's mergen + opruimen

### 0a. Open PR's met groene CI samenvoegen

```powershell
gh pr list --state open --json number,title,headRefName,statusCheckRollup
```

Voor elke open PR:
1. Haal CI-status op: `gh pr checks <nr>`
2. Is Security Gate ✅ en alle andere checks ✅ of skipped? → merge:
   ```powershell
   gh pr merge <nr> --merge --delete-branch
   ```
3. Is Security Gate ❌? → sla over, noteer als blocker.
4. Zijn sommige checks nog pending? → sla over (later in de cyclus controleren).

Rapporteer: welke PR's gemerged, welke overgeslagen en waarom.

### 0b. Merged branches opruimen (lokaal)

```powershell
# Fetch en prune remote-refs die niet meer bestaan
git fetch --prune

# Verwijder lokale branches waarvan de remote al weg is
git branch -vv | Where-Object { $_ -match '\[origin/.*: gone\]' } |
    ForEach-Object { ($_ -split '\s+')[1] } |
    ForEach-Object { git branch -d $_ }
```

Rapporteer welke branches verwijderd zijn.

### 0c. Lokaal naar main synchen

```powershell
git checkout main
git pull origin main
```

Verifieer daarna: `git status` → schoon + up-to-date.

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

Verwerk elk security issue via de volledige implementatiecyclus (zie Fase 2 per-issue stappen A t/m E). Security issues volgen **altijd** de grote-feature regels niet — ze worden altijd direct uitgevoerd, ongeacht scope of omvang.

**Lus totdat leeg:**
```
HERHAAL:
  1. gh issue list --state open --label "security"
  2. Zijn er issues? → Implementeer elk issue (stappen A-E)
  3. Na merge: gh issue close <nr>
  4. Zijn er daarna nog open security issues? → terug naar 1
  5. Geen security issues meer? → ✅ STOP lus
```

### Als een security issue niet autonoom oplosbaar is

```powershell
gh issue edit <nr> --add-label "wacht op: eigenaar"
gh issue comment <nr> --body "⏸️ Security issue — wacht op eigenaar. Open vraag: [beschrijf wat ontbreekt om dit op te lossen]"
```

**→ STOP de volledige cyclus hier.** Fase 1 en verder worden niet uitgevoerd zolang er een security issue open staat dat niet autonoom oplosbaar is. Meld dit expliciet aan de gebruiker.

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

Voor elk "wacht op eigenaar" issue dat nog geen passend label heeft:
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
- Lees het volledige issue: `gh issue view <nr>`
- Implementeer alle betrokken lagen tegelijk: DB → API → Blazor GUI (nooit één laag alleen)
- Controleer: ClubCode discriminator, UTC in DB, GUI synchroon met code, geen club-specifieke strings

**Stap B — Verificatielus (max 3 iteraties)**
```
a. dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug
   → fouten? Fix, terug naar a.

b. dotnet build BlazorAdmin/BlazorAdmin.csproj
   → fouten? Fix, terug naar a.

c. .\scripts\dev\Test-App.ps1
   → exit 1? Fix, terug naar a.
```

Als FunctionApp C# gewijzigd is → stop FunctionApp en herstart:
```powershell
Stop-Process -Name "func" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp; func start --port 7094"
Start-Sleep -Seconds 15
Invoke-RestMethod http://localhost:7094/api/health
```

**Stap C — Documentatie bijwerken (verplicht vóór commit)**

Loop onderstaande twee categorieën na. Lees elk relevant bestand, vergelijk met de gemaakte wijziging, en update wat niet meer klopt. Verouderde informatie is erger dan geen informatie.

**Categorie 1 — Technische documentatie** (voor ontwikkelaars en Claude):

| Bestand | Bijwerken bij |
|---|---|
| `CLAUDE.md` | Architectuurregel, buildproces, conventie of deployment-constraint gewijzigd |
| `FunctionApp/CLAUDE.md` | Endpoint, datamodel, API-veld of FunctionApp-configuratie gewijzigd |
| `docs/API.md` | Endpoint toegevoegd, gewijzigd of verwijderd |
| `docs/openapi.yaml` | Idem — sync met API.md |
| `docs/ARCHITECTURE-PLANNER.md` | Planner-logica, pipeline of kanaalstrategie gewijzigd |
| `docs/AZURE-ENTRA-SETUP.md` | Auth-configuratie, Easy Auth, Entra of rollen gewijzigd |
| `docs/TESTING.md` | Testscript, schema-controle of endpoint-verificatie gewijzigd |
| `docs/MONITORING.md` | Alerting, KQL-queries of escalatiematrix gewijzigd |
| `docs/EMAIL-VERWERKING.md` | Email-pipeline, kanalen of AI-verwerking gewijzigd |
| `docs/VERSIONING.md` | Release-proces of semver-afspraken gewijzigd |
| `SECURITY.md` | Security-beleid, AVG-regels of secrets-protocol gewijzigd |

**Categorie 2 — Gebruikers-handleidingen** (voor beheerders die de Admin GUI gebruiken):

| Bestand | Bijwerken bij |
|---|---|
| `docs/v2-admin-handleiding.md` | **Altijd** als er een scherm, instelling, knop, workflow of tekst in de GUI gewijzigd is |
| `docs/SETUP.md` | Lokale setup of configuratiestappen gewijzigd |
| `README.md` | Publieke beschrijving, architectuuroverzicht of quick-start gewijzigd |

**CHANGELOG.md — altijd bijwerken:**
Voeg een entry toe onder `## [Unreleased]` in de juiste sectie (`### Added`, `### Fixed`, `### Changed`, `### Security`). Schrijf voor de gebruiker, niet de developer: "Beheerders kunnen nu X" in plaats van "Methode Y aangepast".

**Stap D — Commit + PR**
```powershell
git add <specifieke bestanden>   # NOOIT git add -A of git add .
git commit -m "fix(#<nr>): ..."   # of feat(#<nr>): voor features

git push -u origin <branch-naam>
gh pr create --base main --title "..." --body "Closes #<nr>"
```

**Stap E — CI bewaken**
```powershell
gh pr checks <pr-nr> --watch
```

**Als CI rood (Security Gate ❌ of buildfouten na 3 fixpogingen):**
```powershell
# 1. Draai de branch-commit(s) terug
git checkout main
git branch -D <branch-naam>
git push origin --delete <branch-naam>

# 2. Laat issue open + label + comment
gh issue edit <nr> --add-label "wacht op: eigenaar"
gh issue comment <nr> --body "⏸️ Implementatie teruggedraaid — [beschrijf hier concreet wat er mis ging en wat de open vraag is]. CI-fout: [fout-samenvatting]"
```
Security Gate ❌ of > 3 iteraties zonder voortgang → altijd terugdraaien, nooit half-werkende code laten staan.

**Stap F — POORT 1: Pre-merge GO/NO-GO (vóór elke merge naar main)**

> **main IS productie** — elke merge triggert een automatische deploy naar Azure.
> Dit is een harde checklist, geen aanbeveling.

Controleer elk punt voordat je `gh pr merge` aanroept:

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

Alle vakjes ✅? → **GO: merge**
```powershell
gh pr merge <pr-nr> --merge --delete-branch
```

Één of meer ❌? → **NO-GO: niet mergen**
- Fix het probleem op de branch en push opnieuw
- Of draai terug + wacht op eigenaar (zie CI-rood procedure hierboven)

Na geslaagde merge:
```powershell
# Wacht op deploy-workflow + verplichte per-job check
gh run list --branch main --workflow deploy.yml --limit 1 --json databaseId | ConvertFrom-Json
gh run watch <run-id> --exit-status
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
# Alle jobs success/skipped? → ✅
gh issue close <nr> --comment "✅ Geïmplementeerd, Poort 1 geslaagd, gemerged in PR #<pr-nr>."
```

### Één branch per batch of per issue?

- Meerdere kleine gerelateerde issues (zelfde laag, zelfde pagina): één branch, één PR
- Grote feature of architectuurwijziging: eigen branch
- Branchnaam: `feature/#<nr>-<slug>` (voor primaire issue)

---

## FASE 2b — HERCHECK: zijn er nieuwe issues bijgekomen?

Na het afronden van Fase 2 (alle uitvoerbare issues geïmplementeerd), **altijd** opnieuw controleren of er nieuwe issues zijn aangemaakt — bijv. door Dependabot, security scans, CI-alerts, of door de eigenaar tijdens de implementatie.

### Stap: haal alle open issues opnieuw op

```powershell
gh issue list --state open --limit 50 --json number,title,labels,createdAt,body
```

Vergelijk de resultaten met de lijst uit Fase 1:
- Zijn er issues aangemaakt **ná** de start van deze cyclus? → verwerk ze opnieuw via Fase 1b (classificeren) en zo nodig Fase 2 (implementeren)
- Zijn er issues die eerder "wacht op eigenaar" waren maar nu genoeg context hebben? → herclassificeer
- Zijn er security-issues, Dependabot-alerts of CI-gegenereerde issues? → hoge prioriteit, direct oppakken als uitvoerbaar

### Herhaallus (zolang er uitvoerbare issues zijn)

```
HERHAAL:
  1. Voer gh issue list uit
  2. Classificeer (zie Fase 1b)
  3. Zijn er uitvoerbare issues? → voer Fase 2 uit voor elk van hen
  4. Zijn er GEEN uitvoerbare issues meer? → STOP lus, ga naar Fase 3

MAX ITERATIES: onbeperkt, maar na > 5 rondes zonder voortgang → meld aan gebruiker
```

**Loopterm:** ga pas verder met Fase 3 als er **geen enkele open uitvoerbare issue** meer bestaat — niet wanneer de huidige batch klaar is.

---

## FASE 3 — SYNC CHECK: lokaal = online

Na alle PR's gemerged en CI groen:

### 3a. Haal main opnieuw op

```powershell
git checkout main
git pull origin main
```

### 3b. Versie vergelijken

```powershell
# Versie in csproj
Select-String -Path "FunctionApp/fa-dev-sportlink-01.csproj" -Pattern "<Version>"

# Versie op productie (als services draaien)
(Invoke-RestMethod http://localhost:7094/api/health).version
```

Zijn CHANGELOG `[Unreleased]` en csproj-versie consistent? ✅ / divergentie? ⚠️

### 3c. Ongepushte commits?

```powershell
git log origin/main..HEAD --oneline
```

Geen output → ✅. Commits aanwezig → push:
```powershell
git push origin main   # alleen als direct op main; anders via PR
```

### 3d. Deploy-workflow opnieuw controleren na merges

```powershell
gh run list --branch main --workflow deploy.yml --limit 1
```

Wacht op groen als er net een merge was. Verplichte per-job check (zie Fase 0d).

---

## FASE 3.5 — POORT 2: RELEASE GO/NO-GO (vóór versie-bump + tag)

> **⚠️ DEZE FASE WORDT ALLEEN UITGEVOERD ALS `--release` IS MEEGEGEVEN.**
>
> Zonder `--release`: sla Fase 3.5 volledig over en ga direct naar Fase 4.
> Meld dan aan de gebruiker:
> "✅ Cyclus voltooid op develop — alle issues geïmplementeerd en gemerged.
> Start `/autonoom --release` als je klaar bent om naar productie te gaan."

> **Poort 2 is zwaarder dan Poort 1.** Poort 1 bewaakt één PR (per merge naar main).
> Poort 2 bewaakt de codebase als geheel vóórdat een versienummer en tag worden
> aangemaakt — dat is het formele moment dat een release "live" is voor alle clubs
> die de repo gebruiken. Sla deze fase nooit over als `--release` aanwezig is.

Dit is een volledige security- en kwaliteitsaudit van de huidige staat van `main`.
Voer alleen uit als Fase 2b heeft bevestigd dat er geen uitvoerbare issues meer zijn.

> **Opmerking:** Poort 2 is optioneel als er geen releasewaardig werk in deze cyclus zit
> (bijv. alleen chore/docs). Check: zijn er `feat:` of `fix:` commits? Zo ja → uitvoeren.
> Zo nee → sla versie-bump over en ga direct naar Fase 4.

### Checklist Poort 2

#### P2-A — Gitleaks scan (volledige codebase)

```powershell
gitleaks detect --source . --no-git 2>&1 | Select-Object -Last 20
```

- Geen findings → ✅
- Findings aanwezig → ❌ STOP — meld aan eigenaar, geen versie-bump

Als gitleaks niet geïnstalleerd is (`(Get-Command gitleaks -ErrorAction SilentlyContinue) -ne $null` → false):
```powershell
# Fallback: scan op bekende high-risk patronen
Get-ChildItem -Recurse -Include *.cs,*.json,*.yaml,*.yml,*.md |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern "(password|secret|token|key)\s*=\s*['""][^'""]{8,}" -CaseSensitive:$false |
    Where-Object { $_ -notmatch "(placeholder|template|example|your_|YOUR_|<[A-Z])" } |
    Select-Object -First 20
```

#### P2-B — Club-data scan (volledige main)

```powershell
git diff HEAD~20..HEAD --name-only | ForEach-Object {
    if (Test-Path $_) {
        Select-String -Path $_ -Pattern "(func-[a-z]|swa-[a-z]|\.azurewebsites\.|\.database\.windows\.|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4})" |
            Where-Object { $_ -notmatch "(//|#|placeholder|template|example)" }
    }
} | Select-Object -First 20
```

- Geen output → ✅
- Club-ID's of resource-namen gevonden → ❌ STOP — fix eerst, dan Poort 2 opnieuw

#### P2-C — AVG / persoonsgegevens scan

```powershell
git diff HEAD~20..HEAD -- "*.cs" "*.razor" "*.json" |
    Select-String -Pattern "(@[a-z0-9._%+-]+\.[a-z]{2,}|BSN|geboortedatum|IBAN)" |
    Where-Object { $_ -notmatch "(voorbeeld\.nl|example\.com|placeholder|//|#)" } |
    Select-Object -First 20
```

- Geen output → ✅
- Persoonsgegevens gevonden → ❌ STOP — verwijder en fix, dan Poort 2 opnieuw

#### P2-D — Kwaliteitscontroles

```powershell
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug --no-restore 2>&1 | Select-Object -Last 5
dotnet build BlazorAdmin/BlazorAdmin.csproj --no-restore 2>&1 | Select-Object -Last 5
.\scripts\dev\Test-App.ps1 2>&1 | Select-Object -Last 10
```

- Beide builds exit 0 → ✅
- Build-fouten → ❌ STOP — fix eerst
- Test-App.ps1 exit 1 → ⚠️ noteer (geen hard stop als service-afhankelijke check faalt)

#### P2-E — CHANGELOG completeness

Lees de eerste 80 regels van `CHANGELOG.md`:
- `## [Unreleased]` bevat entries voor alle geïmplementeerde issues uit deze cyclus → ✅
- Lege `[Unreleased]` terwijl er wel feat/fix-commits zijn → ⚠️ vul aan vóór verdere actie

#### P2-F — Versie-bump beslissing

Op basis van commits sinds de laatste tag:
```powershell
git log $(git describe --tags --abbrev=0 2>/dev/null)..HEAD --pretty=format:"%s" 2>/dev/null |
    Select-Object -First 30
```

- `feat:` commits aanwezig → MINOR bump (2.x.y → 2.x+1.0)
- Alleen `fix:`/`security:` → PATCH bump (2.x.y → 2.x.y+1)
- `BREAKING CHANGE:` in commit-body → MAJOR bump (escaleer naar eigenaar eerst)
- Bij twijfel tussen MINOR en PATCH → MINOR

### Poort 2 uitvoeren — release-volgorde (versie-bump ALTIJD NA succesvolle deploy)

> **KRITIEKE VOLGORDE — nooit omdraaien:**
> versie-bump en CHANGELOG afsluiten ALLEEN als de deploy succesvol is afgerond.
> Een versie-label op code die niet deployed is misleidt toekomstige releases.

Alleen als **alle P2-A t/m P2-E checks ✅ of ⚠️ (geen ❌)**:

```powershell
$newVersion = "<nieuw versienummer>"   # bijv. "2.3.0"

# ── Stap 1: Pre-release DB-check — vóór PR aanmaken ──────────────────────
# De database MOET online zijn vóór de merge. Zo niet: abort en meld.
$dbStatus = az sql db show `
  --name "$env:SQL_DATABASE" `
  --resource-group "$env:SQL_RESOURCE_GROUP" `
  --server "$env:SQL_SERVER" `
  --query 'status' --output tsv 2>$null
if ($dbStatus -ne "Online") {
    Write-Host "❌ Database is '$dbStatus' — release afgebroken."
    Write-Host "Zorg dat de database Online is en voer --release opnieuw uit."
    # GEEN versie-bump, GEEN PR, GEEN merge
    exit 1
}

# ── Stap 2: Versie-bump + CHANGELOG op develop (ZONDER te mergen) ─────────
# Pas <Version> aan in FunctionApp/fa-dev-sportlink-01.csproj
# Pas <Version> aan in BlazorAdmin/BlazorAdmin.csproj
# CHANGELOG: verplaats [Unreleased] naar [x.y.z] — YYYY-MM-DD
git add FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj CHANGELOG.md
git commit -m "chore: release v$newVersion"
git push origin develop

# ── Stap 3: Release-PR develop → main ────────────────────────────────────
# pre-release-check.yml draait automatisch op de PR en controleert opnieuw:
#   - db-check (database Online)
#   - build-check (FunctionApp + BlazorAdmin compileren)
# De PR kan NIET gemerged worden als deze checks falen (branch protection).
gh pr create --base main --head develop --title "release: v$newVersion" --body "..."

# ── Stap 4: Wacht op pre-release-check en merge ───────────────────────────
gh pr checks <pr-nr> --watch
# Alle checks groen? → merge
gh pr merge <pr-nr> --merge

# ── Stap 5: Wacht op SUCCESVOLLE deploy — dan pas tag ────────────────────
gh run list --branch main --workflow deploy.yml --limit 1 --json databaseId | ConvertFrom-Json
gh run watch <run-id> --exit-status

# Verplichte per-job check (alle jobs success/skipped?)
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'

# ── Stap 6: Tag ALLEEN na succesvolle deploy ─────────────────────────────
git checkout main && git pull
git tag "v$newVersion" -m "Release v$newVersion"
git push origin "v$newVersion"
```

Na tag-push:
```powershell
gh run list --workflow release.yml --limit 1
gh run watch <run-id> --exit-status
```

Rapporteer: `✅ Poort 2 geslaagd — v$newVersion gedeployed, getagd + GitHub Release aangemaakt`

### Poort 2 — NO-GO (database niet beschikbaar)

Als de DB-check in Stap 1 of de pre-release-check op de PR faalt:
- **Geen versie-bump op develop**
- **Geen PR naar main**
- **Geen tag**
- Meld aan gebruiker: "Database niet beschikbaar — release uitgesteld. Issues staan klaar op develop. Voer `/autonoom --release` opnieuw uit zodra de database Online is."

Als deploy (Stap 5) faalt na succesvolle merge:
- Tag NIET aanmaken (Stap 6 overslagen)
- Meld aan gebruiker: "Deploy gefaald na merge — zie GitHub Actions voor details. Tag wordt aangemaakt zodra deploy slaagt."

Als andere ❌ checks (P2-A/B/C) falen:
- Fix het probleem op develop
- Voer Poort 2 opnieuw uit vanaf Stap 1

---

## FASE 4 — NIEUWE ITERATIE-BRANCH

### 4a. Bepaal versienummer

```powershell
$csproj = Get-Content "FunctionApp/fa-dev-sportlink-01.csproj" -Raw
$version = ([regex]'<Version>([\d.]+)</Version>').Match($csproj).Groups[1].Value
# Haal MINOR op: bijv. "2.3.0" → "v2.3"
$minor = ($version -split '\.')[0..1] -join '.'
$branchName = "feature/v$minor-iteratie"
```

### 4b. Branch aanmaken

```powershell
git checkout main
git checkout -b $branchName
```

Rapporteer: `✅ Nieuwe iteratie-branch: $branchName`

---

## FASE 5 — DEBUG STARTEN

### 5a. Check lopende services

```powershell
$fa  = [bool](Get-NetTCPConnection -LocalPort 7094  -State Listen -ErrorAction SilentlyContinue)
$bl  = [bool](Get-NetTCPConnection -LocalPort 5242  -State Listen -ErrorAction SilentlyContinue)
$az  = [bool](Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue)
```

### 5b. Start wat ontbreekt

Als alle drie al draaien → health-check en klaar:
```powershell
Invoke-RestMethod http://localhost:7094/api/health
```

Als iets mist → start alles opnieuw:
```powershell
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
.\scripts\dev\Start-Debug.ps1
Start-Sleep -Seconds 15
Invoke-RestMethod http://localhost:7094/api/health
Invoke-WebRequest http://localhost:5242/ -UseBasicParsing | Select-Object StatusCode
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
