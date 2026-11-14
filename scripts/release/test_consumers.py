"""Exercise the checked-in release consumers against generated changelogs."""

from __future__ import annotations

import os
import re
import subprocess
import unittest
from pathlib import Path

import changelog
import test_changelog
from test_changelog import fragment
from test_support import snapshot

ROOT = Path(__file__).resolve().parents[2]


def assignment_command(workflow: str, variable: str) -> str:
    source = (ROOT / ".github/workflows" / workflow).read_text()
    match = re.search(rf"(?m)^ *{variable}=\$\(", source)
    if not match:
        raise AssertionError(f"Workflow mist {variable}")
    lines = source[match.end():].splitlines()
    command = []
    for line in lines:
        command.append(line)
        if line.rstrip().endswith(")"):
            return "\n".join(command).rstrip()[:-1]
    raise AssertionError(f"Workflow mist einde van {variable}")


def consume(repo: Path, command: str, version: str = "3.18.0.0") -> str:
    return subprocess.run(["bash", "-c", command], cwd=repo, check=True,
                          capture_output=True, text=True,
                          env={"PATH": os.defpath, "LC_ALL": "C", "VERSION": version}).stdout


class ConsumerTests(unittest.TestCase):
    setUp = test_changelog.ChangelogTests.setUp
    tearDown = test_changelog.ChangelogTests.tearDown
    write_fragment = test_changelog.ChangelogTests.write_fragment

    def test_existing_consumers_keep_legacy_new_ids_and_hotfix_provenance(self):
        path = self.repo / changelog.CHANGELOG
        path.write_text(path.read_text().replace("Oud\n", "Oud (#1880)\n"))
        self.write_fragment(fragment())
        extra = fragment("feature.second")
        extra["issues"] = [1941, 1942]
        extra["text"] = "Tweede wijziging voor beheerders"
        self.write_fragment(extra)
        hotfix = fragment("fix.previous", origin="3.17.4.1")
        hotfix["issues"] = [1881]
        hotfix["text"] = "Eerder aangekondigde reparatie"
        self.write_fragment(hotfix)
        heading_command = assignment_command("pre-release-check.yml", "MAIN_VERSIES")
        heading_command = "cat CHANGELOG.md | " + heading_command.split(" | ", 1)[1]
        original_headings = set(consume(self.repo, heading_command).splitlines())
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        release_command = assignment_command("release.yml", "NOTES")
        close_command = assignment_command("close-released-issues.yml", "CHANGELOG_NUMBERS")
        notes = consume(self.repo, release_command)
        self.assertIn("Legacy entry (#1900)", notes)
        for issue in (1900, 1940, 1941, 1942):
            self.assertEqual(notes.count(f"#{issue}"), 1)
        self.assertNotIn("Eerder aangekondigde reparatie", notes)
        self.assertNotIn("#1880", notes)
        self.assertEqual(consume(self.repo, close_command).splitlines(), ["1900", "1940", "1941", "1942"])
        headings = set(consume(self.repo, heading_command).splitlines())
        self.assertTrue(original_headings <= headings)
        self.assertIn("## [3.18.0.0]", headings)
        before = snapshot(self.repo)
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        self.assertEqual(snapshot(self.repo), before)
        self.assertEqual(consume(self.repo, release_command), notes)

    def test_lost_pending_journal_cannot_duplicate_existing_release_heading(self):
        from unittest.mock import patch
        real_write = changelog._write_atomic

        def stop(path, content):
            if path.name == "release-manifest.json":
                raise OSError("interrupted")
            real_write(path, content)

        with patch.object(changelog, "_write_atomic", side_effect=stop):
            with self.assertRaises(OSError):
                changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        (self.repo / changelog.JOURNAL).unlink()
        before = snapshot(self.repo)
        for apply in (False, True):
            with self.assertRaises(changelog.ChangelogError):
                changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply)
            self.assertEqual(snapshot(self.repo), before)
