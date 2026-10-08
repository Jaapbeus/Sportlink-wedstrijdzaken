# Dossier: agentinstructies binnen het budget van Codex (#1580)

> Dit is een **werkdossier**: het legt vast hoe `AGENTS.md` (124.770 bytes) en `FunctionApp/AGENTS.md`
> (16.175 bytes) worden teruggebracht tot een kern die Codex volledig leest, welke regel waarheen ging en
> hoe dat is geverifieerd. De geldende regels staan in `AGENTS.md` en de gezaghebbende documenten
> (`docs/ARCHITECTUUR.md` §13.1); dit dossier is bewijs en onderbouwing, geen tweede bron. Het wordt niet
> bijgewerkt bij latere wijzigingen van de instructies.

## 1. Probleem en meting

Codex leest van de projectinstructies standaard maximaal **32.768 bytes** (`project_doc_max_bytes`) over
de hele keten van de repositoryroot tot de werkmap samen, en kapt de rest stilzwijgend af. Gemeten op
`develop` (9859f823): root 124.770 bytes, `FunctionApp/AGENTS.md` 16.175 bytes. De absolute
veiligheidsregels begonnen pas bij byte ~46.000; Codex zag ze dus niet.

**Gemeten semantiek** (Codex 0.158.0, `codex debug prompt-input`, synthetische fixtures, geen model-API):

- Root en submap worden samengevoegd met `\n\n`; het budget telt de **som van de bestandsbytes**, de
  scheidingstekens tellen niet mee (som 32.769 volledig, som 32.770 zonder de laatste bytes). De guard
  telt ze toch mee, als extra marge.
- `AGENTS.override.md` vervangt de `AGENTS.md` in dezelfde map; fallbacknamen gelden alleen zonder
  `AGENTS.md`. Geen van beide verhoogt het budget.
- Een projectconfig met een hoger budget werkt alleen bij een extern als vertrouwd gemarkeerd project;
  daarom is het budget hier structureel opgelost en niet via configuratie.

**Doel** (eigenaarsopdracht): root ≈ 24 KiB, `FunctionApp/AGENTS.md` ≤ 4 KiB, iedere keten ≤ 30 KiB
(inclusief scheidingstekens), zonder verhoogde gebruikersinstelling.

## 2. Beslisregels

1. **Een harde regel blijft inline**, compact. Een regel die alleen achter een link staat wordt niet
   gelezen: een Markdownlink importeert niets. Dat geldt voor bevoegdheden, eigenaarschap, worktree- en
   runtime-isolatie, reviewbeurten, merge-/deployautorisatie, security/AVG, de publicatieregel 4a
   (inclusief vindaanwijzingen), kostenstops, eerlijke verificatie en de architectuur- en live-testgrenzen.
2. **Toelichting, onderbouwing, historie, voorbeelden en uitvoeringsdetail verhuizen** naar het
   gezaghebbende document volgens `docs/ARCHITECTUUR.md` §13.1. Normatieve agentwerkwijze blijft in
   `AGENTS.md`.
3. **Eerst verplaatsen, dan inkorten.** Wat nergens anders stond is eerst in het doel toegevoegd (PR B);
   daarna wordt de bron ingekort (PR C). Er is geen moment waarop een regel nergens staat.
4. **Een leesmoment is een instructie, geen link.** Voor taakgebonden documenten staat in `AGENTS.md` een
   concrete regel "lees X vóór Y" (tabel *Leesmomenten*).

## 3. Werkwijze van de inventaris

Per regeleenheid (genummerd punt, alinea, tabelrij-groep of codeblok) is vastgesteld: de soort (HARD,
UITLEG, VOORBEELD, UITVOERING, NAVERWIJZING), of dezelfde inhoud al elders staat (met `pad:regel`) en het
voorstel (inline-compact, alleen-verwijzing, verplaatsen, vervalt). Dat deed per regelbereik een
onafhankelijke subagent (Sonnet), met een eigen controle van drie "volledig gedekt"-claims. De implementer
heeft daarna elke melding "ontbreekt" of "risico" afgehandeld en elke verplaatsing in het doeldocument
aangebracht; alle bestemmingen in de matrix hieronder zijn in PR B toegevoegd.

## 4. Matrix per sectie

Regelnummers verwijzen naar `AGENTS.md` op `develop` (9859f823), vóór de verkleining. *Inline* = blijft
in `AGENTS.md`, compacter. De laatste kolom noemt wat verhuisde of vervalt, en waar de uitwerking nu staat.

| # | Sectie (regels) | Bytes | Aard | In `AGENTS.md` | Wat verhuisde of vervalt, en waarheen |
|---|---|---:|---|---|---|
| 1 | Rollen (5–19) | 910 | HARD | inline | — |
| 2 | Eigenaarschap en overlap (20–54) | 2.950 | HARD | inline | — |
| 3 | Bevoegdheden en merge/deploy (55–76) | 1.610 | HARD | inline | — |
| 4 | Wederzijdse review en `turn:` (77–119) | 3.170 | HARD | inline | Herformulering; "automatische ontwikkelruns" en "raak geen andere issues/PR's aan" teruggezet na de onafhankelijke controle (§7) |
| 5 | Gedeelde debugomgeving (120–136) | 1.280 | HARD | inline | — |
| 6 | Werkinstructies wijzigen (137–171) | 2.440 | HARD | inline | Formaat memory-notitie → `ARCHITECTUUR-CODEKWALITEIT.md` ("Grenzen van instructiehandhaving") |
| 7 | Kostenbeleid: harde regels (174–201) | 1.380 | HARD | inline | Meldingsformat blijft inline (andere documenten citeren het) |
| 8 | Kostenbeleid: gratis- en betaaldtabellen, checklist (203–236) | 2.670 | UITVOERING | verwijzing + stopregels | Tabellen en checklist → `ARCHITECTUUR.md` §8.6 |
| 9 | Sessie-isolatie en branch-strategie (240–268) | 1.420 | HARD/UITLEG | inline (kort) | Boomdiagram en uitleg vervallen (staan in README, CONTRIBUTING, WZ-ADR-007); branch-op-branch → `CONTRIBUTING.md` |
| 10 | Stap S0 en branchtabel (269–312) | 4.100 | HARD | inline, tabel compact | Voorbeeldcommando vervalt (de tabel is leidend) |
| 11 | Hotfix en backport naar develop (314–347) | 1.400 | HARD + UITLEG | kern en verbod inline | Onderbouwing, script, incident → `VERSIONING.md` ("Hotfix en backport naar develop"); `BEHEERDER-HANDLEIDING.md` rechtgezet |
| 12 | Autonome cyclus: stap 0–1 (351–367) | 950 | HARD | inline | Label `fase: N` bestaat niet (meer): vervalt |
| 13 | Stap 2 verificatielus (369–496) | 7.530 | HARD + UITVOERING | grenzen inline | Handmatige start → `DEVELOPER-SETUP.md`; CSP-regel en uitleg → `ARCHITECTUUR.md` §8.2.3 rij 12; criterium "geslaagd" → `DEVELOPER-SETUP.md` §7 |
| 14 | Stap 2b documentatie nalopen (498–536) | 3.780 | UITVOERING | plicht + verwijzing | Matrix document ↔ trigger → `DOCUMENTATIEPLAN.md` ("Updateregels per document") |
| 15 | Stap 3 commit en PR (538–554) | 810 | HARD + VOORBEELD | inline (compact) | Reden voor `--draft` → `CONTRIBUTING.md` |
| 16 | Herkomstlabel `source:` (556–575) | 1.400 | HARD | inline | Reden label-boven-auteursveld → `ARCHITECTUUR-CODEKWALITEIT.md` §6 |
| 17 | Issue-lifecycle en statuslabels (577–639) | 4.630 | HARD + UITLEG | invarianten inline | Historie (#690, `waiting-codex`) vervalt; `issue-status.test.js` → `VERIFICATIE-SCRIPTS.md` |
| 18 | Lifecycle awaiting-release (641–658) | 1.500 | HARD + UITLEG | inline (kort) | Reden en incident 2026-07-26 → `VERSIONING.md` §6b |
| 19 | Stap 4 en 5, escalatie (660–695) | 2.050 | HARD | inline | — |
| 20 | Absolute veiligheidsregels (699–807) | 8.650 | HARD + VOORBEELD | inline, compact | Incident v2.17.2.0 en CSP-uitleg → `ARCHITECTUUR.md` §7.3; checklist live rendercheck → `VERSIONING.md` §6b; `gh run rerun --failed` → `MONITORING.md`; extra FOUT/GOED-voorbeelden → `SECURITY.md` |
| 21 | Open-source en multi-club (810–831) | 1.350 | HARD | inline | Uitzondering `dbo.AppSettings` en template-tokens → `ARCHITECTUUR.md` §8.1.2 en §8.2.4 |
| 22 | Codekwaliteit, negen regels (843–906) | 4.990 | HARD + UITLEG | regels inline | Derde wrapper → `ARCHITECTUUR-CODEKWALITEIT.md` regel 9; incidentverhaal staat daar al |
| 23 | Teamnaam → TeamId (908–946) | 2.730 | HARD | regels inline | Eisen aan een disambiguator en niet-opruimen → `ARCHITECTUUR-TEAMRESOLUTIE.md` ("Regels bij wijzigingen" 3 en 7) |
| 24 | AI-services (948–974) | 1.270 | HARD | regels inline | Voorbeeld en checklist staan in `ARCHITECTUUR-AI-SERVICES.md` |
| 25 | Multi-tier databasestrategie (976–1053) | 5.670 | HARD + UITLEG | regels inline | Tier degraderen → `ARCHITECTUUR.md` §8.4; voorwaarde derde tier met cijfers → `ARCHITECTUUR-DATABASE-TIERS.md` §1; blinde vlek pariteitsguard → `ARCHITECTUUR-CODEKWALITEIT.md` §6 |
| 26 | Supabase RLS, regels 1–8 (1057–1195) | 11.230 | HARD + UITLEG | regels inline, compact | Achtervang dashboard, INFO-reden, AVG-regel MCP → `MONITORING.md`; data-geen-instructies → `DEVELOPER-SETUP.md` §5.4; de rest staat al in `ARCHITECTUUR-DATABASE-TIERS.md` §65–§68 |
| 27 | UPPER()/LOWER() en expressie-index (1197–1251) | 3.910 | HARD + UITLEG | regel inline | Controletabel queryplan → `ARCHITECTUUR-DATABASE-TIERS.md` §69 |
| 28 | E-mail (1253–1271) | 1.200 | HARD | inline (kort) | — |
| 29 | EgressGuard (1272–1288) | 1.020 | HARD | inline | — |
| 30 | Feedback (1289–1310) | 1.580 | HARD | inline (kort) | Regel tweede redactieset → `FEEDBACK.md` |
| 31 | Thema-logica (1311–1334) | 1.590 | HARD | inline (kort) | Staat in `ARCHITECTUUR-CODEKWALITEIT.md` regel 4 en in `ThemeCore` |
| 32 | Sportlink Web Extension (1335–1358) | 1.700 | HARD | inline (kort) | Normatieve regels → `SPORTLINK-WEB-EXTENSION.md` |
| 33 | .NET-versie (1359–1372) | 1.000 | HARD | inline (kort) | Staat in `ARCHITECTUUR.md` en `RUNBOOK-FLEX-MIGRATIE.md` |
| 34 | Cross-platform scripts (1373–1440) | 5.360 | HARD + UITVOERING | kernregels inline | Shell-portabiliteit → `VERIFICATIE-SCRIPTS.md`; PowerShell-platformregels → `DEVELOPER-SETUP.md`; `DevServices.psm1`-regel → `VERIFICATIE-SCRIPTS.md` |
| 35 | Azure Entra setup (1441–1457) | 1.190 | HARD | inline (kort) | Motivatie → `ENTRA-AUTH-BEHEER.md` |
| 36 | Auth, UTC, secrets (1458–1475) | 1.200 | HARD | inline (kort) | Staat in `ARCHITECTUUR.md` §8.1.1 en §8.2 |
| 37 | Tijdinvoer (1476–1481) | 570 | HARD | inline (kort) | Componentuitleg → `ARCHITECTUUR-CODEKWALITEIT.md` regel 4 |
| 38 | GUI-synchroniteit en AVG-testdata (1482–1494) | 910 | HARD | inline (kort) | Staat in `ARCHITECTUUR.md` §8.1.5 en §8.7 |
| 39 | Microsoft Learn MCP (1495–1501) | 620 | HARD | inline (kort) | — |
| 40 | API-standaarden en controles (1502–1528) | 1.450 | HARD | inline (kort) | Checklist en regeneratie-opdracht → `API.md` |
| 41 | Versiebeheer: semver, commits, changelog (1529–1597) | 3.600 | HARD + UITLEG | kern inline | Staat in `VERSIONING.md`; CHANGELOG-plicht → `VERSIONING.md` §7 |
| 42 | Release-workflow (1598–1641) | 3.240 | HARD | poort inline | Volgorde staat in de skill `/release` |
| 43 | Versienummer in code, Build & Run, Security Setup (1642–1698) | 2.750 | UITVOERING | verwijzing | Versienummer in code: `VERSIONING.md` (vier cijfers); gitleaks-installatie: `SECURITY.md`; Build & Run en setup: `DEVELOPER-SETUP.md` en `CONTRIBUTING.md` |
| 44 | Architecture, v2.0 Architectuur, Auth-architectuur (1699–1753) | 3.000 | UITLEG | vervalt | Achterhaald, of aanwezig in `ARCHITECTUUR.md` |
| 45 | Admin API-endpoints en backlog (1754–1783) | 1.870 | UITVOERING | vervalt | Staat in `API.md` en OpenAPI; de backlog is achterhaald |
| 46 | Solution Structure, Code Conventions (1784–1811) | 1.660 | UITVOERING | vervalt | Projectenoverzicht in `DEVELOPER-SETUP.md` §8 (aantal rechtgezet: 16); de vijf codeconventies (camelCase, exacte SQL-casing, async I/O, excepties bij entry-points, instellingen in de settingstabel) in `ARCHITECTUUR.md` (Naamconventies, Async/await) |
| 47 | Sportlink API (1812–1829) | 1.110 | UITVOERING | vervalt | → `SPORTLINK-DATASERVICE.md` (nieuw) |
| 48 | Exports — Teambegeleiding (1830–1850) | 1.420 | HARD | inline (kort) | De AVG-regel voor exports blijft inline |
| 49 | `FunctionApp/AGENTS.md` (16.175 bytes) | 16.175 | HARD + UITVOERING | ≤ 4 KiB | Veldreferentie, nieuwe-databron-checklist en debug-SQL → `SPORTLINK-DATASERVICE.md`; `Pooling=false`-onderbouwing → `ARCHITECTUUR-DATABASE-TIERS.md` |

De bytes zijn bij benadering; de exacte sectiegrenzen staan in het Codex-rapport bij #1580.

## 5. Resultaat

| | Vóór | Na | Grens |
|---|---:|---:|---:|
| `AGENTS.md` (root) | 124.770 B | 27.522 B | 27 KiB (guard), doel was circa 24 KiB |
| `FunctionApp/AGENTS.md` | 16.175 B | 2.093 B | 4 KiB |
| keten root → `FunctionApp/` | 140.947 B | 29.617 B | 30 KiB inclusief scheidingstekens (Codex: 32 KiB) |

De root komt ruim 2,5 KiB boven het streefgetal van circa 24 KiB uit. De bindende grens is de keten van 30 KiB;
de root is niet verder ingekort omdat daarvoor een harde regel (bevoegdheid, isolatie, reviewbeurt, security/AVG,
publicatieregel 4a, kostenstop of een architectuur-/live-testgrens) achter een link had moeten verdwijnen. Een
volgende verkleining kan de resterende toelichting in de alinea's *Architectuurinvarianten* en *Werkinstructies
wijzigen* verplaatsen; de guard houdt de omvang tot dan op het huidige plafond.

## 6. Verificatie (lokaal, zonder betaalde model-API)

**Codex 0.158.0** — `codex debug prompt-input` zonder byte-override, in een worktree van deze repository:

- cwd = repositoryroot: de instructietekst is 27.524 bytes; eerste en laatste regel van `AGENTS.md` staan erin, ook de
  publicatieregel (de zin over vindaanwijzingen) en de tabel *Leesmomenten*.
- cwd = `FunctionApp/`: de instructietekst is 29.619 bytes (= 27.522 + 2 + 2.093 + 2 bytes wrapper); beide bestanden volledig,
  inclusief de laatste regel van `FunctionApp/AGENTS.md`.
- Voor de wijziging eindigde de zichtbare tekst bij byte 32.768, midden in *Stap 2b*; de veiligheidsregels ontbraken.

**Claude Code 2.1.293** — `claude -p "/context"` met een `InstructionsLoaded`-hook (geen modelaanroep) toont per geladen bestand
pad en `load_reason`:

- cwd = root: `CLAUDE.md` (session_start) en `AGENTS.md` (include) — dezelfde kern als Codex.
- cwd = `FunctionApp/`, standaardinstelling van de gebruiker: `FunctionApp/CLAUDE.md`, `FunctionApp/AGENTS.md` en de root-`CLAUDE.md`,
  maar **niet** de root-`AGENTS.md`: de `@`-import van een ancestor-`CLAUDE.md` buiten de werkmap vraagt per project
  goedkeuring (`hasClaudeMdExternalIncludesApproved` in `~/.claude.json`). Een gebruikersinstelling die de repository niet kan
  afdwingen. Mitigatie: `FunctionApp/AGENTS.md` begint met een leesmoment ("lees `../AGENTS.md` als de rootregels niet in je
  context staan").
- cwd = `FunctionApp/` met die goedkeuring (tijdelijke `CLAUDE_CONFIG_DIR`, projectsleutel van de hoofdrepository én de worktree):
  alle vier de bestanden worden geladen. Een worktree onder `.claude/worktrees/` hoort bij het project van de hoofdrepository.

## 7. Onafhankelijke controle op regelbehoud

Een tweede, niet door de implementer gestuurde controle (Opus, met alleen het oude en het nieuwe bestand en het criterium van de
eigenaar) vond na de eerste verkleining: twee verdwenen bevoegdheidsregels ("een onderzoeksopdracht verleent geen
implementatieopdracht"; "een automatische ontwikkelrun vraagt een aparte eigenaarsopdracht"), één feitelijk onjuiste regel (een
verwijzing naar `IEmailVerzendService`, dat nog niet bestaat) en zes afgezwakte regels (blokkadegeval bij review, release-securitypoort
voor secret-alerts, AVG-delen van Feedback, "een guard moet rood kunnen worden", de plicht tot dashboardcontrole, hostcontrole bij SSRF).
Alle negen zijn hersteld; extra teruggezet zijn de `source:`-aanvulregel, "duplicatie alleen omlaag", het verbod om een Sportlink-request-URL te
loggen (stond alleen in de submap-instructie van één tier) en de executable-bit van hooks. De overige lichte punten staan in een document met
een leesmoment en zijn bewust niet teruggezet. Het compenseren van ~700 bytes gebeurde door formuleringen te verkorten.

## 8. Wat niet is geverifieerd

Niet getest: Codex-desktop en cloud-runs, Windows, de interactieve goedkeuringsdialoog van Claude Code (de instelling is
alleen uit configuratie en gedrag afgeleid), de Claude-desktop-app en andere clients. Een model dat de regels leest volgt ze
niet daarmee; dit bewijst alleen wat er in de context komt.

## 9. Wat niet is herzien, en wat open blijft

- De actualiteit van de kostentabellen (de Function App draait op Flex; of de rij *Azure SQL Database Free
  offer* nog geldt) is **niet herzien** maar ongewijzigd overgenomen, met een opmerking in
  `ARCHITECTUUR.md` §8.6. Dat is een besluit voor de eigenaar bij de eerstvolgende kostencontrole.
- De procedure voor een hotfix-backport in `BEHEERDER-HANDLEIDING.md` (een PR `main` → `develop`) wees af
  van de regel in `AGENTS.md` (aparte backport-branch zonder versie en CHANGELOG). Ze is gelijkgetrokken
  met `AGENTS.md`; dat is een herstel van een tegenstrijdigheid, geen nieuw besluit.
- Verwijzingen in andere documenten, skills, workflows en code-opmerkingen naar `AGENTS.md`-kopjes die bij de
  verkleining verdwenen zijn, zijn aangepast (alleen tekst; geen gedrag).
