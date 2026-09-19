# Bijdragen aan Sportlink Wedstrijdzaken

Welkom! Dit project is open-source en andere voetbalverenigingen mogen de code gebruiken, verbeteren en aanpassingen voorstellen.

## Inhoudsopgave

- [Voor andere clubs — jouw eigen instantie opzetten](#voor-andere-clubs)
- [Bijdragen als developer](#bijdragen-als-developer)
- [Branch-strategie](#branch-strategie)
- [Commit-conventies](#commit-conventies)
- [Pull Request-proces](#pull-request-proces)
- [Security](#security)
- [Lokale ontwikkelomgeving](#lokale-ontwikkelomgeving)

---

## Voor andere clubs

Wil je deze software gebruiken voor jouw vereniging? Zie [SETUP-NIEUWE-CLUB.md](SETUP-NIEUWE-CLUB.md) voor de volledige installatie-instructies. Je hebt een eigen Azure-omgeving nodig (gratis tier is voldoende) en een Microsoft Entra ID tenant.

**Nooit** fork-specifieke configuratie (tenant-IDs, connection strings, API-keys) terugsturen als Pull Request naar dit project — die horen in jouw eigen GitHub Secrets/Variables.

---

## Bijdragen als developer

### Hoe werkt het?

1. **Fork** deze repository naar jouw eigen GitHub-account
2. Maak een **feature-branch** aan (zie [Branch-strategie](#branch-strategie))
3. Implementeer je wijziging — inclusief tests en documentatie
4. Maak een **Pull Request** naar `develop` van dit project (de integratiebranch — zie [Branch-strategie](#branch-strategie); alleen een release of urgente productiefix gaat naar `main`)
5. De eigenaar beoordeelt de PR, eventueel samen met Claude Code
6. Na goedkeuring wordt de PR gemerged

### Wat zijn welkome bijdragen?

- Bugfixes (met reproductiestappen in de issue)
- Verbeteringen die multi-club werking verbeteren
- Documentatie-verbeteringen
- Performance-verbeteringen zonder architectuurwijziging

### Wat wordt niet geaccepteerd?

- Club-specifieke configuratie, namen of waarden in broncode
- Wijzigingen die de security-architectuur (5 auth-lagen) verzwakken
- Features die AVG/GDPR-compliance schenden
- Code zonder documentatie of met incomplete implementatie

---

## Branch-strategie

```
main     ←── develop            (via PR: release naar productie)
  └── hotfix/#<issue>-<slug>    (via PR: urgente productiefix)

develop  ←── feature/#<issue>-<slug>  (via PR: nieuwe features en bugfixes)
```

| Type | Basis | PR naar | Wanneer |
|---|---|---|---|
| `feature/#<nr>-<slug>` | `develop` | `develop` | Nieuwe features, bugfixes, docs |
| `hotfix/#<nr>-<slug>` | `main` | `main` | Urgente productiefixes |
| `develop` | — | `main` | Release naar productie (na lokaal testen) |

### Regels

- **Altijd** een GitHub Issue aanmaken vóór je begint — de branch-naam bevat het issue-nummer
- **Nooit** direct committen naar `main` of `develop`
- Feature-branches starten vanuit `develop` (niet vanuit `main`)
- Branch-naam altijd beginnen met `feature/` of `hotfix/`
- Na merge wordt de feature-branch verwijderd
- External contributors: fork → branch in je fork → PR naar `develop` van de upstream

### Voorbeeld

```bash
# 1. Fork + clone
git clone https://github.com/JOUW-NAAM/Sportlink-wedstrijdzaken.git
cd Sportlink-wedstrijdzaken

# 2. Branch aanmaken (na issue #42 aanmaken op GitHub)
git checkout -b feature/#42-wedstrijd-exportfunctie

# 3. Werk... commit... push
git push -u origin feature/#42-wedstrijd-exportfunctie

# 4. Pull Request openen via GitHub UI naar de develop-branch van de upstream repository
```

---

## Commit-conventies

Dit project gebruikt [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(#<issue>): <beschrijving>
```

| Type | Wanneer |
|---|---|
| `feat` | Nieuwe functionaliteit |
| `fix` | Bugfix |
| `security` | Beveiligingsfix |
| `docs` | Alleen documentatie |
| `refactor` | Code-herstructurering zonder gedragswijziging |
| `chore` | Builds, dependencies, CI-configuratie |

**Voorbeelden:**
```
feat(#42): wedstrijd-export als CSV toevoegen
fix(#67): null-reference in PlannerDataAccess.GetMatchesAsync
docs(#81): SETUP.md bijwerken voor Azure Free Tier
```

**Versie-impact.** Het versienummer heeft vier cijfers (`MAJOR.MINOR.PATCH.REVISION`) en wordt in
twee fasen opgehoogd. **[docs/VERSIONING.md](docs/VERSIONING.md) is de enige bron** — onderstaande
samenvatting is er een verkorte weergave van; wijkt iets af, dan geldt VERSIONING.md.

*Tijdens development, op je feature-branch:*

| Commit | Bump |
|---|---|
| `feat:` — nieuwe functionaliteit | PATCH (`3.5.3.0 → 3.5.4.0`) |
| `fix:` / `security:` / kleine fix met zichtbaar effect | REVISION (`3.5.3.0 → 3.5.3.1`) |
| `chore:` / `docs:` / `refactor:` zonder zichtbaar effect | geen bump |

*Bij de release (`develop` → `main`), eenmalig door de eigenaar:* MINOR-bump als `[Unreleased]`
minstens één `feat:` bevat, anders een PATCH-bump; `BREAKING CHANGE:` geeft een MAJOR-bump.
**MINOR gaat dus nooit omhoog op een feature-branch.**

Zet het nummer synchroon in **alle drie** de csproj's — `FunctionApp/fa-dev-sportlink-01.csproj`,
`FunctionApp.Postgres/FunctionApp.Postgres.csproj` en `BlazorAdmin/BlazorAdmin.csproj` — en in alle
drie de velden (`Version`, `AssemblyVersion`, `FileVersion`). De Postgres-csproj wordt het vaakst
vergeten wanneer een wijziging alleen die tier raakt.

Voeg bij elke feature of fix ook een regel toe onder `## [Unreleased]` in
[CHANGELOG.md](CHANGELOG.md), geschreven voor de gebruiker.

---

## Pull Request-proces

1. Zorg dat alle CI-checks groen zijn — de Security Gate is **verplicht**
2. Vul het PR-template volledig in
3. Link het bijbehorende GitHub Issue (`Closes #42`)
4. Beschrijf wat er getest is en hoe
5. Nooit persoonsgegevens in PR-beschrijving, issue-comments of code

De eigenaar beoordeelt PRs binnen redelijke tijd. Feedback wordt in de PR gegeven. Een PR kan worden afgewezen als het niet aansluit bij de projectdoelen of architectuurregels.

### Wat er na de merge met je issue gebeurt

Automatisering houdt precies één `status:`-label per issue bij. Zet er zelf nooit met de hand een
bij — `Closes #<nr>` in de PR-titel of -body is genoeg.

| Moment | Status wordt |
|---|---|
| Issue aangemaakt of heropend | `status: triage` |
| Draft-PR geopend | `status: in-progress` |
| PR *ready for review* | `status: review-needed` |
| PR gesloten zonder merge | `status: triage` |
| **PR gemerged naar `develop`** | **`status: awaiting-release` — het issue blijft open** |
| Release-tag gepusht naar `main` | status-labels weg, issue gesloten |

**Een merge naar `develop` sluit je issue dus niet, en dat is opzet.** `develop` is de
integratiebranch; er staat op dat moment nog niets van je wijziging live. Het issue blijft open met
`status: awaiting-release` tot de eerstvolgende release naar `main`, waarna
`close-released-issues.yml` het automatisch sluit. Je bijdrage is niet vergeten — ze wacht op een
releasemoment.

### Security Gate

De Security Gate job in CI is **leidend**. Zolang deze rood is, wordt een PR niet gemerged — ook niet als andere checks groen zijn. De gate controleert:

- Wachtwoorden en tokens (gitleaks)
- Persoonsgegevens (PII-scan)
- Dependency vulnerabilities (Trivy)

De gate draait op elke push én op elke pull request naar `main` of `develop` — ook op een PR vanuit
een fork. Een fork-PR krijgt van GitHub een read-only token zonder secrets; de scan is daar bewust
op ingericht en draait dus volledig (zie SECURITY.md, "Laag 2 — GitHub Actions").

---

## Security

- **Nooit** secrets, API-keys, passwords of persoonsgegevens in code of commits
- Zie [SECURITY.md](SECURITY.md) voor het volledige security-protocol
- Git hooks activeren vóór eerste commit: `git config core.hooksPath .githooks`
- Security-issues melden via een **private** GitHub Security Advisory (niet als publiek issue)

---

## Lokale ontwikkelomgeving

Zie [docs/DEVELOPER-SETUP.md](docs/DEVELOPER-SETUP.md) voor de volledige lokale setup. Samenvatting:

**Vereisten:**
- .NET 10.0 SDK (BlazorAdmin — net10.0)
- .NET 9 Runtime (FunctionApp — net9.0, vereist door het Linux Consumption Plan). Installeer **beide**
  frameworks: `Microsoft.NETCore.App` én `Microsoft.AspNetCore.App` — zonder de tweede breken de
  FunctionApp-testprojecten af.
- Azure Functions Core Tools v4
- Docker, voor de lokale database
- Azurite (Azure Storage Emulator)

Er zijn twee databasetiers. **Postgres is de standaard en de tier die in productie draait**; SQL
Server is een gelijkwaardige, volledig ondersteunde tweede tier die een fork bewust kan kiezen.
Werk je niet specifiek aan de SQL Server-tier, blijf dan op de standaard.

**Starten (Postgres — de standaard):**
```powershell
# Database starten. 'docker compose up -d' start Postgres; SQL Server staat achter een profile:
#   docker compose --profile sqlserver up -d sqlserver
docker compose up -d

# Configuratie aanmaken en POSTGRES_CONNECTION_STRING invullen
cp FunctionApp.Postgres/local.settings.template.json FunctionApp.Postgres/local.settings.json

# Alles starten (Azurite + FunctionApp :7094 + BlazorAdmin :5242)
.\scripts\dev\Start-Debug.ps1            # -Tier SqlServer voor de tweede tier

# Verifiëren (Start-Debug wacht zelf tot de services klaar zijn)
.\scripts\dev\Test-App.ps1
# → verwacht: alle checks groen (exit 0)
```

Werk je aan de SQL Server-tier, dan is het bijbehorende sjabloon
`FunctionApp/local.settings.template.json` met de sleutel `SqlConnectionString`. Gebruik daar altijd
een SQL-login tegen de Docker-container, nooit `Integrated Security`.

**Poorten:**
| Service | Poort |
|---|---|
| Azure Functions | http://localhost:7094 |
| Blazor Admin GUI | http://localhost:5242 |
| Azurite (Blob) | 10000 |

**Tip:** Voor lokaal testen heb je **geen** Azure-abonnement nodig. De FunctionApp werkt volledig lokaal met Postgres (Docker) + Azurite. De Blazor Admin GUI werkt lokaal zonder authenticatie (AlwaysAuthenticated mock).
