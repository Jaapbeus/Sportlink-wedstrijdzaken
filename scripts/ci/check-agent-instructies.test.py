#!/usr/bin/env python3
"""Positieve/negatieve checks in tijdelijke fixtures; wijzigt geen repositorybestanden."""

import runpy
import tempfile
import unittest
from pathlib import Path

guard = runpy.run_path(str(Path(__file__).with_name("check-agent-instructies.py")))


class AgentInstructiesTests(unittest.TestCase):
    def fixture(self, links, rechts):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        for naam in ("release", "sluitsessie", "startdebug"):
            for basis in (".claude", ".agents"):
                pad = root / basis / "skills" / naam / "SKILL.md"
                pad.parent.mkdir(parents=True)
                pad.write_text("Gedeelde instructie.\n")
        for basis, tekst in ((".claude", links), (".agents", rechts)):
            pad = root / basis / "skills/autonoom/SKILL.md"
            pad.parent.mkdir(parents=True)
            pad.write_text(tekst)
        return root

    def test_documentverwijzing_mag_verschillen(self):
        root = self.fixture("Lees CLAUDE.md\n```bash\ngit status\n```\n", "Lees AGENTS.md\n```bash\ngit status\n```\n")
        self.assertEqual(guard["controleer"](root), [])

    def test_bevoegdheidsdrift_wordt_geweigerd(self):
        root = self.fixture("Review alleen read-only.\n", "Implementeer in andermans worktree.\n")
        self.assertTrue(guard["controleer"](root))

    def test_losse_fence_wordt_geweigerd_ook_als_beide_twins_gelijk_zijn(self):
        root = self.fixture("Tekst\n```\n### Vervolg\n", "Tekst\n```\n### Vervolg\n")
        self.assertEqual(len(guard["controleer"](root)), 2)

    def test_verplichte_twin_ontbreekt(self):
        root = self.fixture("Tekst\n", "Tekst\n")
        (root / ".agents/skills/autonoom/SKILL.md").unlink()
        self.assertTrue(guard["controleer"](root))

    def test_beide_verplichte_twins_ontbreken(self):
        root = self.fixture("Tekst\n", "Tekst\n")
        for basis in (".claude", ".agents"):
            (root / basis / "skills/autonoom/SKILL.md").unlink()
        self.assertTrue(guard["controleer"](root))

    def test_fencelengte_en_type(self):
        check = guard["onafgesloten_codeblok"]
        self.assertIsNone(check("````text\n```\n~~~~\n````\n"))
        self.assertEqual(check("````text\n```\n"), 1)
        self.assertIsNone(check("~~~text\n```\n~~~\n"))
        self.assertIsNone(check("Een inline ``` is geen codeblok.\n"))


if __name__ == "__main__":
    unittest.main()
