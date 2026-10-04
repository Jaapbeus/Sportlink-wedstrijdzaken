#!/usr/bin/env python3
"""Read-only guard voor gelijke agent-skills en afgesloten Markdown-codeblokken."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


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


def controleer(root: Path) -> list[str]:
    fouten = []
    claude = root / ".claude/skills"
    codex = root / ".agents/skills"
    namen = {"autonoom", "release", "sluitsessie", "startdebug"} | {p.parent.name for basis in (claude, codex) for p in basis.glob("*/SKILL.md")}
    for naam in sorted(namen):
        links, rechts = claude / naam / "SKILL.md", codex / naam / "SKILL.md"
        # Some tools are intentionally installed for only one agent.
        if not links.exists() or not rechts.exists():
            if naam in {"autonoom", "release", "sluitsessie", "startdebug"}:
                fouten.append(f"{naam}: verplichte skilltweeling ontbreekt")
            continue
        if links.read_text().replace("CLAUDE.md", "AGENTS.md") != rechts.read_text().replace("CLAUDE.md", "AGENTS.md"):
            fouten.append(f"{naam}: skilltweelingen verschillen inhoudelijk")
    for basis in (claude, codex):
        for pad in sorted(basis.glob("*/SKILL.md")):
            regel = onafgesloten_codeblok(pad.read_text())
            if regel:
                fouten.append(f"{pad.relative_to(root)}:{regel}: onafgesloten codeblok")
    return fouten


if __name__ == "__main__":
    fouten = controleer(ROOT)
    for fout in fouten:
        print(f"::error::{fout}")
    if not fouten:
        print("OK — skilltweelingen gelijk; Markdown-codeblokken afgesloten.")
    raise SystemExit(bool(fouten))
