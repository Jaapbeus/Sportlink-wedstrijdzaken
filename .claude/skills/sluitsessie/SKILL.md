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
- Op `feature/*` of `hotfix/*` → ✅, ongeacht de rest van deze fase.
- Op `main`, `develop` of detached HEAD → nog geen oordeel — wacht op 0b/0c en pas dan het
  **branch-oordeel** hieronder toe. Op zichzelf is dit geen harde blocker: pas 0b/0c samen bepalen
  of er van déze sessie iets op het spel staat.

**0b. Uncommitted werk**
Voer uit: `git status --short`
- Geen output → ✅
- Wijzigingen aanwezig → ❌ HARDE BLOCKER, altijd, ongeacht de branch — lijst bestanden op en stop hier.

**0c. Ongepushte commits**
Voer uit: `git log --oneline origin/$(git branch --show-current)..HEAD 2>/dev/null || git log --oneline -5`
- Geen output → ✅
- Commits aanwezig die niet op origin staan → ⚠️ (of ❌ in combinatie met 0a — zie branch-oordeel)

**Branch-oordeel (combineert 0a + 0b + 0c — bepaalt of 0a een harde blocker is):**
- `feature/*`/`hotfix/*` → altijd ✅.
- `main`/`develop`/detached HEAD, mét 0b schoon (geen output) én 0c leeg (geen ongepushte
  commits) → **geen harde blocker, wel ⚠️**. Dit was een puur read-only/onderzoeksessie of de
  checkout staat zo door een andere, gelijktijdige sessie ([[feedback_worktree_isolation_required]]
  — meerdere sessies delen deze working directory zonder git-worktree). Er staat niets van déze
  sessie op het spel: geen wijzigingen, niets te verliezen. Rapporteer dit expliciet als ⚠️ en ga
  NIET zelf een branch aanmaken of wisselen — dat kan een andere sessie die deze checkout verwacht
  aan te treffen verstoren.
- `main`/`develop`/detached HEAD MET 0b-wijzigingen of MET 0c-commits van déze sessie → ❌ HARDE
  BLOCKER — stop hier. Dit is het scenario waar de regel voor bedoeld is: eigen werk dat op een
  beschermde branch staat en verloren kan gaan of per ongeluk gedeeld wordt.

**0d. Open PR**
Voer uit: `gh pr list --head $(git branch --show-current) 2>/dev/null`
- PR aanwezig → noteer PR-nummer
- Geen PR → ⚠️

→ Toon triage-samenvatting. Stop bij een harde blocker uit het branch-oordeel of bij 0b op
zichzelf — ga pas verder als de gebruiker de blocker oplost of expliciet vraagt door te gaan.

---

## FASE 1 — CODE-INTEGRITEIT (alleen als Fase 0 geen harde blockers heeft)

**1a. FunctionApp build**
`dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug --no-restore 2>&1 | tail -8`
- Exit 0 → ✅ | fouten → ❌ HARDE BLOCKER — stop hier.

**1b. BlazorAdmin build**
`dotnet build BlazorAdmin/BlazorAdmin.csproj --no-restore 2>&1 | tail -8`
- Exit 0 → ✅ | fouten → ❌ HARDE BLOCKER — stop hier.

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
| FunctionApp/Planner/** | docs/ARCHITECTURE-PLANNER.md |
| BlazorAdmin/**/*.razor | docs/v2-admin-handleiding.md |
| Architectuurregel/conventie | CLAUDE.md |
| Setup/configuratie | docs/SETUP.md |
| Testscript | docs/TESTING.md |
| Email-pipeline | docs/EMAIL-VERWERKING.md |
| Auth/Entra | docs/AZURE-ENTRA-SETUP.md |
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

> **`grep -E`, nooit `grep -P` (#800, #1286).** De BSD-grep van macOS kent geen PCRE en weigert
> `-P`. In deze pijplijn faalt dat *stil*: `grep` schrijft zijn foutmelding naar stderr, levert
> geen regels, en `sort` sluit daarna af met 0. De skill zou dan "geen afgeronde issues" melden
> in plaats van een fout. Let op dat de fout onzichtbaar blijft op een macOS met `ugrep` of
> GNU-grep uit Homebrew op `PATH` — die accepteren `-P` wél.
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

> **Waarom geen vast pad (#1286).** De projectmap onder `~/.claude/projects/` is een slug van het
> checkout-pad, dus hij verschilt per machine én per platform — op macOS bijvoorbeeld
> `~/.claude/projects/-Users-<gebruiker>-Repo-Sportlink-wedstrijdzaken/memory/`, op Windows
> `%USERPROFILE%\.claude\projects\c--repo-<map>-Sportlink-wedstrijdzaken\memory\`. Hier stond een
> hardgecodeerd Windows-pad inclusief gebruikersnaam: op macOS bestaat dat niet, en de sessie-
> samenvatting belandde dan nergens of op een nieuw aangemaakt, verkeerd pad.

Twijfel je welke map het is, leid hem dan af in plaats van hem te gokken:

```bash
# PowerShell 7 (Windows + macOS)
Join-Path $HOME '.claude/projects'
# → zoek de submap die bij deze checkout hoort; daarin staat memory/MEMORY.md
```

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

**Alleen als alle checks ✅ zijn** (volledig groen, geen enkele ⚠️ of ❌): plaats na de
aanbevolen volgende actie, als allerlaatste in de response, dit banner in een code block:

```
  ██████╗ ██╗  ██╗ █████╗ ██╗   ██╗
 ██╔═══██╗██║ ██╔╝██╔══██╗╚██╗ ██╔╝
 ██║   ██║█████╔╝ ███████║ ╚████╔╝
 ██║   ██║██╔═██╗ ██╔══██║  ╚██╔╝
 ╚██████╔╝██║  ██╗██║  ██║   ██║
  ╚═════╝ ╚═╝  ╚═╝╚═╝  ╚═╝   ╚═╝
```

Bij ⚠️ of ❌ dit banner NIET tonen — dat zou een onafgeronde sessie als voltooid voorspiegelen.
