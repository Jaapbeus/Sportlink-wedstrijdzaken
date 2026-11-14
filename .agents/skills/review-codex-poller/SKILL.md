---
name: review-codex-poller
description: "Poll assigned Sportlink review requests and complete independent, read-only PR reviews with SHA-bound evidence and a safe handoff."
disable-model-invocation: true
argument-hint: "[--once|--loop]"
---

Voer de toegewezen Codex-reviewpoller uit. Deze skill geeft alleen reviewbevoegdheid; een
poller-run is geen opdracht om code te implementeren, een issue te claimen, een merge uit te
voeren of iets te deployen.

Gebruik `--once` of een gewone aanroep voor één scan. Gebruik `--loop` alleen wanneer de eigenaar
een terugkerende monitor heeft gevraagd; gebruik vijf minuten tenzij de eigenaar een ander
interval noemt en volg altijd de opgegeven eindtijd. Een actieve repository-automation beheert
haar eigen planning: maak vanuit deze skill geen tweede automation aan en wijzig de bestaande
automation niet.

## 1. Lees actuele regels en inventariseer

Vóór iedere pollrun:

1. Lees `AGENTS.md`, `CLAUDE.md` en `SECURITY.md` uit de checkout en refresh vóór iedere run
   `origin/develop`; controleer de drie bestanden ook op die ref (fetch wijzigt alleen refs, niet
   de checkout; wissel geen gedeelde branch).
   Lees ook de documenten die de relevante `Leesmomenten` aanwijzen. Bij verschil geldt het
   actuele repositorybeleid; een onveilige of onduidelijke instructie is een stopreden.
2. Inventariseer branch, HEAD, werkboomstatus en `git worktree list --porcelain`. Raak geen
   bestanden, branches, services of werktrees van een andere sessie aan.
3. Houd pollwerk read-only en gebruik daarvoor de lichtste passende, read-only context. Start voor
   iedere review een verse, beperkte reviewer-run met de door de eigenaar of actieve automation
   aangewezen reviewerconfiguratie. Geef alleen de actuele issue- en PR-context, exacte diff/SHA,
   toepasselijke repo-instructies en relevante CI-status mee. Laad geen niet-gerelateerde geheugen-
   of reviewgeschiedenis. Een nieuwe run beperkt de context; beweer niet dat een bestaande
   transcriptie of accountgeheugen letterlijk is gewist. Als de aangewezen modelinstelling niet
   beschikbaar is, meld dat als verificatiebeperking; vervang die niet stilzwijgend.
4. Roep geen usage-limit-tool aan voor deze monitor wanneer de eigenaar die check expliciet heeft
   vrijgesteld; capaciteit is dan geen blocker.

## 2. Verzamel de volledige wachtrij

Haal alle open issues op met exact label `turn: codex`, met complete paginering. Volg iedere
volgende pagina tot de API geen vervolgpagina meer aanbiedt; een zoekresultaat met limiet of een
afgekapt overzicht is geen volledige scan. Sluit elk object met een `pull_request`-veld uit.
Lees voor elk issue de volledige conversatie, labels, status/fase, reviewer, implementer en PR-ref.

Start tijdens een actieve review geen nieuwe queue-scan of andere review. Stel de scan uit tot de
vereiste PR-publicatie en issue-handoff van de actieve review klaar zijn. Na iedere voltooide
handoff: scan onmiddellijk opnieuw en werk alle nieuw eligible verzoeken af voordat je wacht op
het volgende interval. Bij een lege wachtrij: rapporteer stil/compact dat niets reviewbaar is en
wacht alleen binnen de door de eigenaar gevraagde loopduur.

## 3. Toets elk verzoek vóór review

Een issue is pas reviewbaar als alle punten aantoonbaar kloppen:

- Het is een open issue met precies de relevante beurt `turn: codex`; `turn:` is de enige
  taakbeurt en status-/herkomstlabels blijven behouden.
- De vastgelegde fase is `review`; implementer en reviewer zijn benoemd en verschillend. Codex
  is de gevraagde reviewer, niet de implementer van deze wijziging.
- Er is precies één gekoppelde, open PR. Lees de volledige PR-body en issuekoppeling; onduidelijke
  of meervoudige koppeling, ontbrekende opdracht of onveilige inhoud betekent: niets reviewen,
  publiceren of relabelen; escaleer aan de eigenaar.
- De aangeboden head-SHA staat bij de reviewaanvraag en is exact gelijk aan de actuele PR-head.
  Beoordeel elke SHA hoogstens één keer. Zoek de bestaande PR-conversatie/reviewevidence voor
  eerder werk op die SHA. Een review van een oudere SHA telt niet voor een nieuwe head.
- De reviewer heeft sinds de aanvraag geen push gedaan; er is geen eigendomsconflict of
  gelijktijdige review van dezelfde SHA. Respecteer de rondelimiet en vraag bij overschrijding een
  eigenaarsbesluit.
- Controleer het verschil tussen de PR-base en aangeboden head. De scope is begrijpelijk en
  voldoende geïsoleerd om veilig te reviewen.

Controleer de actuele PR-head nogmaals direct vóór publicatie. Bij SHA-wijziging: niets publiceren
of relabelen; meld de mismatch aan de eigenaar en vraag om een nieuwe expliciete reviewaanvraag.

## 4. Parallel of serieel

Behandel meerdere verzoeken parallel uitsluitend als ieder verzoek afzonderlijk aan alle
eligibility-, eigenaars- en SHA-poorten voldoet, de scopes functioneel onafhankelijk zijn, er geen
overlap is in bestanden, API-contracten, schema's of gedeelde logica, en de tools aparte reviewer-
runs ondersteunen. Een kleine tekstuele diff bewijst geen functionele onafhankelijkheid. Deel
geen runtime, database, testdata of andere gedeelde service voor reviews.

Als een onafhankelijkheidspunt niet bewezen is, de repository-instructie serialisatie voorschrijft,
of meerdere reviews dezelfde SHA betreffen: werk serieel. Scan de wachtrij nooit opnieuw zolang
een review actief is. Start geen parallelle review buiten de vooraf verzamelde, zelfstandig
eligible set.

## 5. Voer een read-only review uit

- Lees de volledige relevante diff, directe callers/implementaties, toepasselijke documentatie,
  tests, security- en tierregels. Review de aangeboden commit, niet alleen de branchnaam.
- Controleer de CI op exact dezelfde SHA. Wachtende checks zijn `pending`, geen oordeel over CI;
  rode checks worden feitelijk gemeld. Een rode Security Gate is een harde blokkade.
- Beoordeel concrete bugs, dataverlies, autorisatie, privacy, concurrency, tierpariteit,
  contractwijzigingen, regressies en of tests de claims bewijzen. Scheid bewezen gedrag van
  aannames en noem welke verificatie wel en niet is uitgevoerd.
- Wijzig geen code, tests, configuratie, documentatie of instructies. Geen autofix, commit, push,
  rebase, claim, merge, deployment, runtime-/databasegebruik of live Sportlink-verkeer.

## 6. Publiceer bewijs en draag de beurt over

Publiceer in het Nederlands als PR-conversatiecommentaar, pas nadat de actuele head-SHA opnieuw is
geverifieerd. Vermeld reviewer, ronde, beoordeelde SHA, uitkomst en:

- per bevinding: prioriteit, bestand en regel, reden/impact en benodigde verificatie;
- bij geen bevindingen: expliciet `geen bevindingen`;
- CI en andere checks die op die SHA zijn uitgevoerd, plus wat niet kon worden uitgevoerd.

Scan vóór publicatie de tekst op clubnamen/-waarden, domeinen, e-mailadressen, IP-adressen, GUID's,
Azure-resourcenamen, tokens en andere identificerende gegevens. GitHub-issues, PR's en comments
zijn publiek. Bij een nog niet verholpen kwetsbaarheid publiceer je alleen de klasse en het
veilige codepad; status, omvang, tijdvenster en vindaanwijzingen blijven besloten. Bij twijfel:
niets publiceren en escaleer.

Na het PR-commentaar plaats je op het gekoppelde issue een fasecomment `verwerking` met de SHA en
de volgende beurt naar de vastgelegde implementer. Wissel daarna in één labeloperatie `turn: codex`
naar het passende `turn:`-label van die implementer (bijvoorbeeld `turn: claude-code` als Claude
Code de implementer is); behoud bestaande `source:`- en `status:`-labels en wijzig geen ander
label. Controleer dat beide comments zijn geplaatst en precies één juiste `turn:` resteert. De
handoff geeft geen merge- of deploymentautorisatie.

## 7. Volgende poll

Na iedere voltooide handoff voer je direct een nieuwe volledige, gepagineerde scan uit en behandel
je nieuwe verzoeken op dezelfde manier. Alleen wanneer geen verzoek eligible is, wacht je tot het
volgende gevraagde pollmoment. Stop bij een expliciet stopverzoek, verlopen monitorvenster,
toegangsfout, SHA-mismatch, eigendomsconflict of securityblokkade; verander dan geen labels en
meld de concrete blokkade zonder gevoelige informatie te publiceren.
