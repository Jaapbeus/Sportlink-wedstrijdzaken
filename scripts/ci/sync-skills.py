#!/usr/bin/env python3
"""sync-skills.py (#1579) — skills hebben één bron; de andere locatie is een kopie.

WAAROM DIT SCRIPT BESTAAT
-------------------------
Codex leest skills uit `.agents/skills/`, Claude Code uitsluitend uit `.claude/skills/`. Een
gedeelde map zou symlinks in git vragen, en die breken op Windows zonder Developer Mode of
`core.symlinks` — dit repo ondersteunt Windows én macOS. Daarom: `.agents/skills/<naam>/` is de
bron en `.claude/skills/<naam>/` een byte-identieke kopie. Voordien waren het twee met de hand
bijgehouden tweelingen die alleen mochten verschillen in de naam van het instructiebestand.

Een skill die uitsluitend voor Claude Code bestaat (bijvoorbeeld omdat hij Claude-specifieke
MCP-tools aanroept) staat alleen in `.claude/skills/` en in `scripts/ci/skills-alleen-claude.txt`.
Hij heeft dan precies één bron en geen kopie.

GEBRUIK
-------
    python3 scripts/ci/sync-skills.py            # controleer (exit 1 bij verschil)
    python3 scripts/ci/sync-skills.py --schrijf  # schrijf de kopieën vanuit de bron

`--schrijf` verwijdert nooit een skill die geen bron heeft: een onbekende map in `.claude/skills/`
is óf een vergeten bron óf een bewerkte kopie, en dat beslist een mens.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

WORTEL = Path(__file__).resolve().parents[2]
BRON = ".agents/skills"
KOPIE = ".claude/skills"
ALLEEN_CLAUDE = "scripts/ci/skills-alleen-claude.txt"
NEGEER = {".DS_Store"}


def bestanden(map_: Path) -> dict[str, bytes]:
    return {
        p.relative_to(map_).as_posix(): p.read_bytes()
        for p in sorted(map_.rglob("*"))
        if p.is_file() and p.name not in NEGEER
    }


def skillnamen(basis: Path) -> set[str]:
    return {p.name for p in basis.iterdir() if p.is_dir()} if basis.is_dir() else set()


def alleen_claude(root: Path) -> set[str]:
    pad = root / ALLEEN_CLAUDE
    if not pad.is_file():
        return set()
    regels = (r.split("#", 1)[0].strip() for r in pad.read_text(encoding="utf-8").splitlines())
    return {r for r in regels if r}


def verschillen(root: Path) -> list[str]:
    fouten: list[str] = []
    bron, kopie = root / BRON, root / KOPIE
    uitzonderingen = alleen_claude(root)
    bronnen, kopieen = skillnamen(bron), skillnamen(kopie)

    for naam in sorted(bronnen):
        if naam in uitzonderingen:
            fouten.append(f"{naam}: staat in {ALLEEN_CLAUDE} maar heeft ook een bron in {BRON} (twee bronnen)")
        if naam not in kopieen:
            fouten.append(f"{naam}: kopie in {KOPIE} ontbreekt — draai sync-skills.py --schrijf")
            continue
        links, rechts = bestanden(bron / naam), bestanden(kopie / naam)
        for pad in sorted(links.keys() | rechts.keys()):
            if pad not in rechts:
                fouten.append(f"{naam}/{pad}: ontbreekt in de kopie ({KOPIE})")
            elif pad not in links:
                fouten.append(f"{naam}/{pad}: staat alleen in de kopie ({KOPIE}) — wijzig de bron ({BRON})")
            elif links[pad] != rechts[pad]:
                fouten.append(f"{naam}/{pad}: kopie wijkt af van de bron — wijzig de bron ({BRON}) en draai sync-skills.py --schrijf")

    for naam in sorted(kopieen - bronnen - uitzonderingen):
        fouten.append(f"{naam}: staat in {KOPIE} zonder bron in {BRON} en niet in {ALLEEN_CLAUDE}")
    for naam in sorted(uitzonderingen - kopieen):
        fouten.append(f"{naam}: staat in {ALLEEN_CLAUDE} maar bestaat niet in {KOPIE}")
    return fouten


def schrijf(root: Path) -> list[str]:
    geschreven = []
    for naam in sorted(skillnamen(root / BRON)):
        doel = root / KOPIE / naam
        if doel.exists():
            shutil.rmtree(doel)
        shutil.copytree(root / BRON / naam, doel, ignore=shutil.ignore_patterns(*NEGEER))
        geschreven.append(naam)
    return geschreven


def main() -> int:
    if "--schrijf" in sys.argv:
        namen = schrijf(WORTEL)
        print(f"{len(namen)} skillkopie(ën) geschreven naar {KOPIE}: {', '.join(namen)}.")

    fouten = verschillen(WORTEL)
    for fout in fouten:
        print(f"::error::{fout}")
    if not fouten:
        print(f"OK — elke skill heeft één bron; kopieën in {KOPIE} zijn identiek.")
    return 1 if fouten else 0


if __name__ == "__main__":
    sys.exit(main())
