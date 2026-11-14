from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import version
import transaction
from test_support import exercise_failures, snapshot


APP_VERSION = "3.17.4.0"
API_VERSION = "3.16.0.0"


class VersionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.repo = Path(self.temporary.name)
        for relative in version.CSPROJS:
            path = self.repo / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("<Project><PropertyGroup><Version>3.17.4.0</Version><AssemblyVersion>3.17.4.0</AssemblyVersion><FileVersion>3.17.4.0</FileVersion></PropertyGroup></Project>\n", encoding="utf-8")
        api = {"openapi": "3.0.3", "info": {"title": "Example", "version": API_VERSION}, "paths": {}}
        (self.repo / version.YAML).parent.mkdir(parents=True, exist_ok=True)
        (self.repo / version.YAML).write_text(f"openapi: 3.0.3\ninfo:\n  title: Example\n  version: {API_VERSION}\npaths: {{}}\n", encoding="utf-8")
        (self.repo / version.JSON_PATH).write_text(json.dumps(api, indent=2) + "\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_build_updates_changes_only_version_values(self) -> None:
        before = {relative: (self.repo / relative).read_text(encoding="utf-8") for relative in (*version.CSPROJS, version.YAML, version.JSON_PATH)}
        updates = version.build_updates(self.repo, APP_VERSION, API_VERSION, "3.18.0.0")
        self.assertEqual(set(updates), set(before))
        self.assertEqual(before[version.YAML].replace(API_VERSION, "3.18.0.0"), updates[version.YAML])
        for relative in version.CSPROJS:
            self.assertEqual(before[relative].replace(APP_VERSION, "3.18.0.0"), updates[relative])
        self.assertEqual(json.loads(updates[version.JSON_PATH])["info"]["version"], "3.18.0.0")
        self.assertEqual(before[version.JSON_PATH], (self.repo / version.JSON_PATH).read_text(encoding="utf-8"))

    def test_expected_versions_fail_closed(self) -> None:
        with self.assertRaises(version.VersionError):
            version.build_updates(self.repo, "3.16.0.0", API_VERSION, "3.18.0.0")
        with self.assertRaises(version.VersionError):
            version.build_updates(self.repo, APP_VERSION, "3.15.0.0", "3.18.0.0")

    def test_current_json_must_match_yaml_semantically(self) -> None:
        (self.repo / version.JSON_PATH).write_text('{"openapi":"3.0.3","info":{"title":"Changed","version":"3.16.0.0"},"paths":{}}\n', encoding="utf-8")
        with self.assertRaises(version.VersionError):
            version.build_updates(self.repo, APP_VERSION, API_VERSION, "3.18.0.0")

    def test_cli_dry_run_is_default_and_apply_updates_only_expected_files(self) -> None:
        script = Path(version.__file__)
        arguments = [sys.executable, str(script), "--repo", str(self.repo), "--expected-current", APP_VERSION, "--expected-api-version", API_VERSION, "--version", "3.18.0.0"]
        before = {relative: (self.repo / relative).read_text(encoding="utf-8") for relative in (*version.CSPROJS, version.YAML, version.JSON_PATH)}
        preview = subprocess.run(arguments, check=True, capture_output=True, text=True)
        self.assertIn("Preview:", preview.stdout)
        self.assertEqual(before, {relative: (self.repo / relative).read_text(encoding="utf-8") for relative in before})
        applied = subprocess.run(arguments + ["--apply"], check=True, capture_output=True, text=True)
        self.assertIn("Versie bijgewerkt", applied.stdout)
        for relative in version.CSPROJS:
            self.assertIn("<Version>3.18.0.0</Version>", (self.repo / relative).read_text(encoding="utf-8"))
        self.assertEqual(json.loads((self.repo / version.JSON_PATH).read_text(encoding="utf-8"))["info"]["version"], "3.18.0.0")

    def test_every_write_boundary_recovers_and_dry_run_never_writes(self) -> None:
        exercise_failures(self, self.repo, version, lambda repo, apply: version.prepare(
            repo, APP_VERSION, API_VERSION, "3.18.0.0", apply), 7)

    def test_retry_and_later_allocation_keep_only_latest_state(self) -> None:
        version.prepare(self.repo, APP_VERSION, API_VERSION, "3.18.0.0", True)
        before = snapshot(self.repo)
        version.prepare(self.repo, APP_VERSION, API_VERSION, "3.18.0.0", True)
        self.assertEqual(before, snapshot(self.repo))
        version.prepare(self.repo, "3.18.0.0", "3.18.0.0", "3.19.0.0", True)
        state = json.loads((self.repo / version.JOURNAL).read_text())
        self.assertEqual(state["identity"]["target"], "3.19.0.0")
        self.assertEqual(list((self.repo / "artifacts/release").iterdir()), [self.repo / version.JOURNAL])

    def test_completed_retry_rejects_each_modified_target_before_writing(self) -> None:
        version.prepare(self.repo, APP_VERSION, API_VERSION, "3.18.0.0", True)
        for relative in (*version.CSPROJS, version.YAML, version.JSON_PATH):
            with self.subTest(relative=relative):
                path = self.repo / relative
                before = path.read_bytes()
                path.write_bytes(before + b" ")
                changed = snapshot(self.repo)
                for apply in (False, True):
                    with self.assertRaises(transaction.TransactionError):
                        version.prepare(self.repo, APP_VERSION, API_VERSION, "3.18.0.0", apply)
                    self.assertEqual(changed, snapshot(self.repo))
                path.write_bytes(before)


if __name__ == "__main__":
    unittest.main()
