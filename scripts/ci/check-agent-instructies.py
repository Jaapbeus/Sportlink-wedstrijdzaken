#!/usr/bin/env python3
"""check-agent-instructies.py (#1579) — read-only guard: één bron voor agentinstructies en skills.

WAAROM DEZE GUARD BESTAAT
-------------------------
`AGENTS.md` is de enige bron van de agentinstructies, voor Codex én Claude Code. Claude Code leest
een `AGENTS.md` niet zelf zodra er een `CLAUDE.md` in of boven de werkmap staat — ook niet in een
submap — dus naast elke `AGENTS.md` staat een `CLAUDE.md`-stub die alleen `@AGENTS.md` importeert.
Staat er inhoud in een stub, dan bestaat die instructie op twee plekken en loopt ze vanzelf uiteen.

De guard dwingt af:
  1. naast elke `AGENTS.md` staat een `CLAUDE.md`, en andersom;
  2. een `CLAUDE.md` bestaat uit exact de import `@AGENTS.md`, eventueel gevolgd door exact de
     vaste zin STUB_ZIN (lege regels mogen), blijft onder STUB_MAX_BYTES, en de zin staat niet ook
     in de `AGENTS.md` ernaast. Elke andere regel — ook een verwijzing, commentaar of tweede
     import — faalt: niets mag een tweede instructiebron worden;
  3. `AGENTS.md` is geen gegenereerd bestand meer (de oude generatorkop is weg);
  4. skills hebben één bron: `.agents/skills/` met een identieke kopie in `.claude/skills/`, of staan
     in `scripts/ci/skills-alleen-claude.txt` (zie `scripts/ci/sync-skills.py`);
  5. Markdown-codeblokken in skills en `AGENTS.md` zijn afgesloten.

Wat dit NIET bewijst: dat de inhoud juist is, of dat de agent hem leest. Dat Claude Code de import
laadt is handmatig vastgesteld bij #1579 en staat in dat issue.
"""

from __future__ import annotations

import os
import re
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SYNC = runpy.run_path(str(Path(__file__).with_name("sync-skills.py")))

STUB_MAX_BYTES = 300
STUB_IMPORT = "@AGENTS.md"
STUB_ZIN = "AGENTS.md is de enige bron van de agentinstructies; zet hier geen inhoud bij."
TOEGESTANE_STUBS = ([STUB_IMPORT], [STUB_IMPORT, STUB_ZIN])
VERPLICHTE_SKILLS = {"autonoom", "release", "sluitsessie", "startdebug"}
CODEX_MAX_BYTES = 32 * 1024  # project_doc_max_bytes, standaard
NEGEER_MAPPEN = {".git", "node_modules", "bin", "obj", "packages", ".venv", "artifacts"}


def onafgesloten_codeblok(tekst: str) -> int | None:
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


def vind(root: Path, bestandsnaam: str) -> list[Path]:
    """Alle bestanden met deze naam, zonder worktrees van andere sessies en buildmappen."""
    gevonden = []
    for map_, submappen, namen in os.walk(root):
        pad = Path(map_)
        submappen[:] = [
            d for d in submappen
            if d not in NEGEER_MAPPEN and not (d == "worktrees" and pad.name in {".claude", ".codex"})
        ]
        if bestandsnaam in namen:
            gevonden.append(pad / bestandsnaam)
    return sorted(gevonden)


def controleer_stub(stub: Path, root: Path) -> list[str]:
    rel = stub.relative_to(root)
    agents = stub.with_name("AGENTS.md")
    fouten = []
    if not agents.is_file():
        return [f"{rel}: geen AGENTS.md ernaast — een stub zonder bron laadt niets"]

    tekst = stub.read_text(encoding="utf-8")
    regels = [r.strip() for r in tekst.splitlines() if r.strip()]
    if len(tekst.encode("utf-8")) > STUB_MAX_BYTES:
        fouten.append(f"{rel}: stub is langer dan {STUB_MAX_BYTES} bytes — instructies horen in AGENTS.md")
    # Geen vormcontrole maar een vaste toegestane inhoud: elke andere regel — ook een zin die
    # 'AGENTS.md' noemt, commentaar of een tweede import — is een mogelijke tweede instructiebron.
    if regels not in TOEGESTANE_STUBS:
        fouten.append(f"{rel}: een stub bestaat uit exact '{STUB_IMPORT}', eventueel gevolgd door exact de zin "
                      f"'{STUB_ZIN}' — alles anders hoort in AGENTS.md")
    bron_regels = {r.strip() for r in agents.read_text(encoding="utf-8").splitlines() if r.strip()}
    if STUB_ZIN in bron_regels:
        fouten.append(f"{rel}: de stubzin staat ook in AGENTS.md — één waarheid")
    return fouten


def controleer_instructiebestanden(root: Path) -> list[str]:
    fouten = []
    agents = vind(root, "AGENTS.md")
    stubs = vind(root, "CLAUDE.md")
    if root / "AGENTS.md" not in agents:
        fouten.append("AGENTS.md: ontbreekt in de repositoryroot — dit is de enige bron")
    for pad in agents:
        rel = pad.relative_to(root)
        tekst = pad.read_text(encoding="utf-8")
        if not tekst.strip():
            fouten.append(f"{rel}: leeg")
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


def controleer_skills(root: Path) -> list[str]:
    fouten = list(SYNC["verschillen"](root))
    bron = root / SYNC["BRON"]
    for naam in sorted(VERPLICHTE_SKILLS):
        if not (bron / naam / "SKILL.md").is_file():
            fouten.append(f"{naam}: verplichte skill ontbreekt in de bron ({SYNC['BRON']})")
    for basis in (SYNC["BRON"], SYNC["KOPIE"]):
        for pad in sorted((root / basis).glob("*/SKILL.md")):
            regel = onafgesloten_codeblok(pad.read_text(encoding="utf-8"))
            if regel:
                fouten.append(f"{pad.relative_to(root)}:{regel}: onafgesloten codeblok")
    return fouten


def controleer(root: Path) -> list[str]:
    return controleer_instructiebestanden(root) + controleer_skills(root)


if __name__ == "__main__":
    fouten = controleer(ROOT)
    for fout in fouten:
        print(f"::error::{fout}")
    grootte = (ROOT / "AGENTS.md").stat().st_size if (ROOT / "AGENTS.md").is_file() else 0
    if grootte > CODEX_MAX_BYTES:
        # Geen fout: een repositorybestand kan de gebruikersinstelling van Codex niet verhogen.
        print(f"::notice::AGENTS.md is {grootte} bytes; Codex leest standaard maximaal {CODEX_MAX_BYTES} "
              "(project_doc_max_bytes) en ziet de rest niet zonder verhoogde instelling. Zie #1580.")
    if not fouten:
        print("OK — AGENTS.md is de enige bron; stubs bevatten niets; skills hebben één bron; codeblokken afgesloten.")
    raise SystemExit(bool(fouten))
