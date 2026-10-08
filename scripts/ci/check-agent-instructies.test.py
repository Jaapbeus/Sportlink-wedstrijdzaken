#!/usr/bin/env python3
"""Positieve/negatieve checks in tijdelijke fixtures; wijzigt geen repositorybestanden.

Elke negatieve test maakt van één afspraak uit check-agent-instructies.py tijdelijk een echte
overtreding en eist dat de guard rood wordt — een guard die niets ziet, is ook groen.
"""

import os
import runpy
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

guard = runpy.run_path(str(Path(__file__).with_name("check-agent-instructies.py")))

STUB = "@AGENTS.md\n\nAGENTS.md is de enige bron van de agentinstructies; zet hier geen inhoud bij.\n"
SKILLS = ("autonoom", "release", "sluitsessie", "startdebug")
# Bewust uitgeschreven in plaats van uit de guard gelezen: een testlijst die uit de guard komt
# controleert alleen dat de guard zichzelf gelooft (de mutatierunner ving dit).
UITGESLOTEN_MAPPEN = ("bin", "obj", "packages", "node_modules", ".venv", "artifacts")
SETTINGS_SLEUTELS = ("hooks", "outputStyle", "agent", "enabledPlugins", "extraKnownMarketplaces")
CODEX_SLEUTELS = (
    "project_doc_max_bytes", "project_doc_fallback_filenames", "project_root_markers",
    "developer_instructions", "model_instructions_file", "experimental_instructions_file", "compact_prompt",
)


class AgentInstructiesTests(unittest.TestCase):
    def schrijf(self, root: Path, pad: str, tekst: str) -> Path:
        doel = root / pad
        doel.parent.mkdir(parents=True, exist_ok=True)
        doel.write_text(tekst, encoding="utf-8")
        return doel

    def fixture(self) -> Path:
        """Geldige indeling: root + submap met stub, vier skills met kopie, één Claude-only skill."""
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        self.schrijf(root, "AGENTS.md", "# AGENTS.md\n\nRegel A.\n")
        self.schrijf(root, "CLAUDE.md", STUB)
        self.schrijf(root, "sub/AGENTS.md", "# AGENTS.md\n\nSubregel.\n")
        self.schrijf(root, "sub/CLAUDE.md", STUB)
        for naam in SKILLS:
            for basis in (".agents", ".claude"):
                self.schrijf(root, f"{basis}/skills/{naam}/SKILL.md", f"Skill {naam}.\n```bash\nls\n```\n")
        self.schrijf(root, ".claude/skills/zelftest/SKILL.md", "Alleen Claude.\n")
        self.schrijf(root, "scripts/ci/skills-alleen-claude.txt", "# uitzonderingen\nzelftest  # Claude-tools\n")
        self.zet_plafonds(root)
        return root

    def zet_plafonds(self, root: Path) -> None:
        """Plafonds exact op de meting: de gezonde stand."""
        gemeten = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        regels = "".join(f"{sleutel} {waarde} test\n" for sleutel, waarde in sorted(gemeten.items()))
        self.schrijf(root, "scripts/ci/agent-instructies-plafonds.txt", "# fixture\n" + regels)

    def plafond(self, root: Path, sleutel: str, waarde) -> None:
        """Zet één plafond (of verwijder het met None) in het fixturebestand."""
        pad = root / "scripts/ci/agent-instructies-plafonds.txt"
        regels = [r for r in pad.read_text(encoding="utf-8").splitlines() if not r.startswith(sleutel + " ")]
        if waarde is not None:
            regels.append(f"{sleutel} {waarde} test")
        pad.write_text("\n".join(regels) + "\n", encoding="utf-8")

    def git_repo(self, root: Path) -> None:
        if not shutil.which("git"):
            self.skipTest("git ontbreekt")
        subprocess.run(["git", "init", "-q", str(root)], check=True, capture_output=True)

    def git_voeg_toe(self, root: Path, *paden: str) -> None:
        subprocess.run(["git", "-C", str(root), "add", "-f", "--", *paden], check=True, capture_output=True)

    def symlink(self, root: Path, pad: str, doel: str) -> None:
        link = root / pad
        link.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.symlink(doel, link)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks niet beschikbaar op dit platform")

    def waarschuwingen(self, root: Path) -> str:
        return "\n".join(guard["controleer_alles"](root)[1])

    def fouten(self, root: Path) -> str:
        return "\n".join(guard["controleer"](root))

    # ── positief ────────────────────────────────────────────────────────────────────────────
    def test_geldige_indeling_is_groen(self):
        self.assertEqual(guard["controleer"](self.fixture()), [])

    def test_worktrees_van_andere_sessies_worden_genegeerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/worktrees/x/CLAUDE.md", "# Een hele handleiding\n" * 50)
        self.schrijf(root, ".codex/worktrees/y/AGENTS.md", "inhoud\n")
        self.assertEqual(guard["controleer"](root), [])

    # ── stubs ───────────────────────────────────────────────────────────────────────────────
    def test_instructie_in_stub_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", STUB + "\n## Eigen regels\n- Nooit mergen.\n")
        self.assertIn("CLAUDE.md", self.fouten(root))

    def test_stub_boven_maximale_lengte_wordt_geweigerd(self):
        root = self.fixture()
        zin = "AGENTS.md " + "is de bron " * 40
        self.schrijf(root, "CLAUDE.md", f"@AGENTS.md\n\n{zin}\n")
        self.assertIn("langer dan", self.fouten(root))

    def test_stub_die_zijn_zin_uit_agents_md_herhaalt_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", f"# AGENTS.md\n\n{guard['STUB_ZIN']}\n")
        self.assertIn("staat ook in AGENTS.md", self.fouten(root))

    def test_stub_met_alleen_de_import_is_groen(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_stub_met_crlf_en_lege_regels_is_groen(self):
        root = self.fixture()
        (root / "CLAUDE.md").write_bytes(b"@AGENTS.md\r\n\r\n\r\n" + guard["STUB_ZIN"].encode() + b"\r\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_stub_zonder_import_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "sub/CLAUDE.md", "Zie AGENTS.md voor alles.\n")
        self.assertIn("sub/CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    # Regressie review ronde 1 (Codex, P2): een vormcontrole liet extra inhoud door op de
    # verwijzingsregel. Elke afwijking van de vaste inhoud moet rood zijn, ook als de tekst
    # 'AGENTS.md' noemt, kort is en niet met een opmaakteken begint.
    def test_instructie_die_agents_md_noemt_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\n\nAGENTS.md: voer altijd eerst de tests uit.\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_verwijzing_met_extra_instructie_in_dezelfde_regel_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\n\n" + guard["STUB_ZIN"] + " Merge nooit zonder review.\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_html_commentaar_in_stub_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "sub/CLAUDE.md", "@AGENTS.md\n<!-- Regel: nooit mergen zonder AGENTS.md -->\n")
        self.assertIn("sub/CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_tweede_importverwijzing_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\n@docs/ARCHITECTUUR.md\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_import_binnen_een_verwijzende_zin_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\nAGENTS.md zie ook @docs/INDEX.md\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_importregel_met_extra_tekst_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md en lees daarna ook @docs/INDEX.md\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_claude_regelbestand_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/rules/testen.md", "Voer altijd alle tests uit.\n")
        self.assertIn(".claude/rules/testen.md: regelbestanden zijn een tweede instructiebron", self.fouten(root))

    # Regressie review ronde 2 (Codex, P2): ook een regelmap in een submap is een tweede bron.
    def test_geneste_claude_regelmap_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "sub/.claude/rules/api.md", "Gebruik altijd async.\n")
        self.assertIn("sub/.claude/rules/api.md: regelbestanden zijn een tweede instructiebron", self.fouten(root))

    def test_regelmap_in_andermans_worktree_wordt_genegeerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/worktrees/x/.claude/rules/api.md", "Van een andere sessie.\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_claude_md_in_dotclaude_zonder_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/CLAUDE.md", "Eigen regels.\n")
        self.assertIn(".claude/CLAUDE.md: geen AGENTS.md ernaast", self.fouten(root))

    def test_kop_in_stub_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "CLAUDE.md", "@AGENTS.md\n# AGENTS.md\n")
        self.assertIn("CLAUDE.md: een stub bestaat uit exact", self.fouten(root))

    def test_stub_zonder_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "docs/CLAUDE.md", STUB)
        self.assertIn("docs/CLAUDE.md: geen AGENTS.md ernaast", self.fouten(root))

    # ── AGENTS.md ───────────────────────────────────────────────────────────────────────────
    def test_agents_md_zonder_stub_wordt_geweigerd(self):
        root = self.fixture()
        (root / "sub/CLAUDE.md").unlink()
        self.assertIn("sub/AGENTS.md: geen CLAUDE.md-stub", self.fouten(root))

    def test_ontbrekende_root_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        (root / "AGENTS.md").unlink()
        self.assertIn("ontbreekt in de repositoryroot", self.fouten(root))

    def test_oude_generatorkop_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", "<!-- GEGENEREERD BESTAND — NIET MET DE HAND BEWERKEN. -->\nRegel\n")
        self.assertIn("generatorkop", self.fouten(root))

    def test_onafgesloten_codeblok_in_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", "# AGENTS.md\n```bash\nls\n### Vervolg\n")
        self.assertIn("AGENTS.md:2: onafgesloten codeblok", self.fouten(root))

    # ── skills ──────────────────────────────────────────────────────────────────────────────
    def test_afwijkende_skillkopie_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/skills/release/SKILL.md", "Eigenmachtig bewerkt.\n")
        self.assertIn("release/SKILL.md: kopie wijkt af", self.fouten(root))

    def test_ontbrekende_skillkopie_wordt_geweigerd(self):
        root = self.fixture()
        (root / ".claude/skills/startdebug/SKILL.md").unlink()
        self.assertIn("startdebug/SKILL.md: ontbreekt in de kopie", self.fouten(root))

    def test_extra_bestand_in_kopie_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/skills/autonoom/extra.md", "Alleen hier.\n")
        self.assertIn("autonoom/extra.md: staat alleen in de kopie", self.fouten(root))

    def test_kopie_zonder_bron_en_zonder_uitzondering_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/skills/nieuw/SKILL.md", "Wees.\n")
        self.assertIn("nieuw: staat in .claude/skills zonder bron", self.fouten(root))

    def test_skill_met_twee_bronnen_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".agents/skills/zelftest/SKILL.md", "Ook hier.\n")
        self.assertIn("twee bronnen", self.fouten(root))

    def test_verouderde_uitzondering_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "scripts/ci/skills-alleen-claude.txt", "zelftest\nbestaat-niet\n")
        self.assertIn("bestaat-niet: staat in scripts/ci/skills-alleen-claude.txt maar bestaat niet", self.fouten(root))

    def test_verplichte_skill_ontbreekt_in_bron(self):
        root = self.fixture()
        for basis in (".agents", ".claude"):
            (root / basis / "skills/sluitsessie/SKILL.md").unlink()
        self.assertIn("sluitsessie: verplichte skill ontbreekt", self.fouten(root))

    def test_losse_fence_in_skill_wordt_geweigerd_ook_als_kopie_gelijk_is(self):
        root = self.fixture()
        for basis in (".agents", ".claude"):
            self.schrijf(root, f"{basis}/skills/autonoom/SKILL.md", "Tekst\n```\n### Vervolg\n")
        self.assertEqual(len(guard["controleer"](root)), 2)

    def test_fencelengte_en_type(self):
        check = guard["onafgesloten_codeblok"]
        self.assertIsNone(check("````text\n```\n~~~~\n````\n"))
        self.assertEqual(check("````text\n```\n"), 1)
        self.assertIsNone(check("~~~text\n```\n~~~\n"))
        self.assertIsNone(check("Een inline ``` is geen codeblok.\n"))

    # ── leeg / witruimte (#1580: uitschakelen van de leegtecontrole liet alle tests slagen) ───────
    def test_lege_root_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", "")
        self.assertIn("AGENTS.md: leeg", self.fouten(root))

    def test_agents_md_met_alleen_witruimte_wordt_geweigerd(self):
        for inhoud in ("   \n\t\n  \n", "\n", "\r\n\r\n", "﻿\n  ", "  \n"):
            with self.subTest(inhoud=repr(inhoud)):
                root = self.fixture()
                self.schrijf(root, "AGENTS.md", inhoud)
                self.assertIn("AGENTS.md: leeg", self.fouten(root))

    def test_lege_submap_agents_md_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "sub/AGENTS.md", " \n")
        self.assertIn("sub/AGENTS.md: leeg", self.fouten(root))

    # ── tweede laadpaden: Claude-commands, -agents en -output-styles ─────────────────────────────
    KANAALPADEN = {
        ".claude/commands/review.md": "slash-commands",
        ".claude/commands/team/review.md": "slash-commands",
        ".claude/agents/reviewer.md": "subagent-definities",
        ".claude/output-styles/kort.md": "output styles",
        "sub/.claude/commands/x.md": "slash-commands",
        "sub/.claude/agents/x.md": "subagent-definities",
        "sub/.claude/output-styles/x.md": "output styles",
        "sub/.claude/rules/x.md": "regelbestanden",
        ".codex/prompts/x.md": "Codex-prompts",
        ".codex/agents/x.toml": "Codex-agents",
        ".codex/commands/x.md": "Codex-commands",
        ".codex/skills/x/SKILL.md": "Codex-skills buiten .agents/skills",
        "sub/.codex/rules/x.rules": "Codex-regels",
    }

    def test_elk_verboden_laadpad_wordt_geweigerd(self):
        for pad, omschrijving in self.KANAALPADEN.items():
            with self.subTest(pad=pad):
                root = self.fixture()
                self.schrijf(root, pad, "Een instructie.\n")
                self.assertIn(f"{pad}: {omschrijving} zijn een tweede instructiebron", self.fouten(root))

    def test_laadpad_in_andermans_worktree_wordt_genegeerd(self):
        root = self.fixture()
        for pad in (".claude/worktrees/x/.claude/commands/a.md", ".codex/worktrees/y/.claude/agents/a.md"):
            self.schrijf(root, pad, "Van een andere sessie.\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_geneste_skillmap_wordt_geweigerd(self):
        for pad in ("sub/.claude/skills/x/SKILL.md", "sub/.agents/skills/x/SKILL.md", "a/b/.claude/skills/x/SKILL.md"):
            with self.subTest(pad=pad):
                root = self.fixture()
                self.schrijf(root, pad, "Alleen hier.\n")
                self.assertIn(f"{pad}: geneste skillmap", self.fouten(root))

    def test_rootskills_blijven_toegestaan(self):
        self.assertEqual(guard["controleer"](self.fixture()), [])

    # ── override en lokale bestanden ─────────────────────────────────────────────────────────────
    def test_agents_override_wordt_geweigerd_op_elk_niveau(self):
        for pad in ("AGENTS.override.md", "sub/AGENTS.override.md", "diep/er/AGENTS.override.md"):
            with self.subTest(pad=pad):
                root = self.fixture()
                self.schrijf(root, pad, "Vervangt AGENTS.md.\n")
                self.assertIn(f"{pad}: AGENTS.override.md vervangt", self.fouten(root))

    def test_ongetrackte_claude_local_md_blijft_toegestaan(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, "CLAUDE.local.md", "Persoonlijk.\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_geforceerd_getrackte_claude_local_md_wordt_geweigerd(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, "CLAUDE.local.md", "Persoonlijk.\n")
        self.schrijf(root, "sub/CLAUDE.local.md", "Persoonlijk.\n")
        self.git_voeg_toe(root, "CLAUDE.local.md", "sub/CLAUDE.local.md")
        fouten = self.fouten(root)
        self.assertIn("CLAUDE.local.md: CLAUDE.local.md is persoonlijk", fouten)
        self.assertIn("sub/CLAUDE.local.md: CLAUDE.local.md is persoonlijk", fouten)

    def test_getrackte_mcp_json_en_settings_local_worden_geweigerd(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, ".mcp.json", "{}\n")
        self.schrijf(root, ".claude/settings.local.json", "{}\n")
        self.git_voeg_toe(root, ".mcp.json", ".claude/settings.local.json")
        fouten = self.fouten(root)
        self.assertIn(".mcp.json: .mcp.json bevat de projectverwijzing", fouten)
        self.assertIn(".claude/settings.local.json: settings.local.json is persoonlijk", fouten)

    def test_ongetrackt_mcp_json_en_settings_local_blijven_toegestaan(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, ".mcp.json", "{}\n")
        self.schrijf(root, ".claude/settings.local.json", "{}\n")
        self.assertEqual(guard["controleer"](root), [])

    # ── git-tracking is leidend, ook in mappen die de doorloop overslaat ─────────────────────────
    def test_getrackte_instructiebestanden_in_uitgesloten_mappen_worden_gecontroleerd(self):
        for map_ in UITGESLOTEN_MAPPEN:
            with self.subTest(map=map_):
                root = self.fixture()
                self.git_repo(root)
                self.schrijf(root, f"{map_}/x/CLAUDE.md", "Eigen regels.\n")
                self.git_voeg_toe(root, f"{map_}/x/CLAUDE.md")
                self.assertIn(f"{map_}/x/CLAUDE.md: geen AGENTS.md ernaast", self.fouten(root))

    def test_getrackte_laadpaden_in_uitgesloten_mappen_worden_geweigerd(self):
        for map_ in UITGESLOTEN_MAPPEN:
            with self.subTest(map=map_):
                root = self.fixture()
                self.git_repo(root)
                self.schrijf(root, f"{map_}/.claude/commands/a.md", "Verstopt.\n")
                self.schrijf(root, f"{map_}/AGENTS.override.md", "Verstopt.\n")
                self.git_voeg_toe(root, f"{map_}/.claude/commands/a.md", f"{map_}/AGENTS.override.md")
                fouten = self.fouten(root)
                self.assertIn(f"{map_}/.claude/commands/a.md: slash-commands", fouten)
                self.assertIn(f"{map_}/AGENTS.override.md: AGENTS.override.md vervangt", fouten)

    def test_ongetrackte_bestanden_in_uitgesloten_mappen_worden_niet_gelezen(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, "node_modules/pakket/CLAUDE.md", "Van een afhankelijkheid.\n")
        self.schrijf(root, "bin/Debug/AGENTS.md", "Buildresultaat.\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_falende_git_wordt_gemeld_in_plaats_van_stil_overgeslagen(self):
        root = self.fixture()
        (root / ".git").mkdir()  # lijkt op een repository, maar git kan er niets mee
        if not shutil.which("git"):
            self.skipTest("git ontbreekt")
        self.assertIn("git ls-files faalt", self.fouten(root))

    def test_getrackt_maar_uit_de_werkboom_verwijderd_bestand_geeft_geen_crash(self):
        root = self.fixture()
        self.git_repo(root)
        self.schrijf(root, "docs/CLAUDE.md", "Weg.\n")
        self.git_voeg_toe(root, "docs/CLAUDE.md")
        (root / "docs/CLAUDE.md").unlink()
        self.assertEqual(guard["controleer"](root), [])

    # ── symlinks ────────────────────────────────────────────────────────────────────────────────
    def test_symlinks_op_instructiepaden_worden_geweigerd(self):
        gevallen = {
            "diep/CLAUDE.md": "../CLAUDE.md",
            "diep/AGENTS.md": "../AGENTS.md",
            "CLAUDE.local.md": "CLAUDE.md",
            "AGENTS.override.md": "AGENTS.md",
            ".claude/rules": "../sub",
            ".claude/commands": "../sub",
            "sub/.claude": "..",
            ".codex": "sub",
        }
        for pad, doel in gevallen.items():
            with self.subTest(pad=pad):
                root = self.fixture()
                (root / "diep").mkdir()
                self.symlink(root, pad, doel)
                self.assertIn(f"{pad}: symlink op een instructiepad", self.fouten(root))

    def test_symlink_op_skillmap_wordt_geweigerd(self):
        root = self.fixture()
        shutil.rmtree(root / ".claude/skills/autonoom")
        self.symlink(root, ".claude/skills/autonoom", "../../.agents/skills/autonoom")
        self.assertIn(".claude/skills/autonoom: symlink op een instructiepad", self.fouten(root))

    def test_symlinkcyclus_en_gebroken_symlink_hangen_niet_en_worden_geweigerd(self):
        root = self.fixture()
        self.symlink(root, ".claude/rules", "..")  # wijst naar zijn eigen ouder: een cyclus bij volgen
        self.symlink(root, ".agents/lus", "lus")   # wijst naar zichzelf
        self.symlink(root, ".claude/commands", "bestaat-niet")
        fouten = self.fouten(root)
        for pad in (".claude/rules", ".agents/lus", ".claude/commands"):
            self.assertIn(f"{pad}: symlink op een instructiepad", fouten)

    def test_getrackte_symlink_wordt_geweigerd(self):
        root = self.fixture()
        self.git_repo(root)
        self.symlink(root, "docs/CLAUDE.md", "../CLAUDE.md")
        self.git_voeg_toe(root, "docs/CLAUDE.md")
        self.assertIn("docs/CLAUDE.md: symlink op een instructiepad", self.fouten(root))

    def test_getrackte_symlink_in_uitgesloten_map_wordt_alleen_via_git_gezien(self):
        root = self.fixture()
        self.git_repo(root)
        self.symlink(root, "node_modules/x/CLAUDE.md", "../../CLAUDE.md")  # de doorloop komt hier nooit
        self.git_voeg_toe(root, "node_modules/x/CLAUDE.md")
        self.assertIn("node_modules/x/CLAUDE.md: symlink op een instructiepad", self.fouten(root))

    def test_symlink_buiten_instructiepaden_wordt_niet_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "docs/a.md", "x\n")
        self.symlink(root, "docs/b.md", "a.md")
        self.assertEqual(guard["controleer"](root), [])

    # ── settings en config met eigen laadpaden ───────────────────────────────────────────────────
    def test_settings_met_hooks_outputstyle_of_plugins_wordt_geweigerd(self):
        for sleutel in SETTINGS_SLEUTELS:
            with self.subTest(sleutel=sleutel):
                root = self.fixture()
                self.schrijf(root, ".claude/settings.json", '{"permissions": {}, "%s": {}}\n' % sleutel)
                self.assertIn(f"sleutel '{sleutel}' voegt instructies of laadpaden toe", self.fouten(root))

    def test_settings_met_alleen_permissies_is_groen(self):
        root = self.fixture()
        self.schrijf(root, ".claude/settings.json", '{"permissions": {"allow": ["Bash(git status)"]}}\n')
        self.assertEqual(guard["controleer"](root), [])

    def test_ongeldige_settings_json_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, ".claude/settings.json", "{niet-json")
        self.assertIn("geen geldige JSON", self.fouten(root))

    def test_codex_config_met_budget_of_bronselectie_wordt_geweigerd(self):
        for sleutel in CODEX_SLEUTELS:
            with self.subTest(sleutel=sleutel):
                root = self.fixture()
                self.schrijf(root, ".codex/config.toml", f'model = "x"\n  {sleutel} = 1\n')
                self.assertIn(f"sleutel '{sleutel}' wijzigt de bronselectie", self.fouten(root))

    def test_codex_config_zonder_instructiesleutels_is_groen(self):
        root = self.fixture()
        self.schrijf(root, ".codex/config.toml", 'model = "x"\n# project_doc_max_bytes = 1 staat hier alleen als commentaar\n')
        self.assertEqual(guard["controleer"](root), [])

    # ── omvang: plafond per bestand en per keten ─────────────────────────────────────────────────
    def test_plafond_exact_op_de_meting_is_groen(self):
        self.assertEqual(guard["controleer"](self.fixture()), [])

    def test_ontbrekend_plafondbestand_wordt_geweigerd(self):
        root = self.fixture()
        (root / "scripts/ci/agent-instructies-plafonds.txt").unlink()
        self.assertIn("agent-instructies-plafonds.txt: ontbreekt", self.fouten(root))

    def test_groei_met_een_byte_boven_het_plafond_wordt_geweigerd(self):
        root = self.fixture()
        bestand = root / "AGENTS.md"
        bestand.write_bytes(bestand.read_bytes() + b"x")
        fouten = self.fouten(root)
        self.assertIn("bestand:AGENTS.md: 23 bytes is hoger dan het plafond 22", fouten)
        self.assertIn("keten:sub/AGENTS.md", fouten)  # de keten groeit mee

    def test_plafond_een_byte_onder_de_meting_wordt_geweigerd(self):
        root = self.fixture()
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        self.plafond(root, "bestand:sub/AGENTS.md", meting["bestand:sub/AGENTS.md"] - 1)
        self.assertIn("bestand:sub/AGENTS.md", self.fouten(root))

    def test_nieuw_instructiebestand_zonder_plafond_wordt_geweigerd(self):
        root = self.fixture()
        self.schrijf(root, "nieuw/AGENTS.md", "# AGENTS.md\n\nNieuw.\n")
        self.schrijf(root, "nieuw/CLAUDE.md", STUB)
        fouten = self.fouten(root)
        self.assertIn("bestand:nieuw/AGENTS.md: heeft geen plafond", fouten)
        self.assertIn("keten:nieuw/AGENTS.md: heeft geen plafond", fouten)

    def test_winst_binnen_de_ruimte_hoeft_het_plafond_niet_te_verlagen(self):
        root = self.fixture()
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        ruimte = guard["PLAFOND_RUIMTE"]
        self.plafond(root, "bestand:AGENTS.md", meting["bestand:AGENTS.md"] + ruimte)
        self.plafond(root, "keten:sub/AGENTS.md", meting["keten:sub/AGENTS.md"] + ruimte)
        self.assertEqual(guard["controleer"](root), [])

    def test_winst_boven_de_ruimte_moet_in_het_plafond_worden_vastgezet(self):
        root = self.fixture()
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        self.plafond(root, "bestand:AGENTS.md", meting["bestand:AGENTS.md"] + guard["PLAFOND_RUIMTE"] + 1)
        self.assertIn("verlaag het plafond", self.fouten(root))

    def test_plafond_zonder_bestand_wordt_geweigerd(self):
        root = self.fixture()
        self.plafond(root, "bestand:weg/AGENTS.md", 10)
        self.assertIn("bestand:weg/AGENTS.md: staat in", self.fouten(root))

    def test_ongeldige_plafondregel_wordt_geweigerd(self):
        root = self.fixture()
        pad = root / "scripts/ci/agent-instructies-plafonds.txt"
        pad.write_text(pad.read_text(encoding="utf-8") + "bestand:AGENTS.md veel\n", encoding="utf-8")
        self.assertIn("ongeldige regel", self.fouten(root))

    def test_keten_telt_de_scheidingstekens_mee(self):
        root = self.fixture()
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        som = meting["bestand:AGENTS.md"] + meting["bestand:sub/AGENTS.md"]
        self.assertEqual(meting["keten:sub/AGENTS.md"], som + guard["KETEN_SCHEIDING_BYTES"])
        self.plafond(root, "keten:sub/AGENTS.md", som)  # zonder scheiding zou dit groen zijn
        self.assertIn("keten:sub/AGENTS.md", self.fouten(root))

    def test_omvang_telt_bytes_niet_tekens(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", "# AGENTS.md\n\n" + "é" * 10 + "\n")  # 10 tekens, 20 bytes
        self.zet_plafonds(root)
        self.assertEqual(guard["meet_omvang"](root, [root / "AGENTS.md"])["bestand:AGENTS.md"], 13 + 20 + 1)
        self.assertEqual(guard["controleer"](root), [])
        self.plafond(root, "bestand:AGENTS.md", 13 + 10 + 1)  # tekenaantal is geen bytetelling
        self.assertIn("bestand:AGENTS.md", self.fouten(root))

    def test_keten_van_drie_niveaus_heeft_per_niveau_een_meting(self):
        root = self.fixture()
        self.schrijf(root, "sub/diep/AGENTS.md", "# AGENTS.md\n\nDiep.\n")
        self.schrijf(root, "sub/diep/CLAUDE.md", STUB)
        self.zet_plafonds(root)
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        drie = sum(meting[f"bestand:{p}"] for p in ("AGENTS.md", "sub/AGENTS.md", "sub/diep/AGENTS.md")) + 2 * guard["KETEN_SCHEIDING_BYTES"]
        self.assertEqual(meting["keten:sub/diep/AGENTS.md"], drie)
        self.assertEqual(guard["controleer"](root), [])
        self.plafond(root, "keten:sub/diep/AGENTS.md", drie - 1)
        self.assertIn("keten:sub/diep/AGENTS.md", self.fouten(root))

    def test_zus_submappen_vormen_geen_gezamenlijke_keten(self):
        root = self.fixture()
        self.schrijf(root, "zus/AGENTS.md", "# AGENTS.md\n\nZus.\n")
        self.schrijf(root, "zus/CLAUDE.md", STUB)
        self.zet_plafonds(root)
        meting = guard["meet_omvang"](root, sorted(root.rglob("AGENTS.md")))
        self.assertEqual(meting["keten:zus/AGENTS.md"], meting["bestand:AGENTS.md"] + meting["bestand:zus/AGENTS.md"] + 2)

    def test_keten_boven_het_codexbudget_geeft_een_waarschuwing_maar_geen_fout_zolang_het_plafond_klopt(self):
        root = self.fixture()
        self.schrijf(root, "AGENTS.md", "# AGENTS.md\n\n" + "x" * guard["CODEX_MAX_BYTES"] + "\n")
        self.zet_plafonds(root)
        self.assertEqual(guard["controleer"](root), [])
        self.assertIn("Codex leest standaard maximaal", self.waarschuwingen(root))


if __name__ == "__main__":
    unittest.main()
