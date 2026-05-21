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

Wil je deze software gebruiken voor jouw vereniging? Zie [SETUP.md](SETUP.md) voor de volledige installatie-instructies. Je hebt een eigen Azure-omgeving nodig (gratis tier is voldoende) en een Microsoft Entra ID tenant.

**Nooit** fork-specifieke configuratie (tenant-IDs, connection strings, API-keys) terugsturen als Pull Request naar dit project — die horen in jouw eigen GitHub Secrets/Variables.

---

## Bijdragen als developer

### Hoe werkt het?

1. **Fork** deze repository naar jouw eigen GitHub-account
2. Maak een **feature-branch** aan (zie [Branch-strategie](#branch-strategie))
3. Implementeer je wijziging — inclusief tests en documentatie
4. Maak een **Pull Request** naar `main` van dit project
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
main  ←  feature/#<issue>-<slug>
          hotfix/#<issue>-<slug>
```

| Type | Basis | PR naar | Wanneer |
|---|---|---|---|
| `feature/#<nr>-<slug>` | `main` | `main` | Nieuwe features en bugfixes |
| `hotfix/#<nr>-<slug>` | `main` | `main` | Urgente productiefixes |

### Regels

- **Altijd** een GitHub Issue aanmaken vóór je begint — de branch-naam bevat het issue-nummer
- **Nooit** direct committen naar `main`
- Branch-naam altijd beginnen met `feature/` of `hotfix/`
- Na merge wordt de feature-branch verwijderd

### Voorbeeld

```bash
# 1. Fork + clone
git clone https://github.com/JOUW-NAAM/Sportlink-wedstrijdzaken.git
cd Sportlink-wedstrijdzaken

# 2. Branch aanmaken (na issue #42 aanmaken op GitHub)
git checkout -b feature/#42-wedstrijd-exportfunctie

# 3. Werk... commit... push
git push -u origin feature/#42-wedstrijd-exportfunctie

# 4. Pull Request openen via GitHub UI naar Jaapbeus/Sportlink-wedstrijdzaken main
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

**Versie-impact:**
- `feat:` → MINOR bump (2.x.0)
- `fix:` / `security:` → PATCH bump (2.0.x)
- `BREAKING CHANGE:` in body → MAJOR bump (x.0.0)
- `chore:` / `docs:` / `refactor:` → geen versie-bump

---

## Pull Request-proces

1. Zorg dat alle CI-checks groen zijn — de Security Gate is **verplicht**
2. Vul het PR-template volledig in
3. Link het bijbehorende GitHub Issue (`Closes #42`)
4. Beschrijf wat er getest is en hoe
5. Nooit persoonsgegevens in PR-beschrijving, issue-comments of code

De eigenaar beoordeelt PRs binnen redelijke tijd. Feedback wordt in de PR gegeven. Een PR kan worden afgewezen als het niet aansluit bij de projectdoelen of architectuurregels.

### Security Gate

De Security Gate job in CI is **leidend**. Zolang deze rood is, wordt een PR niet gemerged — ook niet als andere checks groen zijn. De gate controleert:

- Wachtwoorden en tokens (gitleaks)
- Persoonsgegevens (PII-scan)
- Dependency vulnerabilities (Trivy)

---

## Security

- **Nooit** secrets, API-keys, passwords of persoonsgegevens in code of commits
- Zie [SECURITY.md](SECURITY.md) voor het volledige security-protocol
- Git hooks activeren vóór eerste commit: `git config core.hooksPath .githooks`
- Security-issues melden via een **private** GitHub Security Advisory (niet als publiek issue)

---

## Lokale ontwikkelomgeving

Zie [SETUP.md](SETUP.md) voor de volledige lokale setup. Samenvatting:

**Vereisten:**
- .NET 10.0 SDK
- Azure Functions Core Tools v4
- SQL Server (lokaal of Docker)
- Azurite (Azure Storage Emulator)

**Starten:**
```powershell
# Configuratie aanmaken
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Vul SqlConnectionString en andere waarden in

# Alles starten
.\Start-Debug.ps1

# Verifiëren
.\Test-App.ps1
# → verwacht: alle checks groen (exit 0)
```

**Poorten:**
| Service | Poort |
|---|---|
| Azure Functions | http://localhost:7094 |
| Blazor Admin GUI | http://localhost:5242 |
| Azurite (Blob) | 10000 |

**Tip:** Voor lokaal testen heb je **geen** Azure-abonnement nodig. De FunctionApp werkt volledig lokaal met SQL Server + Azurite. De Blazor Admin GUI werkt lokaal zonder authenticatie (AlwaysAuthenticated mock).
