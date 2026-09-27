## Wat doet deze PR?

<!-- Korte beschrijving van de wijziging. Sluit het bijbehorende issue: -->
Closes #

## Type wijziging

- [ ] `feat` — nieuwe functionaliteit
- [ ] `fix` — bugfix
- [ ] `security` — beveiligingsfix
- [ ] `docs` — alleen documentatie
- [ ] `refactor` — herstructurering zonder gedragswijziging
- [ ] `chore` — builds, dependencies, CI

## Checklist

### Code
- [ ] Relevante build(s) en tests uit `AGENTS.md` zijn uitgevoerd; noteer hieronder exact welke
      checks wel/niet van toepassing waren
- [ ] Voor backendwijzigingen is de productie-tier `FunctionApp.Postgres` meegenomen; bij
      tier-onafhankelijke wijzigingen zijn beide built tiers beoordeeld (zie
      `scripts/ci/database-tiers.json`)
- [ ] Bij lokale runtimechecks is `./scripts/dev/Test-App.ps1` gebruikt; sla dit alleen over als
      de wijziging geen runtimegedrag raakt en leg dat hieronder uit
- [ ] Geen club-specifieke strings in code (geen hardcoded clubnamen, -IDs of -URLs)
- [ ] UTC in database, `ToLocalTime()` in Blazor voor datumweergave
- [ ] GUI en code synchroon — nieuwe enum/key/type ook in de UI bijgewerkt

### Security & AVG
- [ ] Geen secrets, wachtwoorden of tokens in code of commits
- [ ] Geen persoonsgegevens in code, logs, comments of dit PR
- [ ] Git hooks geactiveerd en groen (`git config core.hooksPath .githooks`)
- [ ] Security Gate in CI is groen

### Documentatie
- [ ] CHANGELOG.md bijgewerkt onder `## [Unreleased]`
- [ ] Relevante docs bijgewerkt waar van toepassing: `docs/DEVELOPER-SETUP.md`, `docs/API.md` +
      OpenAPI, `docs/BEHEERDER-HANDLEIDING.md`, `CLAUDE.md`/gegenereerde `AGENTS.md`, of andere
      doelgroepdocs

## Testbeschrijving

<!-- Hoe is dit getest? Welke scenario's? -->

## Screenshots (indien van toepassing)
