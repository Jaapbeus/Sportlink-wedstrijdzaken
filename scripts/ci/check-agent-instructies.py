#!/usr/bin/env python3
"""check-agent-instructies.py (#1579, #1580) — read-only guard: één bron voor agentinstructies en skills.

WAAROM DEZE GUARD BESTAAT
-------------------------
`AGENTS.md` is de enige bron van de agentinstructies, voor Codex én Claude Code. Claude Code leest
een `AGENTS.md` niet zelf zodra er een `CLAUDE.md` in of boven de werkmap staat — ook niet in een
submap — dus naast elke `AGENTS.md` staat een `CLAUDE.md`-stub die alleen `@AGENTS.md` importeert.
Staat er inhoud in een stub, dan bestaat die instructie op twee plekken en loopt ze vanzelf uiteen.

Codex leest van de projectinstructies standaard maximaal 32 KiB (`project_doc_max_bytes`), over alle
gecombineerde bestanden van de keten samen, en kapt de rest stilzwijgend af (#1580). Daarom bewaakt
deze guard ook de omvang, met een ratchet-plafond per bestand en per keten.

De guard dwingt af:
  1. naast elke `AGENTS.md` staat een `CLAUDE.md`, en andersom;
  2. een `CLAUDE.md` bestaat uit exact de import `@AGENTS.md`, eventueel gevolgd door exact de
     vaste zin STUB_ZIN (lege regels mogen), blijft onder STUB_MAX_BYTES, en de zin staat niet ook
     in de `AGENTS.md` ernaast. Elke andere regel — ook een verwijzing, commentaar of tweede
     import — faalt: niets mag een tweede instructiebron worden;
  3. `AGENTS.md` is niet leeg (ook niet uit alleen witruimte) en geen gegenereerd bestand meer;
  4. skills hebben één bron: `.agents/skills/` met een identieke kopie in `.claude/skills/`, of staan
     in `scripts/ci/skills-alleen-claude.txt` (zie `scripts/ci/sync-skills.py`);
  5. Markdown-codeblokken in skills en `AGENTS.md` zijn afgesloten;
  6. er bestaat geen BEKEND tweede laadpad voor instructies (de toelatingsmatrix hieronder);
  7. de omvang past binnen het plafond per bestand en per keten (`agent-instructies-plafonds.txt`).

TOELATINGSMATRIX (de ene lijst; docs/ARCHITECTUUR-CODEKWALITEIT.md verwijst hierheen)
----------------------------------------------------------------------------------
Bron van de matrix: de actuele documentatie van beide clients (Claude Code: memory- en settings-referentie;
Codex: config-referentie en config-basics), geraadpleegd bij #1580. Documentatie is geen runtimegarantie, en
een kanaal dat nog niet gedocumenteerd of niet bekend is, kan deze guard niet zien.

TOEGESTAAN
  * `AGENTS.md` (root en submappen) met een stub-`CLAUDE.md` ernaast, nooit in een metadatamap;
  * skills uit `.agents/skills/` met een identieke kopie in `.claude/skills/`, alleen in de repositoryroot, plus
    Claude-only skills uit `scripts/ci/skills-alleen-claude.txt`;
  * een getrackt `.claude/settings.json` met UITSLUITEND de sleutels `$schema` en `permissions` (daarbinnen alleen
    `allow`, `deny`, `ask`) — een toelatingslijst, geen verbodslijst: ook een nieuwe of een alias-sleutel faalt;
  * een `.codex/config.toml` met UITSLUITEND de hoofdsleutels/-tabellen uit CODEX_TOEGESTAAN (gelezen als
    TOML-structuur, dus ook gequote, gepunte, in een profiel of in een inline-tabel): een toelatingslijst, geen
    verbodslijst — ook een nieuwe of nog niet gedocumenteerde sleutel faalt (`model_catalog_json` bleef anders groen).

VERBODEN, in de repositoryroot én in elke submap, aanwezig óf door git getrackt (ook in een map die de doorloop
overslaat, zoals bin/obj/node_modules — `git ls-files` is leidend):
  * `.claude/rules/`, `.claude/commands/`, `.claude/agents/`, `.claude/output-styles/` — Claude Code laadt ze als
    prompt/instructie zodra ze ontdekt of geactiveerd worden, Codex niet;
  * een instructiebestand (`AGENTS.md`, `CLAUDE.md`, `CLAUDE.local.md`, `AGENTS.override.md`) direct onder een
    metadatamap `.claude/`, `.agents/` of `.codex/`: Claude Code leest `.claude/CLAUDE.md` en `.claude/AGENTS.md`
    als projectinstructies, een Codex-sessie in de root volgt alleen root → werkmap — twee bronketens;
  * een `.claude/skills/` of `.agents/skills/` in een submap (geneste skills);
  * `.codex/skills|prompts|agents|rules|commands/` en `.codex/hooks.json`;
  * `AGENTS.override.md` — vervangt de AGENTS.md in dezelfde map en maakt het één-bronbeleid stuk;
  * een symlink op elk van deze plekken, op `AGENTS.md`, `CLAUDE.md` of in `.claude/`, `.agents/`, `.codex/` —
    we volgen geen symlinks, dus we weigeren ze (geen cyclus- of padgrenzen nodig).
Verboden zodra git het trackt, in elke vorm in de index (gewoon bestand, symlink, index-mode 120000, ook als
het uit de werkboom is verwijderd): `CLAUDE.local.md`, `.claude/settings.local.json` en `.mcp.json`. Lokaal
zijn dit persoonlijke, genegeerde bestanden en blijven ze bewust buiten de garantie.
Een aanwezige maar kapotte Git-verwijzing (bijvoorbeeld een dangling `.git`-symlink) is een fout; alleen een
werkelijk afwezige repository (de unit-fixtures) valt terug op de werkboom.
Een legitieme toekomstige subagent, command, hook of config vraagt een eigenaarsbesluit én een wijziging van
déze guard — dat is de uitzondering, als diff die iemand goedkeurt.

Wat dit NIET bewijst: dat de inhoud juist is, dat de agent hem leest, dat een niet-gedocumenteerd
kanaal niet bestaat, of dat een gebruikersinstelling (of een niet-vertrouwd project) op een andere
machine het budget of de bronselectie verandert. Dat Claude Code de import laadt is handmatig
vastgesteld bij #1579 en staat in dat issue.
"""

from __future__ import annotations

import json
import os
import re
import runpy
import subprocess
import sys
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

ROOT = Path(__file__).resolve().parents[2]
SYNC = runpy.run_path(str(Path(__file__).with_name("sync-skills.py")))

STUB_MAX_BYTES = 300
STUB_IMPORT = "@AGENTS.md"
STUB_ZIN = "AGENTS.md is de enige bron van de agentinstructies; zet hier geen inhoud bij."
TOEGESTANE_STUBS = ([STUB_IMPORT], [STUB_IMPORT, STUB_ZIN])
VERPLICHTE_SKILLS = {"autonoom", "release", "sluitsessie", "startdebug"}
CODEX_MAX_BYTES = 32 * 1024  # project_doc_max_bytes, standaard; telt de bestandsbytes, niet de scheidingstekens
KETEN_SCHEIDING_BYTES = 2    # Codex voegt "\n\n" tussen twee bestanden; ruim meegeteld (#1580)
PLAFONDS = "scripts/ci/agent-instructies-plafonds.txt"
PLAFOND_RUIMTE = 1024        # winst groter dan dit moet in hetzelfde PR in het plafond worden vastgezet
NEGEER_MAPPEN = {".git", "node_modules", "bin", "obj", "packages", ".venv", "artifacts"}

INSTRUCTIEBESTANDEN = {"AGENTS.md", "CLAUDE.md", "CLAUDE.local.md", "AGENTS.override.md"}
INSTRUCTIEMAPPEN = {".claude", ".agents", ".codex"}
# (bovenliggende map, submap): alles eronder is een tweede laadpad, op elk niveau van de boom.
VERBODEN_KANAALMAPPEN = {
    (".claude", "rules"): "regelbestanden",
    (".claude", "commands"): "slash-commands",
    (".claude", "agents"): "subagent-definities",
    (".claude", "output-styles"): "output styles",
    (".codex", "skills"): "Codex-skills buiten .agents/skills",
    (".codex", "prompts"): "Codex-prompts",
    (".codex", "agents"): "Codex-agents",
    (".codex", "rules"): "Codex-regels",
    (".codex", "commands"): "Codex-commands",
}
SKILLMAPPEN = {(".claude", "skills"), (".agents", "skills")}
# Toelatingsmatrix voor een in git getrackt `.claude/settings.json` (#1580): ALLEEN deze sleutels zijn
# toegestaan; elke andere sleutel faalt, ook een die vandaag nog niet bestaat of een alias is. Onderaan staat
# per bekende sleutel waarom hij een tweede instructiebron of laadpad is (bron: Claude Code settings-referentie
# en memory-documentatie; documentatie is geen runtimegarantie).
SETTINGS_TOEGESTAAN = {"$schema", "permissions"}
PERMISSIONS_TOEGESTAAN = {"allow", "deny", "ask"}  # niet: additionalDirectories (laadt CLAUDE.md/rules van een andere map)
SETTINGS_BEKEND = {
    "hooks": "hooks injecteren context of gedrag", "outputStyle": "output styles wijzigen rol en toon",
    "agent": "start elke sessie als een subagent met eigen prompt", "enabledPlugins": "plugins brengen skills, agents en hooks mee",
    "extraKnownMarketplaces": "registreert pluginbronnen", "additionalMarketplaces": "alias van extraKnownMarketplaces",
    "strictKnownMarketplaces": "pluginbronnen", "pluginConfigs": "conservatief projectverbod: volgens de referentie sinds "
    "2.1.207 alleen user/managed (project-entries worden genegeerd), en het stelt anders de bronselectie van AGENTS.md in "
    "(cc-plugin-agents-md@builtin)",
    "claudeMd": "injecteert instructies", "claudeMdExcludes": "sluit gedeelde instructiebestanden uit",
    "autoMemoryDirectory": "leidt het geheugen om naar een andere map", "autoMemoryEnabled": "wijzigt het geheugen",
    "env": "kan instructieladen wijzigen (CLAUDE_CODE_*)", "skillOverrides": "wijzigt welke skills zichtbaar zijn",
    "enabledMcpjsonServers": "MCP-servers leveren instructies", "enableAllProjectMcpServers": "MCP-servers leveren instructies",
    "disabledMcpjsonServers": "MCP-servers", "includeGitInstructions": "wijzigt de ingebouwde prompt",
    "language": "wijzigt de promptinstructie voor de antwoordtaal", "statusLine": "voert een commando uit",
    "fileSuggestion": "voert een commando uit", "disableAllHooks": "wijzigt hookgedrag",
}
# Toelatingsmatrix voor `.codex/config.toml` (#1580, ronde 2): ALLEEN deze hoofdsleutels/-tabellen zijn toegestaan,
# ook binnen een profiel (`profiles.<naam>.…`). Een verbodslijst bleek onvolledig (`model_catalog_json` laadt via
# een catalogus extra instructievelden en bleef groen); een toelatingslijst weigert ook wat nog niet bekend is.
CODEX_TOEGESTAAN = {"model", "model_reasoning_effort", "approval_policy", "sandbox_mode", "sandbox_workspace_write"}
CODEX_TOEGESTAAN_TABELLEN = {"sandbox_workspace_write"}  # tabel: de subsleutels zijn vrij
# Waarom een bekende sleutel of tabel een instructie-, bron- of laadkanaal is (bron: Codex config-referentie en
# config-basics, Codex 0.158.0); alleen voor de foutmelding — de toelatingslijst bepaalt wat faalt.
CODEX_REDEN = {
    "project_doc_max_bytes": "wijzigt het budget van de projectinstructies", "project_doc_fallback_filenames": "wijzigt de bronselectie",
    "project_root_markers": "wijzigt de bronselectie", "developer_instructions": "injecteert instructies",
    "additional_developer_instructions": "injecteert instructies", "model_instructions_file": "vervangt de ingebouwde instructies",
    "experimental_instructions_file": "gedeprecieerde alias van model_instructions_file", "instructions": "gereserveerd voor instructies",
    "compact_prompt": "vervangt de compactieprompt", "experimental_compact_prompt_file": "vervangt de compactieprompt",
    "model_catalog_json": "laadt een modelcatalogus met instructievelden", "personality": "wijzigt de promptstijl",
    "config_file": "laadt een extra configuratielaag", "agents": "agentrollen met eigen configuratielaag",
    "hooks": "hooks injecteren gedrag", "plugins": "plugins brengen skills/hooks mee", "marketplaces": "pluginbronnen",
    "skills": "skillactivering", "projects": "projectvertrouwen activeert `.codex/`-lagen", "mcp_servers": "MCP-servers leveren instructies",
    "features": "schakelt hooks/plugins in", "auto_review": "Markdown-beleid voor automatische review", "notify": "voert een commando uit",
    "memories": "geheugenmodellen", "model_providers": "provider met auth-commando", "profiles": "profielen",
}


def onafgesloten_codeblok(tekst: str) -> Optional[int]:
    opening = None
    for nummer, regel in enumerate(tekst.splitlines(), 1):
        match = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", regel)
        if not match:
            continue
        marker, rest = match.groups()
        if opening is None:
            # Backticks in the info string cannot open a backtick fence.
            if marker[0] == "`" and "`" in rest:
                continue
            opening = (marker[0], len(marker), nummer)
        elif marker[0] == opening[0] and len(marker) >= opening[1] and not rest.strip():
            opening = None
    return opening[2] if opening else None


class Inventaris:
    """Eén doorloop: wat er staat (zonder andermans worktrees en buildmappen) én wat git trackt.

    Paden zijn relatief aan de root, met `/` als scheiding. `getrackt` komt uit `git ls-files` en
    kent dus geen uitgesloten mappen: een geforceerd getrackt instructiebestand in `bin/` of
    `node_modules/` verdwijnt niet uit beeld door zijn mapnaam.
    """

    def __init__(self, root: Path):
        self.root = root
        self.aanwezig: Set[str] = set()
        self.symlinks: Set[str] = set()
        self.getrackt: Set[str] = set()
        self.index: Set[str] = set()  # alles wat git trackt, ook als het uit de werkboom is verwijderd
        self.git_fout: Optional[str] = None
        self._doorloop()
        self._git()

    def _doorloop(self) -> None:
        for map_, submappen, namen in os.walk(self.root):
            pad = Path(map_)
            submappen[:] = [
                d for d in submappen
                if d not in NEGEER_MAPPEN and not (d == "worktrees" and pad.name in {".claude", ".codex"})
            ]
            for naam in list(submappen) + list(namen):
                volledig = pad / naam
                rel = volledig.relative_to(self.root).as_posix()
                if volledig.is_symlink():
                    self.symlinks.add(rel)
                elif naam in namen:
                    self.aanwezig.add(rel)

    def _git(self) -> None:
        # lexists, niet exists: een aanwezige maar kapotte verwijzing (dangling symlink, .git-bestand naar niets)
        # is geen afwezige repository — git faalt dan en dat moet zichtbaar worden.
        if not os.path.lexists(self.root / ".git"):
            return
        try:
            uitvoer = subprocess.run(
                ["git", "-C", str(self.root), "ls-files", "-z", "--stage"],
                check=True, capture_output=True,
            ).stdout
        except (OSError, subprocess.CalledProcessError) as fout:
            # Stil terugvallen op de werkboom zou de git-controle onzichtbaar uitschakelen — juist
            # dat is wat een geforceerd getrackt bestand nodig heeft om ongezien te blijven.
            self.git_fout = str(fout).strip().splitlines()[0] if str(fout).strip() else type(fout).__name__
            return
        for item in uitvoer.split(b"\0"):
            if not item:
                continue
            kop, _, pad = item.partition(b"\t")
            rel = pad.decode("utf-8", "surrogateescape")
            self.index.add(rel)
            if not os.path.lexists(self.root / rel):
                continue  # getrackt maar uit de werkboom verwijderd: niets om te laden
            self.getrackt.add(rel)
            if kop.split(b" ")[0] == b"120000":
                self.symlinks.add(rel)

    def bestanden(self) -> Set[str]:
        """Alle bestanden die een agent kan laden: aanwezig of getrackt (symlinks apart)."""
        return (self.aanwezig | self.getrackt) - self.symlinks

    def vind(self, bestandsnaam: str) -> List[Path]:
        return sorted(self.root / p for p in self.bestanden() if p.split("/")[-1] == bestandsnaam)


def lees_tekst(pad: Path) -> str:
    return pad.read_bytes().decode("utf-8", "replace")


def controleer_stub(stub: Path, root: Path) -> List[str]:
    rel = stub.relative_to(root)
    agents = stub.with_name("AGENTS.md")
    fouten = []
    if not agents.is_file():
        return [f"{rel}: geen AGENTS.md ernaast — een stub zonder bron laadt niets"]

    tekst = lees_tekst(stub)
    regels = [r.strip() for r in tekst.splitlines() if r.strip()]
    if len(tekst.encode("utf-8")) > STUB_MAX_BYTES:
        fouten.append(f"{rel}: stub is langer dan {STUB_MAX_BYTES} bytes — instructies horen in AGENTS.md")
    # Geen vormcontrole maar een vaste toegestane inhoud: elke andere regel — ook een zin die
    # 'AGENTS.md' noemt, commentaar of een tweede import — is een mogelijke tweede instructiebron.
    if regels not in TOEGESTANE_STUBS:
        fouten.append(f"{rel}: een stub bestaat uit exact '{STUB_IMPORT}', eventueel gevolgd door exact de zin "
                      f"'{STUB_ZIN}' — alles anders hoort in AGENTS.md")
    bron_regels = {r.strip() for r in lees_tekst(agents).splitlines() if r.strip()}
    if STUB_ZIN in bron_regels:
        fouten.append(f"{rel}: de stubzin staat ook in AGENTS.md — één waarheid")
    return fouten


def controleer_instructiebestanden(inv: Inventaris) -> List[str]:
    root = inv.root
    fouten = []
    agents = inv.vind("AGENTS.md")
    stubs = inv.vind("CLAUDE.md")
    if root / "AGENTS.md" not in agents:
        fouten.append("AGENTS.md: ontbreekt in de repositoryroot — dit is de enige bron")
    for pad in agents:
        rel = pad.relative_to(root)
        tekst = lees_tekst(pad)
        if not tekst.replace("\ufeff", "").strip():
            fouten.append(f"{rel}: leeg — een AGENTS.md zonder inhoud laadt niets")
        if "GEGENEREERD BESTAND" in tekst:
            fouten.append(f"{rel}: bevat de oude generatorkop — AGENTS.md is geen afgeleid bestand meer")
        if not pad.with_name("CLAUDE.md").is_file():
            fouten.append(f"{rel}: geen CLAUDE.md-stub ernaast — Claude Code leest deze AGENTS.md anders niet")
        regel = onafgesloten_codeblok(tekst)
        if regel:
            fouten.append(f"{rel}:{regel}: onafgesloten codeblok")
    for stub in stubs:
        fouten.extend(controleer_stub(stub, root))
    return fouten


def deel_van_kanaal(delen: Tuple[str, ...]) -> Optional[Tuple[str, int]]:
    """(omschrijving, index van de kanaalmap) als dit pad onder een verboden kanaalmap valt."""
    for i in range(len(delen) - 2):  # er moet nog minstens één element onder de kanaalmap staan
        reeks = (delen[i], delen[i + 1])
        if reeks in VERBODEN_KANAALMAPPEN:
            return VERBODEN_KANAALMAPPEN[reeks], i
    return None


def controleer_kanalen(inv: Inventaris) -> List[str]:
    """Tweede laadpaden voor instructies: Claude-mappen, geneste skills, override en lokaal bestand."""
    fouten = []
    for pad in sorted(inv.bestanden()):
        delen = tuple(pad.split("/"))
        gevonden = deel_van_kanaal(delen)
        if gevonden:
            omschrijving, _ = gevonden
            fouten.append(f"{pad}: {omschrijving} zijn een tweede instructiebron naast AGENTS.md — zet de regel in AGENTS.md")
            continue
        for i in range(len(delen) - 2):
            if (delen[i], delen[i + 1]) in SKILLMAPPEN and i > 0:
                fouten.append(f"{pad}: geneste skillmap — skills staan alleen in de repositoryroot (.agents/skills met kopie in .claude/skills)")
                break
        else:
            if delen[-1] == "AGENTS.override.md":
                fouten.append(f"{pad}: AGENTS.override.md vervangt de AGENTS.md in dezelfde map en omzeilt het één-bronbeleid")
    # Metadatamappen: Claude Code leest `.claude/CLAUDE.md` en `.claude/AGENTS.md` als projectinstructies; een
    # sessie in de root van Codex volgt alleen root → werkmap en leest ze niet. Dat zijn dus twee verschillende
    # bronketens (#1580): niet toegestaan. Aanwezig óf getrackt.
    for pad in sorted(inv.bestanden()):
        delen = pad.split("/")
        if len(delen) >= 2 and delen[-2] in INSTRUCTIEMAPPEN and delen[-1] in INSTRUCTIEBESTANDEN:
            fouten.append(f"{pad}: instructiebestand onder de metadatamap {delen[-2]}/ — Claude Code laadt het, een Codex-sessie in "
                          "de root niet; instructies staan alleen in AGENTS.md naast een stub-CLAUDE.md")
        if pad == ".codex/hooks.json" or pad.endswith("/.codex/hooks.json"):
            fouten.append(f"{pad}: Codex-hookbestand — hooks zijn een tweede instructie-/gedragskanaal naast AGENTS.md")
    # Het trackingverbod geldt voor ELKE vorm in de index: gewoon bestand, symlink, index-mode 120000, en ook
    # als het bestand uit de werkboom is verwijderd (een verse checkout levert het weer op).
    for pad in sorted(inv.index):
        if pad.split("/")[-1] == "CLAUDE.local.md":
            fouten.append(f"{pad}: CLAUDE.local.md is persoonlijk en hoort niet in git (ook niet met git add -f)")
        if pad == ".mcp.json" or pad.endswith("/.mcp.json"):
            fouten.append(f"{pad}: .mcp.json bevat de projectverwijzing en hoort niet in git — gebruik .mcp.json.template")
        if pad.split("/")[-2:] == [".claude", "settings.local.json"]:
            fouten.append(f"{pad}: settings.local.json is persoonlijk en hoort niet in git")
    return fouten


def controleer_symlinks(inv: Inventaris) -> List[str]:
    fouten = []
    for pad in sorted(inv.symlinks):
        delen = pad.split("/")
        if delen[-1] in INSTRUCTIEBESTANDEN or INSTRUCTIEMAPPEN.intersection(delen):
            fouten.append(f"{pad}: symlink op een instructiepad — symlinks worden niet gevolgd en breken op Windows; kopieer of verwijs met een import")
    return fouten


# ── TOML-structuur (Python 3.9: geen tomllib) ───────────────────────────────────────────────────
class TomlFout(ValueError):
    """De tekst is geen TOML die deze eenvoudige structuurlezer kan volgen — de guard faalt dan dicht."""


_BARE = re.compile(r"[A-Za-z0-9_-]+")
_ESCAPES = {"b": "\b", "t": "\t", "n": "\n", "f": "\f", "r": "\r", '"': '"', "\\": "\\", "e": "\x1b"}


def toml_sleutelpaden(tekst: str) -> List[Tuple[str, ...]]:
    """Alle sleutelpaden (tabelkoppen, sleutels, gepunte sleutels, inline-tabellen) van een TOML-document.

    Geen waardevalidatie en GEEN volledige TOML-validator: dubbele sleutels, ongeldige getallen/datums en
    tabelconflicten worden niet geweigerd (Codex of tomllib doet dat wel). Alleen de structuur die nodig is om
    te zien WELKE sleutels er staan, ook als ze
    gequote (`"a.b" = 1`), gepunt (`a."b".c = 1`), in een tabel (`[a.b]`, `[[a]]`), in een profiel of in een
    inline-tabel staan — en om tekst binnen een (meerregelige) string NIET als sleutel te lezen. Ongeldige
    TOML geeft TomlFout; de aanroeper behandelt dat als fout (een onleesbare config bewaakt niets).
    """
    s = tekst.lstrip("\ufeff")  # een lone \r telt als witruimte (ws), dus CRLF hoeft niet genormaliseerd
    n = len(s)
    paden: List[Tuple[str, ...]] = []

    def fout(i: int, wat: str) -> TomlFout:
        return TomlFout(f"{wat} (regel {s.count(chr(10), 0, i) + 1})")

    def ws(i: int, regels: bool = False) -> int:
        while i < n:
            c = s[i]
            if c in " \t\r" or (regels and c == "\n"):
                i += 1
            elif c == "#":
                while i < n and s[i] != "\n":
                    i += 1
            else:
                break
        return i

    def tekenreeks(i: int) -> Tuple[str, int]:
        """Eén- of meerregelige basic/literal string vanaf i; geeft (inhoud, positie erna)."""
        q = s[i]
        meerregelig = s.startswith(q * 3, i)
        i += 3 if meerregelig else 1
        uit: List[str] = []
        while i < n:
            c = s[i]
            if q == '"' and c == "\\":
                if i + 1 >= n:
                    break
                e = s[i + 1]
                if e in _ESCAPES:
                    uit.append(_ESCAPES[e]); i += 2
                elif e in "uU":
                    lengte = 4 if e == "u" else 8
                    hex_ = s[i + 2:i + 2 + lengte]
                    try:
                        uit.append(chr(int(hex_, 16)))
                    except (ValueError, OverflowError):
                        raise fout(i, "ongeldige escape in string")
                    i += 2 + lengte
                elif meerregelig and e in " \t\r\n":
                    # Regeleinde-backslash: `\`, optioneel spaties/tabs, dan LF of CRLF; slikt alle witruimte erna.
                    # Een backslash gevolgd door tekst zonder regeleinde blijft een ongeldige escape.
                    j = i + 1
                    while j < n and s[j] in " \t":
                        j += 1
                    if not (s.startswith("\n", j) or s.startswith("\r\n", j)):
                        raise fout(i, "ongeldige escape in string")
                    i = j
                    while i < n and s[i] in " \t\r\n":
                        i += 1
                else:
                    raise fout(i, "ongeldige escape in string")
                continue
            if meerregelig:
                if c == q:
                    run = 1
                    while i + run < n and s[i + run] == q:
                        run += 1
                    if run >= 3:
                        if run > 5:
                            raise fout(i, "te veel aanhalingstekens")
                        uit.append(q * (run - 3))
                        return "".join(uit), i + run
                    uit.append(q * run); i += run
                    continue
            else:
                if c == q:
                    return "".join(uit), i + 1
                if c == "\n":
                    raise fout(i, "string niet afgesloten op dezelfde regel")
            uit.append(c); i += 1
        raise fout(i, "string niet afgesloten")

    def sleutel(i: int) -> Tuple[List[str], int]:
        delen: List[str] = []
        while True:
            i = ws(i)
            if i >= n:
                raise fout(i, "sleutel verwacht")
            if s[i] in "\"'":
                tekst_, i = tekenreeks(i)
                delen.append(tekst_)
            else:
                m = _BARE.match(s, i)
                if not m:
                    raise fout(i, "ongeldige sleutel")
                delen.append(m.group(0)); i = m.end()
            i = ws(i)
            if i < n and s[i] == ".":
                i += 1
                continue
            return delen, i

    def waarde(i: int, pad: Tuple[str, ...]) -> int:
        i = ws(i)
        if i >= n:
            raise fout(i, "waarde verwacht")
        c = s[i]
        if c in "\"'":
            return tekenreeks(i)[1]
        if c == "[":
            i = ws(i + 1, True)
            while True:
                if i >= n:
                    raise fout(i, "array niet afgesloten")
                if s[i] == "]":
                    return i + 1
                i = ws(waarde(i, pad), True)
                if i < n and s[i] == ",":
                    i = ws(i + 1, True)
                elif i < n and s[i] == "]":
                    return i + 1
                else:
                    raise fout(i, "komma of ] verwacht in array")
        if c == "{":
            i = ws(i + 1, True)
            while True:
                if i >= n:
                    raise fout(i, "inline-tabel niet afgesloten")
                if s[i] == "}":
                    return i + 1
                delen, i = sleutel(i)
                i = ws(i)
                if i >= n or s[i] != "=":
                    raise fout(i, "= verwacht in inline-tabel")
                nieuw = pad + tuple(delen)
                paden.append(nieuw)
                i = ws(waarde(i + 1, nieuw), True)
                if i < n and s[i] == ",":
                    i = ws(i + 1, True)
                elif i < n and s[i] == "}":
                    return i + 1
                else:
                    raise fout(i, "komma of } verwacht in inline-tabel")
        start = i
        while i < n and s[i] not in ",]}#\n":
            i += 1
        if not s[start:i].strip():
            raise fout(start, "waarde verwacht")
        return i

    i = 0
    tabel: Tuple[str, ...] = ()
    while True:
        i = ws(i, True)
        if i >= n:
            return paden
        if s.startswith("[[", i) or s[i] == "[":
            dubbel = s.startswith("[[", i)
            delen, j = sleutel(i + (2 if dubbel else 1))
            sluit = "]]" if dubbel else "]"
            if not s.startswith(sluit, j):
                raise fout(j, f"{sluit} verwacht")
            tabel = tuple(delen)
            paden.append(tabel)
            i = j + len(sluit)
        else:
            delen, j = sleutel(i)
            j = ws(j)
            if j >= n or s[j] != "=":
                raise fout(j, "= verwacht")
            pad = tabel + tuple(delen)
            paden.append(pad)
            i = waarde(j + 1, pad)
        i = ws(i)
        if i < n and s[i] != "\n":
            raise fout(i, "onverwachte tekst na de waarde")


def codex_overtredingen(paden: List[Tuple[str, ...]]) -> List[str]:
    """Alles in een Codex-config dat niet op de toelatingsmatrix staat; een profiel telt als hoofdniveau."""
    gevonden: Set[str] = set()
    for pad in paden:
        if pad[0] == "profiles":
            if len(pad) <= 2:
                continue  # `profiles` zelf en de profielnaam: pas de inhoud wordt getoetst
            pad = pad[2:]
        if pad[0] in CODEX_TOEGESTAAN and (len(pad) == 1 or pad[0] in CODEX_TOEGESTAAN_TABELLEN):
            continue
        reden = CODEX_REDEN.get(pad[0]) or CODEX_REDEN.get(pad[-1]) or "niet op de toelatingsmatrix"
        gevonden.add(f"'{'.'.join(pad)}' ({reden})")
    return sorted(gevonden)


def controleer_settings(pad: str, bestand: Path) -> List[str]:
    try:
        data = json.loads(bestand.read_bytes().decode("utf-8-sig"))
    except (ValueError, OSError):
        return [f"{pad}: geen geldige JSON — settings die niet te lezen zijn kunnen niet worden bewaakt"]
    if not isinstance(data, dict):
        return [f"{pad}: geen JSON-object"]
    fouten = []
    for sleutel in sorted(set(data) - SETTINGS_TOEGESTAAN):
        reden = SETTINGS_BEKEND.get(sleutel, "niet op de toelatingsmatrix; voeg alleen toe na beoordeling in de guard")
        fouten.append(f"{pad}: sleutel '{sleutel}' is niet toegestaan ({reden}) — alleen {sorted(SETTINGS_TOEGESTAAN)} is toegelaten")
    rechten = data.get("permissions")
    if isinstance(rechten, dict):
        for sleutel in sorted(set(rechten) - PERMISSIONS_TOEGESTAAN):
            fouten.append(f"{pad}: permissions.{sleutel} is niet toegestaan — alleen {sorted(PERMISSIONS_TOEGESTAAN)}")
    return fouten


def controleer_configuratie(inv: Inventaris) -> List[str]:
    """Settings en config waarmee instructies of laadpaden langs AGENTS.md heen binnenkomen."""
    fouten = []
    for pad in sorted(inv.bestanden()):
        delen = pad.split("/")
        if delen[-2:] == [".claude", "settings.json"]:
            fouten.extend(controleer_settings(pad, inv.root / pad))
        if delen[-2:] == [".codex", "config.toml"]:
            try:
                paden = toml_sleutelpaden(lees_tekst(inv.root / pad))
            except TomlFout as fout:
                fouten.append(f"{pad}: geen leesbare TOML ({fout}) — een config die niet te lezen is kan niet worden bewaakt")
                continue
            for wat in codex_overtredingen(paden):
                fouten.append(f"{pad}: {wat} staat niet op de toelatingsmatrix voor .codex/config.toml (alleen "
                              f"{', '.join(sorted(CODEX_TOEGESTAAN))}) — instructies, bronselectie, budget en extra lagen "
                              "horen niet in een projectconfig (#1580)")
    return fouten


def controleer_skills(root: Path) -> List[str]:
    fouten = list(SYNC["verschillen"](root))
    bron = root / SYNC["BRON"]
    for naam in sorted(VERPLICHTE_SKILLS):
        if not (bron / naam / "SKILL.md").is_file():
            fouten.append(f"{naam}: verplichte skill ontbreekt in de bron ({SYNC['BRON']})")
    for basis in (SYNC["BRON"], SYNC["KOPIE"]):
        for pad in sorted((root / basis).glob("*/SKILL.md")):
            regel = onafgesloten_codeblok(lees_tekst(pad))
            if regel:
                fouten.append(f"{pad.relative_to(root)}:{regel}: onafgesloten codeblok")
    return fouten


# ── omvang ──────────────────────────────────────────────────────────────────────────────────────
def lees_plafonds(root: Path) -> Optional[Tuple[Dict[str, int], List[str]]]:
    """(plafonds, ongeldige regels), of None als het bestand ontbreekt."""
    pad = root / PLAFONDS
    if not pad.is_file():
        return None
    plafonds: Dict[str, int] = {}
    ongeldig: List[str] = []
    for regel in lees_tekst(pad).splitlines():
        regel = regel.strip()
        if not regel or regel.startswith("#"):
            continue
        delen = regel.split(None, 2)
        if len(delen) < 2 or not delen[1].isdigit():
            ongeldig.append(regel)
        else:
            plafonds[delen[0]] = int(delen[1])
    return plafonds, ongeldig


def meet_omvang(root: Path, agents: List[Path]) -> Dict[str, int]:
    """Bytes per AGENTS.md (`bestand:<pad>`) en per keten van root naar die map (`keten:<pad>`).

    Codex telt de bestandsbytes van alle AGENTS.md van de root tot de werkmap; de scheidingstekens
    tellen wij ruim mee. Een keten bestaat alleen als er minstens twee bestanden in zitten.
    """
    grootte = {p.parent: len(p.read_bytes()) for p in agents}
    gemeten: Dict[str, int] = {}
    for pad in agents:
        rel = pad.relative_to(root).as_posix()
        gemeten[f"bestand:{rel}"] = grootte[pad.parent]
        keten = [grootte[d] for d in sorted(grootte, key=lambda x: len(x.parts))
                 if d == pad.parent or d in pad.parent.parents]
        if len(keten) > 1:
            gemeten[f"keten:{rel}"] = sum(keten) + KETEN_SCHEIDING_BYTES * (len(keten) - 1)
    return gemeten


def controleer_omvang(inv: Inventaris) -> Tuple[List[str], List[str]]:
    """(fouten, waarschuwingen). Een plafond is een ratchet: groei faalt, flinke winst moet worden vastgezet."""
    root = inv.root
    agents = inv.vind("AGENTS.md")
    if not agents:
        return [], []
    gelezen = lees_plafonds(root)
    if gelezen is None:
        return [f"{PLAFONDS}: ontbreekt — zonder plafond kan de omvang van de instructies ongemerkt groeien (#1580)"], []
    plafonds, ongeldig = gelezen
    fouten: List[str] = [f"{PLAFONDS}: ongeldige regel '{regel}' — verwacht '<sleutel> <bytes> <toelichting>'" for regel in ongeldig]
    waarschuwingen: List[str] = []
    gemeten = meet_omvang(root, agents)
    for sleutel in sorted(gemeten):
        meting = gemeten[sleutel]
        plafond = plafonds.get(sleutel)
        if plafond is None:
            fouten.append(f"{sleutel}: heeft geen plafond in {PLAFONDS} — voeg een regel toe op de meting ({meting} bytes) in dezelfde PR")
        elif meting > plafond:
            fouten.append(f"{sleutel}: {meting} bytes is hoger dan het plafond {plafond} — maak de instructie compacter of verplaats "
                          "toelichting naar docs/; het plafond verhogen is een eigenaarsbesluit (ratchet, #1580)")
        elif plafond - meting > PLAFOND_RUIMTE:
            fouten.append(f"{sleutel}: {meting} bytes ligt {plafond - meting} onder het plafond {plafond} — verlaag het plafond in "
                          f"{PLAFONDS} naar de meting zodat de winst vastligt")
        if sleutel.startswith("keten:") and meting > CODEX_MAX_BYTES:
            waarschuwingen.append(f"{sleutel}: {meting} bytes; Codex leest standaard maximaal {CODEX_MAX_BYTES} "
                                  "(project_doc_max_bytes) en ziet de rest niet. Zie #1580.")
        if sleutel.startswith("bestand:") and meting > CODEX_MAX_BYTES:
            waarschuwingen.append(f"{sleutel}: {meting} bytes is op zichzelf al meer dan Codex standaard leest ({CODEX_MAX_BYTES}). Zie #1580.")
    for sleutel in sorted(set(plafonds) - set(gemeten)):
        fouten.append(f"{sleutel}: staat in {PLAFONDS} maar bestaat niet meer — verwijder de regel")
    return fouten, waarschuwingen


def controleer_alles(root: Path) -> Tuple[List[str], List[str]]:
    inv = Inventaris(root)
    omvang_fouten, waarschuwingen = controleer_omvang(inv)
    git_fouten = [f"git ls-files faalt ({inv.git_fout}) — getrackte instructiebestanden kunnen niet worden gecontroleerd"] if inv.git_fout else []
    fouten = (
        git_fouten
        + controleer_instructiebestanden(inv)
        + controleer_kanalen(inv)
        + controleer_symlinks(inv)
        + controleer_configuratie(inv)
        + controleer_skills(root)
        + omvang_fouten
    )
    return fouten, waarschuwingen


def controleer(root: Path) -> List[str]:
    return controleer_alles(root)[0]


if __name__ == "__main__":
    fouten, waarschuwingen = controleer_alles(ROOT)
    for waarschuwing in waarschuwingen:
        print(f"::warning::{waarschuwing}")
    for fout in fouten:
        print(f"::error::{fout}")
    if not fouten:
        print("OK — AGENTS.md is de enige bron; stubs bevatten niets; geen bekend tweede laadpad; skills hebben één bron; "
              "codeblokken afgesloten; omvang binnen het plafond.")
    raise SystemExit(bool(fouten))
