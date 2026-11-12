# Agentinstructies volledig laden — wat elk harnas leest en hoe je het controleert (#1898)

> Doel: `AGENTS.md` en `FunctionApp/AGENTS.md` worden door Claude Code, Codex en GitHub Copilot volledig
> gelezen, ook met een ander model erachter (bijvoorbeeld Gemini in Copilot). Het **harnas** bepaalt wat er
> geladen wordt; het **model** bepaalt hoe goed lange, dichte tekst wordt nageleefd. Dit document legt de
> gedocumenteerde limieten vast (bron per rij, geraadpleegd 2026-11-12; limieten zijn versie- en
> modelafhankelijk, controleer de bron bij een laadprobleem) en een controle die je zelf kunt draaien.
> Documentatie is geen runtimegarantie en een waargenomen canary is geen bewijs van naleving: zie
> "Wat de canary wel en niet bewijst".

## Wat elk harnas doet

### Claude Code ([memory-documentatie](https://code.claude.com/docs/en/memory))

- **Laden bij opstarten:** met een `CLAUDE.md` komt `AGENTS.md` via de `@AGENTS.md`-import (maximaal vier niveaus
  diep). Claude leest `AGENTS.md` alleen zelf als er geen `CLAUDE.md` is (vanaf een in de bron genoemde minimale
  versie; controleer die bij een afwijking).
- **Harde limiet bij opstarten:** een bestand van meer dan 4 MiB wordt overgeslagen en niets ervan geladen. Onze
  bestanden zijn ruim daaronder.
- **Laden op aanvraag (subdirectory-`CLAUDE.md`, ook `FunctionApp/CLAUDE.md`, en de bestanden die ze importeren):**
  pas zodra Claude in die map een bestand leest. Hier geldt een kleinere limiet per bestand: 40.000 tekens bij een
  contextvenster van 200K; bij 1M-context 200.000 of 150.000 tekens, afhankelijk van het model. Bij overschrijding
  krijgt Claude alleen het begin, met een notitie om de rest met de Read-tool te lezen. `FunctionApp/AGENTS.md` (ongeveer
  2,6 KB) zit daar ver onder.
- **Richtlijn, geen harde grens:** minder dan 200 regels per bestand; langere en dichtere bestanden worden slechter
  nageleefd. De guard begrenst daarom de regellengte (600 tekens).

### Codex ([AGENTS.md-handleiding](https://learn.chatgpt.com/docs/agent-configuration/agents-md))

- **Project:** `AGENTS.md` van de projectroot naar de werkmap, achter elkaar geplakt; het meest specifieke bestand
  staat laatst.
- **Budget:** `project_doc_max_bytes`, standaard 32 KiB, geldt voor de bestanden van deze keten samen. Volgens de
  broncode (`load_project_instructions` en `read_agents_md` in `codex-rs/core/src/agents_md.rs`, bekeken 2026-11-12;
  versiegebonden, geen lokale laadtest) loopt het budget terug per geladen bestand en wordt een bestand dat nog
  gedeeltelijk past **afgekapt** (met een waarschuwing), niet geheel weggelaten. Pas als het budget op is, valt een
  volgend bestand helemaal weg. Het meest specifieke bestand (`FunctionApp/AGENTS.md`) is dus het eerste dat verloren gaat.
- **Globaal `~/.codex/AGENTS.md`:** wordt volgens dezelfde broncode afzonderlijk geladen en niet van het
  projectbudget afgetrokken. De handleiding zegt hierover alleen "combined size". De 2 KiB marge van de guard
  (keten maximaal 30 KiB) is daarom een **conservatieve projectmarge**, geen eis aan een globaal bestand.

### GitHub Copilot ([custom instructions](https://docs.github.com/en/copilot/concepts/prompting-and-context/custom-instructions))

- Code review en cloud agent lezen `AGENTS.md`; een `CLAUDE.md` of `GEMINI.md` in de root is een alternatief.
- **Voorrang:** de documentatie zegt dat voorrang per client en oppervlak verschilt. GitHub.com-chat kent een expliciete
  volgorde (persoonlijk, repository, organisatie); de meeste clients, waaronder Copilot CLI, VS Code en Visual
  Studio, voegen toepasselijke instructiebestanden samen. Dat het dichtstbijzijnde `AGENTS.md` voorrang heeft, is
  gedocumenteerd; **of het bovenliggende bestand dan ontbreekt, is per oppervlak niet bevestigd.** Test dat.
- **Limiet code review:** de eerdere limiet van 4000 tekens is volgens de
  [changelog van 2026-06-12](https://github.blog/changelog/2026-06-12-copilot-code-review-new-configurations-and-controls/)
  verwijderd voor `copilot-instructions.md` en `*.instructions.md` onder `.github`. Voor `AGENTS.md` is dat niet
  bevestigd.
- Of een stub-`CLAUDE.md` zonder import-expansie wordt gelezen, en of een `AGENTS.md` voorrang heeft als beide
  bestaan, is onbekend: test met de canary.

### Gemini

Als model in Copilot bepaalt het harnas het laden. Gemini CLI als losse tool leest standaard `GEMINI.md` (niet nagezocht voor deze repository).

## Globale bestanden

Een globaal `~/.codex/AGENTS.md` telt volgens de broncode niet mee in het projectbudget (zie hierboven; versiegebonden).
Houd het toch klein: het kost contextruimte en kan regels uit dit project tegenspreken. De guard kan een bestand op
andermans machine niet zien; dit is een afspraak, geen bewaking.

## De canary-test (laadcontrole)

De laatste regel van elke `AGENTS.md` begint met `Laadcontrole:` en bevat een canary (de guard dwingt dat af).

### Wat de canary wel en niet bewijst

- Een geciteerde canary bewijst dat de slotregel **in de context kwam**, op welke manier dan ook. Een agent kan het
  bestand ook met een tool lezen; dat zou het laden niet bewijzen.
- Een model dat de canary niet kan citeren, bewijst niet dat het bestand niet geladen is; modellen herhalen geladen
  tekst niet altijd betrouwbaar.
- Canary en laadbron samen zijn pas sluitend: de canary is gezien **en** er is geen toolaanroep geweest om het
  bestand te lezen **en** de lijst van geladen bronnen bevat het bestand.
- Naleving van de regels bewijst deze test nooit.

### Per oppervlak

1. **Claude Code (CLI en extensie).** Start een nieuwe sessie in de repositoryroot. Run `/context all` en controleer
   onder **Memory files** dat `CLAUDE.md` en (via de import) de inhoud van `AGENTS.md` aanwezig zijn. Vraag dan, zonder
   toolgebruik toe te staan (weiger elke toolaanroep): "Citeer de laatste regel van je instructies die met
   `Laadcontrole:` begint." Verwacht `ROOT-EINDE-1898`. Herhaal in een nieuwe sessie in `FunctionApp/`: lees eerst een
   bestand in `FunctionApp/` (laden op aanvraag) en vraag dan om beide canaries (`ROOT-EINDE-1898` en
   `FUNCTIONAPP-EINDE-1898`).
2. **Codex (app en CLI).** Start een nieuwe sessie in de root en vraag hetzelfde zonder toolgebruik. Controleer in de
   sessielog dat er geen bestand gelezen is vóór het antwoord. Herhaal in `FunctionApp/`: beide canaries verwacht. Mist
   de laatste, dan is de keten afgekapt: dat is een budgetprobleem.
3. **Copilot chat en IDE, CLI.** Zelfde vraag in een nieuwe sessie met de repository als context; controleer in de
   zichtbare context/referenties welke instructiebestanden meegestuurd zijn. Noteer per oppervlak of ook de root-canary zichtbaar is
   als je in `FunctionApp/` werkt (samenvoegen of vervangen).
4. **Copilot code review en cloud agent.** Er is geen interactieve sessie waarin je de vraag kunt stellen. Een
   test hier vraagt een bewuste testpull request waarin de reviewinstructie "citeer de laadcontrole-canary" wordt
   meegegeven, en is daarom **nog niet getest**; noteer dat zo bij het resultaat.
5. **Gemini:** herhaal stap 3 met Gemini als gekozen model in Copilot; het resultaat zegt iets over het harnas, niet over het model.

Leg het resultaat vast in het issue: harnas, oppervlak, versie, "canary gezien: ja/nee", "laadbron gecontroleerd: ja/nee"
en "toolgebruik uitgesloten: ja/nee". Geen sessie-inhoud. Een ontbrekende canary of een niet-uitgesloten toolaanroep telt nooit
als geslaagd.

## Wat de guard wel en niet bewijst

`scripts/ci/check-agent-instructies.py` bewaakt omvang per bestand en per keten (ratchet), een maximale
regellengte (600 tekens) en de aanwezigheid van de canary als laatste regel. Hij bewijst niet dat een harnas
het bestand ook echt laadt of naleeft; dat is alleen met de procedure hierboven (en nooit volledig) vast te stellen.
