#!/usr/bin/env python3
"""check-agent-instructies.mutaties.py (#1580) — bewijst dat de tests van de guard echt bewaken.

Een groene guard bewijst niets zolang niet vaststaat dat hij ook rood kan worden, en een groene
testsuite bewijst niets zolang niet vaststaat dat zij rood wordt als de guard stuk is. Bij de derde
review van #1580 bleek dat het uitschakelen van de leegtecontrole alle 33 tests liet slagen.

Deze runner maakt per guardregel één mutant: een kopie van `check-agent-instructies.py` waarin precies
die regel is uitgeschakeld. Daarna draait hij de testsuite tegen de mutant en eist dat die ROOD wordt.
Overleeft een mutant, dan mist er een test of is de regel overbodig. Eerst draait de ongewijzigde kopie:
die moet groen zijn, anders zegt "rood" niets.

Een mutatie die zijn doeltekst niet meer vindt (de guard is herschreven) faalt óók: een
mutatielijst die stilzwijgend niets meer muteert is hetzelfde als geen lijst.

    python3 scripts/ci/check-agent-instructies.mutaties.py            # alle mutaties, parallel
    python3 scripts/ci/check-agent-instructies.mutaties.py --lijst    # alleen de namen

Draagbaar: Python 3.9+, alleen standaardbibliotheek. Wijzigt geen repositorybestanden; alles gebeurt
in een tijdelijke map.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import List, NamedTuple

CI = Path(__file__).resolve().parent
GUARD = "check-agent-instructies.py"
TESTS = "check-agent-instructies.test.py"
HULP = ("sync-skills.py",)


class Mutatie(NamedTuple):
    naam: str
    oud: str
    nieuw: str
    aantal: int = 1  # hoe vaak `oud` in de guard voorkomt; wijkt dat af, dan is de mutatielijst verouderd


def tekst(*delen: str) -> str:
    return "".join(delen)


MUTATIES: List[Mutatie] = [
    # ── inventarisatie en tweede laadpaden (#1580) ─────────────────────────────────────────────
    Mutatie("kanaal .claude/commands niet verboden", '    (".claude", "commands"): "slash-commands",\n', ""),
    Mutatie("kanaal .claude/agents niet verboden", '    (".claude", "agents"): "subagent-definities",\n', ""),
    Mutatie("kanaal .claude/output-styles niet verboden", '    (".claude", "output-styles"): "output styles",\n', ""),
    Mutatie("kanaal .claude/rules niet verboden", '    (".claude", "rules"): "regelbestanden",\n', ""),
    Mutatie("kanaal .codex/skills niet verboden", '    (".codex", "skills"): "Codex-skills buiten .agents/skills",\n', ""),
    Mutatie("kanaal .codex/prompts niet verboden", '    (".codex", "prompts"): "Codex-prompts",\n', ""),
    Mutatie("kanaal .codex/agents niet verboden", '    (".codex", "agents"): "Codex-agents",\n', ""),
    Mutatie("kanaal .codex/rules niet verboden", '    (".codex", "rules"): "Codex-regels",\n', ""),
    Mutatie("kanaal .codex/commands niet verboden", '    (".codex", "commands"): "Codex-commands",\n', ""),
    Mutatie("kanaalcontrole uitgeschakeld", "        gevonden = deel_van_kanaal(delen)\n        if gevonden:", "        gevonden = None\n        if gevonden:"),
    Mutatie("kanaal alleen in de root gezocht", "    for i in range(len(delen) - 2):  # er moet", "    for i in range(min(1, len(delen) - 2)):  # er moet"),
    Mutatie("geneste skills toegestaan", "SKILLMAPPEN and i > 0:", "SKILLMAPPEN and False:"),
    Mutatie("alleen .claude/skills als geneste skillmap", 'SKILLMAPPEN = {(".claude", "skills"), (".agents", "skills")}', 'SKILLMAPPEN = {(".claude", "skills")}'),
    Mutatie("AGENTS.override.md toegestaan", 'if delen[-1] == "AGENTS.override.md":', "if False:"),
    Mutatie("getrackte CLAUDE.local.md toegestaan", 'if pad.split("/")[-1] == "CLAUDE.local.md":', "if False:"),
    Mutatie("getrackte .mcp.json toegestaan", 'if pad == ".mcp.json" or pad.endswith("/.mcp.json"):', "if False:"),
    Mutatie("getrackte settings.local.json toegestaan", 'if pad.split("/")[-2:] == [".claude", "settings.local.json"] or pad == ".claude/settings.local.json":', "if False:"),
    # ── git-tracking en uitgesloten mappen ──────────────────────────────────────────────────────
    Mutatie("falende git stil overgeslagen", "    git_fouten = [f\"git ls-files faalt", "    git_fouten = [] if True else [f\"git ls-files faalt"),
    Mutatie("git-tracking genegeerd", "            self.getrackt.add(rel)\n", "            pass\n"),
    Mutatie("bestanden() kijkt alleen naar de werkboom", "return (self.aanwezig | self.getrackt) - self.symlinks", "return self.aanwezig - self.symlinks"),
    Mutatie("getrackt-maar-verwijderd crasht niet meer opgevangen", "            if not os.path.lexists(self.root / rel):\n                continue", "            if False:\n                continue"),
    # ── symlinks ────────────────────────────────────────────────────────────────────────────────
    Mutatie("symlinkcontrole uitgeschakeld", "if delen[-1] in INSTRUCTIEBESTANDEN or INSTRUCTIEMAPPEN.intersection(delen):", "if False:"),
    Mutatie("symlinks op instructiemappen toegestaan", "or INSTRUCTIEMAPPEN.intersection(delen):", "or False:"),
    Mutatie("symlinks op instructiebestanden toegestaan", "if delen[-1] in INSTRUCTIEBESTANDEN or", "if False or"),
    Mutatie("symlinks niet herkend bij de doorloop", "                if volledig.is_symlink():\n                    self.symlinks.add(rel)", "                if False:\n                    self.symlinks.add(rel)"),
    Mutatie("getrackte symlinks (mode 120000) niet herkend", 'if kop.split(b" ")[0] == b"120000":', "if False:"),
    Mutatie("map .claude niet als instructiemap gezien", 'INSTRUCTIEMAPPEN = {".claude", ".agents", ".codex"}', 'INSTRUCTIEMAPPEN = {".agents", ".codex"}'),
    Mutatie("map .agents niet als instructiemap gezien", 'INSTRUCTIEMAPPEN = {".claude", ".agents", ".codex"}', 'INSTRUCTIEMAPPEN = {".claude", ".codex"}'),
    Mutatie("map .codex niet als instructiemap gezien", 'INSTRUCTIEMAPPEN = {".claude", ".agents", ".codex"}', 'INSTRUCTIEMAPPEN = {".claude", ".agents"}'),
    Mutatie("CLAUDE.local.md niet als instructiebestand gezien", 'INSTRUCTIEBESTANDEN = {"AGENTS.md", "CLAUDE.md", "CLAUDE.local.md", "AGENTS.override.md"}', 'INSTRUCTIEBESTANDEN = {"AGENTS.md", "CLAUDE.md", "AGENTS.override.md"}'),
    Mutatie("AGENTS.override.md niet als instructiebestand gezien", 'INSTRUCTIEBESTANDEN = {"AGENTS.md", "CLAUDE.md", "CLAUDE.local.md", "AGENTS.override.md"}', 'INSTRUCTIEBESTANDEN = {"AGENTS.md", "CLAUDE.md", "CLAUDE.local.md"}'),
    # ── settings en config ──────────────────────────────────────────────────────────────────────
    Mutatie("settings niet gecontroleerd", 'if delen[-2:] == [".claude", "settings.json"]:', "if False:"),
    Mutatie("ongeldige settings-JSON toegestaan", "            if sleutels is None:\n", "            if False:\n"),
    Mutatie("verboden settings-sleutels genegeerd", "for sleutel in sorted(sleutels & SETTINGS_VERBODEN):", "for sleutel in sorted(sleutels & set()):"),
    Mutatie("codex-config niet gecontroleerd", 'if delen[-2:] == [".codex", "config.toml"]:', "if False:"),
    Mutatie("codex-sleutels genegeerd", "                if re.search(rf\"^\\s*{re.escape(sleutel)}\\s*=\", tekst, re.MULTILINE):", "                if False:"),
    # ── leeg en witruimte ───────────────────────────────────────────────────────────────────────
    Mutatie("leegtecontrole uitgeschakeld", 'if not tekst.replace("\\ufeff", "").strip():', "if False:"),
    Mutatie("witruimte telt als inhoud", 'if not tekst.replace("\\ufeff", "").strip():', 'if not tekst.replace("\\ufeff", ""):'),
    Mutatie("BOM telt als inhoud", 'if not tekst.replace("\\ufeff", "").strip():', "if not tekst.strip():"),
    # ── omvang ──────────────────────────────────────────────────────────────────────────────────
    Mutatie("ontbrekend plafondbestand toegestaan", "    if gelezen is None:\n        return [f", "    if gelezen is None:\n        return [], []\n        return [f"),
    Mutatie("groei boven het plafond toegestaan", "        elif meting > plafond:", "        elif False:"),
    Mutatie("groei met een byte toegestaan", "        elif meting > plafond:", "        elif meting > plafond + 1:"),
    Mutatie("winst hoeft niet vastgezet", "        elif plafond - meting > PLAFOND_RUIMTE:", "        elif False:"),
    Mutatie("ruimte voor winst oneindig", "        elif plafond - meting > PLAFOND_RUIMTE:", "        elif plafond - meting > 10 * PLAFOND_RUIMTE:"),
    Mutatie("bestand zonder plafond toegestaan", "        if plafond is None:", "        if False:"),
    Mutatie("verouderd plafond toegestaan", "    for sleutel in sorted(set(plafonds) - set(gemeten)):", "    for sleutel in sorted(set(plafonds) - set(plafonds)):"),
    Mutatie("ongeldige plafondregel toegestaan", "for regel in ongeldig]", "for regel in []]"),
    Mutatie("scheidingstekens tellen niet mee", "KETEN_SCHEIDING_BYTES * (len(keten) - 1)", "0 * (len(keten) - 1)"),
    Mutatie("scheidingstekens tellen maar één keer", "KETEN_SCHEIDING_BYTES * (len(keten) - 1)", "KETEN_SCHEIDING_BYTES"),
    Mutatie("omvang in tekens in plaats van bytes", "grootte = {p.parent: len(p.read_bytes()) for p in agents}", 'grootte = {p.parent: len(p.read_bytes().decode("utf-8")) for p in agents}'),
    Mutatie("keten negeert bovenliggende mappen", "if d == pad.parent or d in pad.parent.parents]", "if d == pad.parent]"),
    Mutatie("geen ketenmeting", "        if len(keten) > 1:\n            gemeten", "        if False:\n            gemeten"),
    Mutatie("ketenvolgorde onbelangrijk maar diepte genegeerd", "for d in sorted(grootte, key=lambda x: len(x.parts))", "for d in list(grootte)[:2]"),
    Mutatie("waarschuwing voor keten boven Codex-budget weg", 'if sleutel.startswith("keten:") and meting > CODEX_MAX_BYTES:', "if False:"),
    Mutatie("omvangcontrole niet aangeroepen", "    omvang_fouten, waarschuwingen = controleer_omvang(inv)\n", "    omvang_fouten, waarschuwingen = [], []\n"),
    # ── bestaande regels uit #1579 ──────────────────────────────────────────────────────────────
    Mutatie("stub zonder AGENTS.md toegestaan", '    if not agents.is_file():\n        return [f"{rel}: geen AGENTS.md ernaast', '    if False:\n        return [f"{rel}: geen AGENTS.md ernaast'),
    Mutatie("stublengte onbegrensd", "    if len(tekst.encode(\"utf-8\")) > STUB_MAX_BYTES:", "    if False:"),
    Mutatie("stubinhoud onbegrensd", "    if regels not in TOEGESTANE_STUBS:", "    if False:"),
    Mutatie("stubzin in bron toegestaan", "    if STUB_ZIN in bron_regels:", "    if False:"),
    Mutatie("ontbrekende root-AGENTS.md toegestaan", "    if root / \"AGENTS.md\" not in agents:", "    if False:"),
    Mutatie("generatorkop toegestaan", '        if "GEGENEREERD BESTAND" in tekst:', "        if False:"),
    Mutatie("AGENTS.md zonder stub toegestaan", '        if not pad.with_name("CLAUDE.md").is_file():', "        if False:"),
    Mutatie("onafgesloten codeblok in AGENTS.md toegestaan", "        regel = onafgesloten_codeblok(tekst)\n        if regel:\n            fouten.append(f\"{rel}:{regel}", "        regel = None\n        if regel:\n            fouten.append(f\"{rel}:{regel}"),
    Mutatie("stubs niet gecontroleerd", "    for stub in stubs:\n        fouten.extend(controleer_stub(stub, root))", "    for stub in stubs:\n        pass"),
    Mutatie("verplichte skill mag ontbreken", "        if not (bron / naam / \"SKILL.md\").is_file():", "        if False:"),
    Mutatie("onafgesloten codeblok in skill toegestaan", "            regel = onafgesloten_codeblok(lees_tekst(pad))", "            regel = None"),
    Mutatie("skillkopieën niet vergeleken", 'fouten = list(SYNC["verschillen"](root))', "fouten = []"),
]


SETTINGS_SLEUTELS = ("hooks", "outputStyle", "agent", "enabledPlugins", "extraKnownMarketplaces")
CODEX_SLEUTELS = (
    "project_doc_max_bytes", "project_doc_fallback_filenames", "project_root_markers",
    "developer_instructions", "model_instructions_file", "experimental_instructions_file", "compact_prompt",
)
# Elke verboden sleutel afzonderlijk uitschakelen: één vergeten sleutel in de lijst mag niet ongemerkt blijven.
MUTATIES += [Mutatie(f"settings-sleutel {k} toegestaan", f'"{k}"', f'"x-{k}"') for k in SETTINGS_SLEUTELS]
MUTATIES += [Mutatie(f"codex-sleutel {k} toegestaan", f'"{k}"', f'"x-{k}"') for k in CODEX_SLEUTELS]


def draai_suite(map_: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(map_ / TESTS)], capture_output=True, text=True, cwd=str(map_)
    )


def maak_werkmap(basis: Path, naam: str, guardtekst: str) -> Path:
    map_ = basis / naam
    map_.mkdir(parents=True)
    for bestand in (TESTS, *HULP):
        shutil.copy(CI / bestand, map_ / bestand)
    (map_ / GUARD).write_text(guardtekst, encoding="utf-8")
    return map_


def probeer(basis: Path, index: int, mutatie: Mutatie, bron: str) -> str:
    """Geeft een regel uitkomst: 'GEDOOD', 'OVERLEEFD' of 'VEROUDERD'."""
    aantal = bron.count(mutatie.oud)
    if aantal != mutatie.aantal:
        return f"VEROUDERD   {mutatie.naam}: doeltekst komt {aantal}x voor (verwacht {mutatie.aantal}x)"
    if mutatie.oud == mutatie.nieuw:
        return f"VEROUDERD   {mutatie.naam}: mutatie verandert niets"
    map_ = maak_werkmap(basis, f"m{index:03d}", bron.replace(mutatie.oud, mutatie.nieuw))
    resultaat = draai_suite(map_)
    if resultaat.returncode == 0:
        return f"OVERLEEFD   {mutatie.naam}: de testsuite bleef groen — er mist een test voor deze regel"
    return f"GEDOOD      {mutatie.naam}"


def main() -> int:
    if "--lijst" in sys.argv:
        for mutatie in MUTATIES:
            print(mutatie.naam)
        return 0
    namen = [m.naam for m in MUTATIES]
    dubbel = {n for n in namen if namen.count(n) > 1}
    if dubbel:
        print(f"::error::dubbele mutatienamen: {sorted(dubbel)}")
        return 1
    bron = (CI / GUARD).read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory() as tmp:
        basis = Path(tmp)
        basislijn = draai_suite(maak_werkmap(basis, "basis", bron))
        if basislijn.returncode != 0:
            print("::error::de ongewijzigde guard faalt zijn eigen tests — mutaties zeggen dan niets")
            print(basislijn.stdout + basislijn.stderr)
            return 1
        with ThreadPoolExecutor(max_workers=4) as pool:
            uitkomsten = list(pool.map(lambda ix: probeer(basis, ix[0], ix[1], bron), enumerate(MUTATIES)))
    for regel in uitkomsten:
        print(regel)
    slecht = [u for u in uitkomsten if not u.startswith("GEDOOD")]
    gedood = len(uitkomsten) - len(slecht)
    print(f"\n{gedood} van {len(MUTATIES)} mutaties gedood.")
    for regel in slecht:
        print(f"::error::{regel.strip()}")
    return 1 if slecht else 0


if __name__ == "__main__":
    sys.exit(main())
