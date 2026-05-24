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
- [ ] `dotnet build` slaagt zonder warnings (`FunctionApp` én `BlazorAdmin`)
- [ ] `.\Test-App.ps1` slaagt (exit 0) — inclusief live services
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
- [ ] Relevante docs bijgewerkt (SETUP.md, CLAUDE.md, inline comments)

## Testbeschrijving

<!-- Hoe is dit getest? Welke scenario's? -->

## Screenshots (indien van toepassing)
