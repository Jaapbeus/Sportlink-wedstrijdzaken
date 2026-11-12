# Agentinstructies volledig laden — wat elk harnas leest en hoe je het controleert (#1898)

> Doel: `AGENTS.md` en `FunctionApp/AGENTS.md` worden door Claude Code, Codex en GitHub Copilot volledig
> gelezen, ook met een ander model erachter (bijvoorbeeld Gemini in Copilot). Het **harnas** bepaalt wat er
> geladen wordt; het **model** bepaalt hoe goed lange, dichte tekst wordt nageleefd. Dit document legt de
> bekende limieten vast (bron: documentatie per harnas, geraadpleegd 2026-11-12) en een controle die je zelf
> kunt draaien. Documentatie is geen runtimegarantie: de canary-test hieronder is het bewijs.

## Wat elk harnas doet

| Harnas | Laden | Limiet of afkapping | Gevolg voor deze repository |
|---|---|---|---|
| Claude Code | Leest `AGENTS.md` alleen zelf als er géén `CLAUDE.md` is; met een `CLAUDE.md` komt `AGENTS.md` via de `@AGENTS.md`-import (maximaal vier niveaus diep). Een `CLAUDE.md` in een submap laadt pas zodra Claude een bestand in die map leest. | Geen harde limiet. Richtlijn: minder dan 200 regels per bestand; langere en dichtere bestanden worden slechter nageleefd (geen afkapping). | Elke `AGENTS.md` heeft een stub-`CLAUDE.md`. Houd regels kort (guard: maximaal 600 tekens per regel). |
| Codex | Plakt `AGENTS.md` van de projectroot naar de werkmap aan elkaar, na het globale `~/.codex/AGENTS.md`. Later = specifieker. | `project_doc_max_bytes`, standaard 32 KiB over de hele keten; daarna stopt Codex met toevoegen. Het laatste, meest specifieke bestand valt dus als eerste weg. | Keten maximaal 30 KiB; de resterende ruimte is bedoeld voor een globaal bestand. Zie "Globale bestanden". |
| GitHub Copilot | Code review en cloud agent lezen `AGENTS.md` (het dichtstbijzijnde bestand wint, dus niet samengevoegd); een `CLAUDE.md` of `GEMINI.md` in de root is een alternatief. De eerdere limiet van 4000 tekens voor code review is volgens de GitHub-changelog van 2026-06-12 verwijderd voor `.github`-instructiebestanden; voor `AGENTS.md` is dat niet bevestigd. | Onbevestigd voor `AGENTS.md`. | `FunctionApp/AGENTS.md` bevat daarom een korte samenvatting van de harde stops uit de root. Of Copilot een stub-`CLAUDE.md` zonder import-expansie leest, is onbekend: test met de canary. |
| Gemini | Als model in Copilot: het harnas bepaalt het laden. Gemini CLI als losse tool leest standaard `GEMINI.md` (niet nagezocht voor deze repository). | n.v.t. | Test per oppervlak met de canary. |

## Globale bestanden

Codex telt het globale `~/.codex/AGENTS.md` waarschijnlijk mee in dezelfde 32 KiB (de documentatie zegt
alleen "combined size"). De guard houdt de keten daarom op 30 KiB, zodat er 2 KiB overblijft. **Houd een
globaal `~/.codex/AGENTS.md` kleiner dan 2 KiB**, of verhoog `project_doc_max_bytes` in je eigen
`~/.codex/config.toml`. De guard kan een bestand op andermans machine niet zien; dit is een afspraak.

## De canary-test (laadcontrole)

De laatste regel van elke `AGENTS.md` begint met `Laadcontrole:` en bevat een canary (de guard dwingt dat af).
Een agent die de canary kan citeren, heeft het bestand tot het einde geladen.

1. Start een **nieuwe** sessie in de repositoryroot, zonder eerst bestanden te lezen.
2. Vraag: "Citeer de laatste regel van je projectinstructies die met `Laadcontrole:` begint."
   Verwacht: `ROOT-EINDE-1898`.
3. Start een nieuwe sessie in `FunctionApp/` en vraag hetzelfde voor beide bestanden. Verwacht: zowel
   `ROOT-EINDE-1898` als `FUNCTIONAPP-EINDE-1898`. Ontbreekt de root-canary, dan wint de submap de
   samenvoeging niet (Copilot-gedrag, of een niet-goedgekeurde import in Claude Code).
4. Herhaal per harnas en per oppervlak: Claude Code (CLI en extensie), Codex (app en CLI), Copilot (code
   review, cloud agent, chat/IDE) en eventueel Copilot met Gemini.
5. Leg het resultaat vast in het issue (alleen het harnas, de oppervlakte en "canary gezien: ja/nee", geen
   sessie-inhoud).

Een ontbrekende canary betekent: die combinatie van harnas en oppervlak leest het bestand niet (volledig).
Meld dat; verhoog niet zomaar het budget.

## Wat de guard wel en niet bewijst

`scripts/ci/check-agent-instructies.py` bewaakt omvang per bestand en per keten (ratchet), een maximale
regellengte (600 tekens) en de aanwezigheid van de canary als laatste regel. Hij bewijst niet dat een harnas
het bestand ook echt laadt of naleeft; dat bewijst alleen de canary-test hierboven.
