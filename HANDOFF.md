# HANDOFF — na release v3.10.0.0 (2026-10-03)

**Productie:** v3.10.0.0 live op de **Flex Consumption-app** (FLEX-05/06/09 afgerond, #1068 #1069 #1072).
De oude Linux Consumption-app is **gestopt, niet verwijderd** — bewaren tot minimaal 2027-01-03 (#1076).
Alle niet-HTTP-triggers draaien op de Flex-app; op de oude app staan ze uit. Deploy-secrets wijzen naar de Flex-app.

Release via `/release` (eerste run van de securitypoort, #1470): Security Gate groen, alerts 0/0/0,
security-review 0 HIGH / 0 MEDIUM / 6 LOW (5 opgelost, 1 geaccepteerd: versietekst kan als link renderen).
Lokale Playwright-acceptatie en -regressie groen; live: health ok, 401-controles, Admin GUI zonder CSP-fouten.

## Eigenaarsacties (open)
1. **PDF-export aanzetten voor de eigen club** (#1459, standaard uit): Instellingen → PDF-export. Vraagt een admin-login.
2. **Nieuwe begeleiders-CSV importeren** (#1360, kolom `Functie`).
3. **Live Sportlink-verwijdertest** (#1458): lokaal script met dry-run-vangnet (TESTTHUIS–TESTUIT op een zondag,
   direct weer verwijderd). Kon niet door een agent: een echte transactie in Sportlink vereist de eigenaar.
   Let op: bij vrije tekst voor het thuisteam gebruikt Sportlink intern het standaardteam van de club.
4. **Seizoensreset één keer op productie** (#1352, toegestaan) — vraagt een admin-login.
5. **Kostenalert op het gratis Flex-tegoed** (#1075): eurobudget (€ 5/mnd, 85%/100%) staat; een metric alert op het
   tegoed is betaald en wacht op akkoord.
6. **#1237** (besloten notitie), **#42/#43** (testperiode e-mailverwerking met dagelijkse review).

## Volgende ronde (Claude Code)
- **.NET 10**: PR #1475 (draft, alle projecten net10.0) mergen na observatie van de Flex-app, daarna #1074 (runtime 10.0) en
  CLAUDE.md-sectie ".NET versie" herschrijven. Daarna release v3.11.
- **#1473** RID-specifieke publish (nu onblokkeerd), **#1472** log-injectie (45 CodeQL-meldingen).
