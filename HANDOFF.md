# HANDOFF — na release v3.11.0.0 (2026-10-04)

**Update 2026-10-04 avond (op develop, nog niet gereleased):** Dependabot NuGet/Actions getest en gemerged
(#1508/#1509; lokaal Azurite vereist nu `--skipApiVersionCheck`, staat in Start-Debug), log-injectie opgelost
(#1472), documentatie geactualiseerd en verouderde scripts verwijderd (#1525), SQL-configuratie als Secrets in de
workflows (#1528), afsluitende codereview (#1529, o.a. nette foutmelding bij 503 en een opgeruimd event-abonnement).
**#1237:** logbewaartermijn op 30 dagen gezet; restrisico oude tags vastgelegd in SECURITY.md (geen history-rewrite);
SQL-waarden als Secret gezet. **Open voor de eigenaar:** de GitHub Variables verwijderen (geblokkeerd voor Claude) —
eerst de zes, ná de volgende release ook de drie SQL-variabelen; teksten/revisies van oude issues; feedbackwidget op
productie doorlopen (lokaal niet volledig te testen zonder AI-dienst). Vervolg-issue #1533.

---

**Update 2026-10-04:** v3.11.0.0 live op Flex met **dotnet-isolated 10.0** (#1073, #1074, eigenaarsbesluit om niet 24 uur te wachten).
Na de overstap gevonden en als hotfix opgelost: **3.10.0.1** (#1512, host-opslag via managed identity op Flex) en
**3.10.0.2** (#1515, instellingen per instantie laden — Flex schaalt elke niet-HTTP-trigger apart). Contract-check
geeft geen vals alarm meer (#1518). Live: health ok, 401-controles, GUI zonder CSP-fouten, keep-alive en contract-check
geslaagd. Live Sportlink-verwijdertest geslaagd (#1458). **Les:** Application Insights is sterk gesampled — controleer
timers via traces of databaseregels, niet alleen via requests.

---

## Vorige stand — na release v3.10.0.0 (2026-10-03)

**Productie:** v3.10.0.0 live op de **Flex Consumption-app** (FLEX-05/06/09 afgerond, #1068 #1069 #1072).
De oude Linux Consumption-app is **gestopt, niet verwijderd** — bewaren tot minimaal 2027-01-03 (#1076).
Alle niet-HTTP-triggers draaien op de Flex-app; op de oude app staan ze uit. Deploy-secrets wijzen naar de Flex-app.

Release via `/release` (eerste run van de securitypoort, #1470): Security Gate groen, alerts 0/0/0,
security-review 0 HIGH / 0 MEDIUM / 6 LOW (5 opgelost, 1 geaccepteerd: versietekst kan als link renderen).
Lokale Playwright-acceptatie en -regressie groen; live: health ok, 401-controles, Admin GUI zonder CSP-fouten.

## Eigenaarsacties (open)
1. **PDF-export aanzetten voor de eigen club** (#1459, standaard uit): Instellingen → PDF-export. Vraagt een admin-login.
2. **Nieuwe begeleiders-CSV importeren** (#1360, kolom `Functie`).
3. ~~Live Sportlink-verwijdertest~~ — geslaagd op 2026-10-03 (#1458).
4. **Seizoensreset één keer op productie** (#1352, toegestaan) — vraagt een admin-login.
5. **Kostenalert op het gratis Flex-tegoed** (#1075): eurobudget (€ 5/mnd, 85%/100%) staat; een metric alert op het
   tegoed is betaald en wacht op akkoord.
6. **#1237** (besloten notitie), **#42/#43** (testperiode e-mailverwerking met dagelijkse review).

## Volgende ronde (Claude Code)
- ~~.NET 10~~ — live in v3.11.0.0.
- **#1473** RID-specifieke publish (nu onblokkeerd), **#1472** log-injectie (45 CodeQL-meldingen).
