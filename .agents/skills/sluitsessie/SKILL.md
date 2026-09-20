---
description: Sluit de sessie gestructureerd af — triage eerst, daarna gates, altijd memory schrijven.
disable-model-invocation: true
---

Voer de sessie-afsluiting uit als een gate-based pipeline. Elke fase is een poort:
als een harde blocker gevonden wordt, stop je bij die fase en rapporteer je wat er
nog moet gebeuren. Schrijf altijd een sessiesamenvatting naar memory — ook bij
gedeeltelijke afsluiting.

Symbolen:
- ✅ In orde
- ⚠️ Aandachtspunt (kan nog gecorrigeerd worden)
- ❌ Harde blocker — sessie NIET veilig af te sluiten zolang dit open staat

> 🖥️ **CROSS-PLATFORM — altijd van toepassing (#800, #1286).**
> Deze skill draait op Windows én macOS. Twee regels bij het aanpassen ervan:
> 1. **`grep -E`, nooit `grep -P`.** De BSD-grep van macOS kent geen PCRE; in een pijplijn faalt
>    dat stil en lijkt het resultaat gewoon leeg.
> 2. **Geen hardgecodeerde paden met een gebruikersnaam of schijfletter.** De memory-map verschilt
>    per machine en per platform — neem hem over uit de sessie-instructies (zie Fase 4).

---

## FASE 0 — TRIAGE (altijd eerst, alleen lezen, geen wijzigingen)

**0a. Branch-check**
Voer uit: `git branch --show-current`
- Op `feature/*` of `hotfix/*` → ✅
- Op `main`, `v2/develop` of detached HEAD → ❌ HARDE BLOCKER — stop hier.

**0b. Uncommitted werk**
Voer uit: `git status --short`
- Geen output → ✅
- Wijzigingen aanwezig → ❌ HARDE BLOCKER — lijst bestanden op en stop hier.

**0c. Ongepushte commits**
Voer uit: `git log --oneline origin/$(git branch --show-current)..HEAD 2>/dev/null || git log --oneline -5`
- Geen output → ✅
- Commits aanwezig die niet op origin staan → ⚠️

**0d. Open PR**
Voer uit: `gh pr list --head $(git branch --show-current) 2>/dev/null`
- PR aanwezig → noteer PR-nummer
- Geen PR → ⚠️

→ Toon triage-samenvatting. Stop bij harde blockers (0a of 0b) — ga pas verder als de gebruiker de blocker oplost of expliciet vraagt door te gaan.

---

## FASE 1 — CODE-INTEGRITEIT (alleen als Fase 0 geen harde blockers heeft)

**1a. FunctionApp build (Postgres-tier eerst — draait in productie, #1060)**
`dotnet build FunctionApp.Postgres/FunctionApp.Postgres.csproj -c Debug --no-restore 2>&1 | tail -8`
- Exit 0 → ✅ | fouten → ❌ HARDE BLOCKER — stop hier.
- Zijn in deze sessie ook SQL Server-tier-bestanden (`FunctionApp/`) gewijzigd? Bouw die er dan
  ook bij: `dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug --no-restore 2>&1 | tail -8`

**1b. BlazorAdmin build — ALLEEN als de BlazorAdmin dev server niet draait**
> ⚠️ Draait `/startdebug` (dotnet watch op :5242) nog vanuit deze of een eerdere sessie?
> `lsof -nP -iTCP:5242 -sTCP:LISTEN` (macOS/Linux) of `Get-NetTCPConnection -LocalPort 5242`
> (Windows) laat dat zien. Een tweede `dotnet build` naast of vlak na die draaiende server
> genereert een tweede set content-hash fingerprints → 404 op framework-JS → "An unhandled
> error has occurred. Reload" (zie AGENTS.md-verificatielus stap b en de `startdebug`-skill).
>
> - Server draait niet → bouw gewoon: `dotnet build BlazorAdmin/BlazorAdmin.csproj --no-restore 2>&1 | tail -8`
> - Server draait wel → sla deze build over (Fase 1a dekt de compileerbaarheid al voor de
>   FunctionApp-lagen) óf herstart bewust via `./scripts/dev/Stop-Debug.ps1 -Clean` gevolgd door
>   `./scripts/dev/Start-Debug.ps1` — nooit een losse `dotnet build BlazorAdmin` ernaast.

- Exit 0 (of bewust overgeslagen omdat de server draait) → ✅ | fouten → ❌ HARDE BLOCKER — stop hier.

---

## FASE 2 — DOCUMENTATIE (alleen als Fase 1 volledig ✅)

**2a. Gewijzigde bestanden**
`git diff origin/$(git branch --show-current)...HEAD --name-only 2>/dev/null || git diff HEAD~5..HEAD --name-only`

**2b. CHANGELOG [Unreleased]**
Lees eerste 60 regels van `CHANGELOG.md` — entry aanwezig en passend? ✅ / leeg? ⚠️

**2c. Docs-matrix**

| Gewijzigd | Controleer |
|---|---|
| FunctionApp/**/*.cs | docs/API.md |
| FunctionApp/Planner/** | docs/ARCHITECTUUR-PLANNER.md |
| BlazorAdmin/**/*.razor | docs/BEHEERDER-HANDLEIDING.md |
| Architectuurregel/conventie | AGENTS.md |
| Setup/configuratie | docs/DEVELOPER-SETUP.md |
| Testscript | docs/VERIFICATIE-SCRIPTS.md |
| Email-pipeline | docs/EMAIL-VERWERKING.md |
| Auth/Entra | docs/ENTRA-AUTH-BEHEER.md |
| Security/AVG | SECURITY.md |

---

## FASE 3 — REPOSITORY-STAAT (alleen als Fase 1 ✅)

**3a.** Commits gepusht? → ✅ / ongepusht → ⚠️ push aanbevolen
**3b.** `gh pr list --head $(git branch --show-current) 2>/dev/null` + `gh pr checks <nr>`
- CI groen → ✅ | CI rood (Security Gate) → ❌ HARDE BLOCKER

**3c. Open issues die in deze sessie zijn afgerond**
Haal issue-nummers op uit recente commit-messages op de huidige branch:
```bash
git log origin/main..HEAD --pretty=format:"%s" 2>/dev/null \
  | grep -oE '#[0-9]+' | sort -u
```
Voor elk gevonden nummer: controleer de GitHub-status:
```bash
gh issue view <nr> --json number,title,state 2>/dev/null
```
- `state: CLOSED` → ✅
- `state: OPEN` → ⚠️ controleer of het issue volledig is afgerond; zo ja: sluit het af met een afsluitend comment:
  ```bash
  gh issue close <nr> --comment "Afgerond in deze sessie — zie commit-geschiedenis voor details."
  ```
- Twijfel of werk nog open? → noteer als ⚠️ met toelichting in het eindrapport, sluit NIET zonder zekerheid.

---

## FASE 4 — MEMORY SCHRIJVEN (altijd)

Schrijf `session_latest.md` naar de **memory-map van deze sessie** — dat is de map die in de
sessie-instructies genoemd staat en waar `MEMORY.md` al in staat. Neem die map over zoals hij
daar vermeld wordt; schrijf hier nooit een pad met de hand uit.

> **Waarom geen vast pad (#1286).** De projectmap onder de agent-configuratiemap is een slug van
> het checkout-pad, dus hij verschilt per machine én per platform. Hier stond een hardgecodeerd
> Windows-pad inclusief gebruikersnaam en schijfletter: op macOS bestaat dat niet, en de
> sessiesamenvatting belandde dan nergens of op een nieuw aangemaakt, verkeerd pad.

```
---
name: session-latest
description: Samenvatting van de meest recente werksessie — branch, wijzigingen, open punten
metadata:
  type: project
---

**Branch:** <branch>
**Datum:** <datum>
**Status:** COMPLEET / GEDEELTELIJK

**Gedaan:**
- <punt>

**Openstaand:**
- <punt of: geen>

**Why:** Sessie-continuïteit.
**How to apply:** Lees bij sessiestart op dezelfde branch.
```

Update ook de `session_latest`-regel in MEMORY.md.

---

## EINDRAPPORT

| Fase | Check | Status |
|---|---|---|
| 0a | Branch geïsoleerd | |
| 0b | Geen uncommitted werk | |
| 0c | Commits gepusht | |
| 0d | PR zichtbaar | |
| 1a | FunctionApp build | |
| 1b | BlazorAdmin build | |
| 2b | CHANGELOG bijgewerkt | |
| 2c | Docs actueel | |
| 3b | PR + CI groen | |
| 3c | Afgeronde issues gesloten | |

- Alle ✅ → `✅ Sessie volledig afgesloten`
- ⚠️ aanwezig → `⚠️ Afgesloten met aandachtspunten`
- ❌ aanwezig → `❌ NIET afgesloten — los blocker op en voer /sluitsessie opnieuw uit`

Sluit af met één aanbevolen volgende actie.
