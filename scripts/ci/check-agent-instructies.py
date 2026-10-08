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
  6. er bestaat geen tweede laadpad voor instructies (zie "Toegestaan en verboden" hieronder);
  7. de omvang past binnen het plafond per bestand en per keten (`agent-instructies-plafonds.txt`).

TOEGESTAAN EN VERBODEN (de ene lijst; docs/ARCHITECTUUR-CODEKWALITEIT.md verwijst hierheen)
-----------------------------------------------------------------------------------------
Toegestaan: `AGENTS.md` (root en submappen) met een stub-`CLAUDE.md` ernaast; skills uit
`.agents/skills/` met een identieke kopie in `.claude/skills/`, alleen in de repositoryroot;
Claude-only skills uit `scripts/ci/skills-alleen-claude.txt`; `.claude/settings.json` zonder de
sleutels uit SETTINGS_VERBODEN; een `.codex/config.toml` zonder de sleutels uit CODEX_VERBODEN.

Verboden, in de repositoryroot én in elke submap, aanwezig óf door git getrackt (ook in een map die
de doorloop overslaat, zoals bin/obj/node_modules — `git ls-files` is leidend):
  * `.claude/rules/`, `.claude/commands/`, `.claude/agents/`, `.claude/output-styles/` — Claude Code
    laadt ze als prompt/instructie zodra ze ontdekt of geactiveerd worden, Codex niet;
  * een `.claude/skills/` of `.agents/skills/` in een submap (geneste skills);
  * `.codex/skills|prompts|agents|rules|commands/`;
  * `AGENTS.override.md` — vervangt de AGENTS.md in dezelfde map en maakt het één-bronbeleid stuk;
  * een symlink op elk van deze plekken, op `AGENTS.md`, `CLAUDE.md` of in `.claude/`, `.agents/`,
    `.codex/` — we volgen geen symlinks, dus we weigeren ze (geen cyclus- of padgrenzen nodig).
Verboden uitsluitend wanneer door git getrackt (lokaal is het een persoonlijk, genegeerd bestand):
  `CLAUDE.local.md` (alleen `.gitignore` houdt het eruit; `git add -f` ging ongezien),
  `.claude/settings.local.json` en `.mcp.json` (bevat de projectverwijzing).
Een legitieme toekomstige subagent, command of config vraagt een eigenaarsbesluit én een wijziging
van déze guard — dat is de uitzondering, als diff die iemand goedkeurt.

Wat dit NIET bewijst: dat de inhoud juist is, dat de agent hem leest, of dat een gebruikersinstelling
op een andere machine het budget verhoogt of verlaagt. Dat Claude Code de import laadt is handmatig
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
# Sleutels waarmee een settings-/configbestand zelf instructies of extra laadpaden toevoegt.
SETTINGS_VERBODEN = {"hooks", "outputStyle", "agent", "enabledPlugins", "extraKnownMarketplaces"}
CODEX_VERBODEN = {
    "project_doc_max_bytes", "project_doc_fallback_filenames", "project_root_markers",
    "developer_instructions", "model_instructions_file", "experimental_instructions_file",
    "compact_prompt",
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
        if not (self.root / ".git").exists():
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
    for pad in sorted(inv.getrackt - inv.symlinks):
        if pad.split("/")[-1] == "CLAUDE.local.md":
            fouten.append(f"{pad}: CLAUDE.local.md is persoonlijk en hoort niet in git (ook niet met git add -f)")
        if pad == ".mcp.json" or pad.endswith("/.mcp.json"):
            fouten.append(f"{pad}: .mcp.json bevat de projectverwijzing en hoort niet in git — gebruik .mcp.json.template")
        if pad.split("/")[-2:] == [".claude", "settings.local.json"] or pad == ".claude/settings.local.json":
            fouten.append(f"{pad}: settings.local.json is persoonlijk en hoort niet in git")
    return fouten


def controleer_symlinks(inv: Inventaris) -> List[str]:
    fouten = []
    for pad in sorted(inv.symlinks):
        delen = pad.split("/")
        if delen[-1] in INSTRUCTIEBESTANDEN or INSTRUCTIEMAPPEN.intersection(delen):
            fouten.append(f"{pad}: symlink op een instructiepad — symlinks worden niet gevolgd en breken op Windows; kopieer of verwijs met een import")
    return fouten


def lees_json_sleutels(pad: Path) -> Optional[Set[str]]:
    try:
        data = json.loads(pad.read_bytes().decode("utf-8-sig"))
    except (ValueError, OSError):
        return None
    return set(data) if isinstance(data, dict) else set()


def controleer_configuratie(inv: Inventaris) -> List[str]:
    """Settings en config waarmee instructies of laadpaden langs AGENTS.md heen binnenkomen."""
    fouten = []
    for pad in sorted(inv.bestanden()):
        delen = pad.split("/")
        if delen[-2:] == [".claude", "settings.json"]:
            sleutels = lees_json_sleutels(inv.root / pad)
            if sleutels is None:
                fouten.append(f"{pad}: geen geldige JSON — settings die niet te lezen zijn kunnen niet worden bewaakt")
                continue
            for sleutel in sorted(sleutels & SETTINGS_VERBODEN):
                fouten.append(f"{pad}: sleutel '{sleutel}' voegt instructies of laadpaden toe naast AGENTS.md")
        if delen[-2:] == [".codex", "config.toml"]:
            tekst = lees_tekst(inv.root / pad)
            for sleutel in sorted(CODEX_VERBODEN):
                if re.search(rf"^\s*{re.escape(sleutel)}\s*=", tekst, re.MULTILINE):
                    fouten.append(f"{pad}: sleutel '{sleutel}' wijzigt de bronselectie of het budget van de projectinstructies — "
                                  "het budget moet binnen de standaardinstelling van Codex passen (#1580)")
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
        print("OK — AGENTS.md is de enige bron; stubs bevatten niets; geen tweede laadpad; skills hebben één bron; "
              "codeblokken afgesloten; omvang binnen het plafond.")
    raise SystemExit(bool(fouten))
