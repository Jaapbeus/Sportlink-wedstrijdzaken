---
name: release
description: "Release van develop naar productie — eerst een verplichte securitypoort (/security-review op de volledige releasediff + open alerts), daarna versiebump, release-PR, deploycontrole en tag."
disable-model-invocation: true
argument-hint: "[--dry-run]"
---

> **Gezamenlijke agentregels zijn leidend (CLAUDE.md/AGENTS.md).** Deze skill geldt voor Codex
> en Claude Code. Werk uitsluitend aan de toegewezen taak in de eigen geverifieerde worktree;
> claim of wijzig geen taak, branch, worktree of services van een andere actieve sessie.
> Behoud `source:` als herkomst; registreer implementer, reviewer, fase en sessie afzonderlijk.
> Vóór merge: wederzijdse review van de huidige head-SHA, relevante checks én afzonderlijke
> eigenaarsautorisatie. Deze skill omzeilt die grenzen niet.
> Een expliciete eigenaarsaanroep `/release` (of `/autonoom --release`) autoriseert alleen
> de releaseprocedure. Reserveer release-scope en gedeelde runtime vóór R0. Is `/security-review`
> niet beschikbaar voor de uitvoerende agent, dan is R1 niet voltooid: draag de securityreview
> expliciet over aan Claude Code; geen versiebump/merge/deploy totdat het bewijs beschikbaar is.

Voer een release uit van `develop` naar `main` (productie). Dit is **de enige releaseprocedure**
van dit project; `/autonoom --release` roept deze skill aan in plaats van een eigen kopie (#1470).

**Argument:**
- `--dry-run` — doorloop R0 t/m R2 (inclusief de volledige securitypoort) en rapporteer wat er zou
  gebeuren. Geen commit, geen push, geen PR, geen tag.

> **Waarom deze skill bestaat (#1470).** De Security Gate in CI dekt secrets, PII,
> club-identifiers, kwetsbare pakketten (Trivy) en sinds #1470 ook CodeQL. Wat CI niet doet, is
> een inhoudelijke review van wát er sinds de vorige release veranderd is, zoals een autorisatiefout
> in een nieuw endpoint of een onveilige aanname in nieuwe logica. Dat doet `/security-review`,
> lokaal in Claude Code en dus zonder API-kosten. Tot #1470 stond die review in geen enkel
> releasepad; vergeten betekende niet gedaan. Hier is hij **stap één en een harde poort**.

Symbolen: ✅ in orde · ⚠️ melden, eigenaar beslist · ❌ **STOP — geen versiebump, geen PR, geen tag**

---

## Harde stopcondities — samengevat

Eén van deze ⇒ de release stopt vóór er iets gecommit wordt:

1. `/security-review` meldt één of meer bevindingen met severity **HIGH**.
2. Open Dependabot-alert met severity **high** of **critical**.
3. Open code-scanning-alert (CodeQL of Trivy) met security-severity **high** of **critical**.
4. Open secret-scanning-alert.
5. Security Gate op de HEAD van `origin/develop` niet groen.
6. Kostenwijziging gedetecteerd (zie R3) — meldingsformat uit CLAUDE.md, "Kostenbeleid".

Een STOP hef je niet op door de bevinding weg te redeneren. Wél toegestaan: een bevinding die bij
nadere controle aantoonbaar onjuist is, als vals-positief markeren. Leg dan in het rapport aan de
eigenaar uit waarom, met bestand en regel. De eigenaar beslist, niet jij.

---

## R0 — Voorbereiding (worktree + scope)

1. **Stap S0 uit CLAUDE.md** — reserveer release-scope (versiebestanden, CHANGELOG en
   OpenAPI) zodat geen andere sessie daar tegelijk schrijft. Eigen worktree vanaf `origin/develop`:
   ```bash
   git fetch origin develop main --tags
   # Release-issue: zoek een open "release vX.Y.Z.R"-issue, anders aanmaken met
   #   --label "type: chore" --label "priority: medium" --label "source: claude-code"
   git worktree add -b "feature/#<nr>-release-<versie>" ".claude/worktrees/<nr>-<sessie>" origin/develop
   ```
   Codex gebruikt `codex/<nr>-release-<versie>` en `.codex/worktrees/<nr>-<sessie>`;
   gebruik `source: codex` voor een nieuw Codex-issue. Claude Code gebruikt het voorbeeld hierboven.
   Stap de sessie de worktree in (Claude Code: `EnterWorktree({ path: ... })`; Codex: expliciete werkmap). Pas de werkboom **niet** aan
   vóór R1 klaar is: de review moet exact de inhoud van `origin/develop` zien.

2. **Is er iets te releasen?**
   ```bash
   git log --oneline origin/main..origin/develop --no-merges
   git diff --stat origin/main...origin/develop | tail -1
   ```
   Leeg ⇒ meld "niets te releasen" en stop.

3. **Hotfix-divergentie** — staat op `main` een CHANGELOG-versiekop die `develop` mist?
   ```bash
   comm -23 <(git show origin/main:CHANGELOG.md | grep -oE '^## \[[^]]+\]' | sort -u) \
            <(git show origin/develop:CHANGELOG.md | grep -oE '^## \[[^]]+\]' | sort -u)
   ```
   Treffer ⇒ ⚠️ backport-check uit CLAUDE.md ("Een hotfix is pas af als hij ook terug in
   `develop` staat"). Een niet-teruggebrachte codewijziging is ❌.

---

## R1 — SECURITYPOORT (hard, altijd, ook bij `--dry-run`)

### R1-a — CI-status van de te releasen commit

```bash
SHA=$(git rev-parse origin/develop)
gh api "repos/{owner}/{repo}/commits/$SHA/check-runs?per_page=100" \
  --jq '[.check_runs[] | select(.name=="Security Gate — blokkeert merge bij fout") | .conclusion]'
```

- Alle runs `"success"` ⇒ ✅
- Leeg (nog bezig) ⇒ wacht met `gh run watch` op de lopende Security Scan van die SHA
- Iets anders ⇒ ❌. Let op: GitHub's rollup neemt bij twee runs met dezelfde naam de *slechtste*
  uitkomst, dus één `failure` naast een `success` is ook ❌, tot je hebt vastgesteld welke run bij
  de huidige inhoud hoort.

### R1-b — Open security-alerts op GitHub

```bash
gh api "repos/{owner}/{repo}/dependabot/alerts?state=open&per_page=100" \
  --jq '[.[] | select(.security_advisory.severity=="high" or .security_advisory.severity=="critical")] | length'
gh api "repos/{owner}/{repo}/code-scanning/alerts?state=open&per_page=100" \
  --jq '[.[] | select(.rule.security_severity_level=="high" or .rule.security_severity_level=="critical")] | length'
gh api "repos/{owner}/{repo}/secret-scanning/alerts?state=open&per_page=100" --jq 'length'
```

Alle drie `0` ⇒ ✅. Anders ⇒ ❌. Toon aan de eigenaar alleen aantallen en regel-id's, geen
waarden.

### R1-c — `/security-review` op de volledige releasediff

De ingebouwde skill `security-review` reviewt de wijzigingen van de huidige branch ten opzichte
van de standaardbranch (`origin/HEAD`). In een worktree vanuit `origin/develop` is dat precies wat
er naar productie gaat. Controleer dat eerst:

```bash
git rev-parse --abbrev-ref origin/HEAD     # moet 'origin/main' zijn; anders: git remote set-head origin main
git status --porcelain                     # moet leeg zijn
git rev-parse HEAD; git rev-parse origin/develop   # moeten gelijk zijn
git diff --stat origin/main...HEAD | tail -1       # dit is de scope van de review
```

Roep daarna de skill aan via de Skill-tool: `Skill({ skill: "security-review" })`.

**Grote releasediff.** Kan de review de diff niet volledig overzien (de skill meldt afgekapte
uitvoer, of de diff is aantoonbaar groter dan wat beoordeeld is)? Review dan per gemergede PR:
`git log --merges --first-parent --format='%h %s' origin/main..origin/develop`, en per PR de diff
van `<merge>^1..<merge>`, met dezelfde methode en dezelfde severityschaal. Zeg in het rapport
welke route je hebt genomen. Een review die maar een deel heeft gezien, telt niet als geslaagd.

**Beoordeling van de uitkomst:**

| Severity | Actie |
|---|---|
| **HIGH** | ❌ STOP. Rapporteer aan de eigenaar in het gesprek (bestand, regel, klasse, scenario). |
| **MEDIUM** | ⚠️ Leg elke bevinding met `AskUserQuestion` voor: *nu fixen (release stopt)* of *bewust accepteren en vervolgissue*. |
| **LOW / geen** | ✅ Noteer het aantal in het rapport. |

**Publicatieregel (CLAUDE.md, veiligheidsregel 4a).** Een **nog niet verholpen** bevinding komt
niet inhoudelijk in een publiek issue, PR of commitbericht: geen vindplaats, geen
exploiteerscenario, geen omvang. In een issue staan hoogstens de klasse en het codepad, en pas
nadat de fix gemerged is. Het releasedossier in de PR-body (R5) bevat alleen **aantallen per
severity**.

**Na een STOP:** fix op een eigen `feature/#<nr>-...`-branch vanuit `develop`, met de normale
ontwikkelcyclus (PR naar `develop`, CI groen, merge). Start daarna **`/release` opnieuw vanaf R0**.
De review moet de nieuwe stand van `develop` zien; een herhaling van alleen de fix-diff is niet
genoeg.

### R1 — resultaat vastleggen

Noteer voor R5 en het eindrapport:
```
Securitypoort: Security Gate ✅ · alerts 0/0/0 · /security-review: <n> HIGH, <n> MEDIUM (<geaccepteerd/gefixt>), <n> LOW
Reviewscope: origin/main...<sha kort> (<bestanden> bestanden), route: volledig | per PR
```

`--dry-run`: rapporteer hier ook R2 en stop daarna.

---

## R2 — Versie en CHANGELOG bepalen

- Lees `## [Unreleased]` in `CHANGELOG.md`. Leeg terwijl er `feat:`/`fix:`-commits zijn ⇒ ❌, vul
  eerst aan via een gewone PR.
- Nieuw nummer volgens CLAUDE.md "Fase 2 — release": minimaal één `feat:` ⇒ MINOR, PATCH en
  REVISION naar 0; alleen fixes ⇒ PATCH, REVISION naar 0; BREAKING CHANGE ⇒ MAJOR, en dan eerst
  de eigenaar vragen.
- Basis is de **huidige versie op `develop`**, niet die van `main`.

---

## R3 — Kostencheck (CLAUDE.md, "Kostenbeleid")

```bash
git diff --name-only origin/main...HEAD -- infrastructure/ .github/workflows/
```

- Nieuwe Azure-resource of tier-/planwijziging in de diff ⇒ prijscheck via
  `mcp__claude_ai_Microsoft_Learn__microsoft_docs_search`, en expliciete bevestiging van de
  eigenaar.
- Altijd: controleer via MS Learn of de gratis grenzen van Functions Consumption en Static Web Apps
  Free ongewijzigd zijn. Twijfel ⇒ ❌ met het meldingsformat `⚠️ KOSTENWIJZIGING GEDETECTEERD`.

---

## R4 — Versiebump-PR naar `develop`

In de worktree:
1. `<Version>`, `<AssemblyVersion>` en `<FileVersion>` in **alle drie** de csproj's:
   `FunctionApp/fa-dev-sportlink-01.csproj`, `FunctionApp.Postgres/FunctionApp.Postgres.csproj`,
   `BlazorAdmin/BlazorAdmin.csproj`.
2. `info.version` in `docs/api-standaarden/openapi.yaml`, daarna `openapi.json` regenereren met de
   one-liner uit CLAUDE.md ("API-standaarden").
3. `CHANGELOG.md`: `## [Unreleased]` ⇒ `## [x.y.z.r] — YYYY-MM-DD`, met een lege `## [Unreleased]`
   erboven. De kop moet exact gelijk zijn aan de tag zonder `v`, anders vindt `release.yml` geen
   notes.
4. Commit (`chore(#<nr>): release vX.Y.Z.R — versiebump, CHANGELOG-sectie, OpenAPI-versie`),
   push, `gh pr create --draft --base develop`, met de kostencheck in de body.
5. Wederzijdse review op huidige head-SHA ⇒ bevindingen verwerkt ⇒
   `gh pr checks <pr> --watch` groen ⇒ `gh pr ready` ⇒ geautoriseerde merge (`--merge`).

Het starten van `/release` door de eigenaar is de autorisatie voor de merges en de tag in deze
procedure. Twijfel over iets wat niet in deze skill staat ⇒ vraag het.

---

## R5 — Release-PR `develop` → `main`

```bash
gh pr create --base main --head develop --title "release: vX.Y.Z.R" --body-file <body>
```

Body, in de vorm van eerdere release-PR's (#1447): de opgeleverde issues, databasemigraties (en of
ze additief zijn), wel/geen nieuwe Azure-resources, de pre-testresultaten, en **de
R1-resultaatregel** (alleen aantallen).

`gh pr checks <pr> --watch`. Daarop draaien de Security Gate (inclusief CodeQL), `pre-release-check`
en `pre-release-db-check`. Rood ⇒ niet mergen. Een rode Security Gate is een STOP die je meldt,
geen fout die je zelf wegwerkt. Bij groen én afgeronde review van de actuele release-head: `gh pr merge <pr> --merge`.

---

## R6 — Deploy verifiëren (CLAUDE.md, veiligheidsregels 2 en 2a)

1. `gh run list --branch main --workflow deploy.yml --limit 1` ⇒ `gh run watch <id> --exit-status`
   (niet op de achtergrond).
2. Per job: `gh run view <id> --json jobs --jq '.jobs[] | {name, conclusion}'`. Alles `success`,
   of `skipped` uitsluitend voor de migratiejob van de níet-actieve databasetier.
3. Live browser-rendercheck op de productie-Admin GUI met Playwright: geen CSP-fouten, geen
   `/_framework/`-404, `window.Blazor` aanwezig, redirect naar de Microsoft-login. Faalt iets ⇒
   geen tag, hotfix vanuit `main`, melden.

---

## R7 — Tag en afronding

```bash
git fetch origin main --tags
git tag vX.Y.Z.R origin/main -m "Release vX.Y.Z.R"
git push origin vX.Y.Z.R
gh run list --workflow release.yml --limit 1              # ⇒ watch tot groen
gh run list --workflow close-released-issues.yml --limit 1 # ⇒ watch tot groen
```

---

## Eindrapport

```
## Release vX.Y.Z.R — resultaat
| Securitypoort       | ✅ / ❌ (<R1-resultaatregel>) |
| Kostencheck         | ✅ / ❌ |
| Versiebump-PR       | #... |
| Release-PR          | #... |
| Deploy (per job)    | ✅ / ❌ |
| Live rendercheck    | ✅ / ❌ |
| Tag + GitHub Release| ✅ / ❌ |
| Issues gesloten     | <aantal> via close-released-issues |
```

Bij een STOP: welke stopconditie, wat de eigenaar moet beslissen, en de zin
"Er is niets gecommit of gepusht" (of precies wat er al wel gebeurd is).
