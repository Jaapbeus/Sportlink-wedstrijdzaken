#!/usr/bin/env python3
"""Positieve/negatieve checks in tijdelijke fixtures; wijzigt geen repositorybestanden.

Elke negatieve test maakt van één afspraak uit check-agent-instructies.py tijdelijk een echte
overtreding en eist dat de guard rood wordt — een guard die niets ziet, is ook groen.
"""

import runpy
import tempfile
import unittest
from pathlib import Path

guard = runpy.run_path(str(Path(__file__).with_name("check-agent-instructies.py")))

STUB = "@AGENTS.md\n\nAGENTS.md is de enige bron van de agentinstructies; zet hier geen inhoud bij.\n"
SKILLS = ("autonoom", "release", "sluitsessie", "startdebug")


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
        return root

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
        self.assertIn(".claude/rules/testen.md: regelbestanden voor alleen Claude Code", self.fouten(root))

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


if __name__ == "__main__":
    unittest.main()
