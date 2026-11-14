from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import changelog
import transaction
from test_support import exercise_failures, snapshot


def fragment(change_id: str = "feature.test", *, impact: str = "user_facing", origin: str | None = None) -> dict:
    return {
        "schema_version": 1,
        "change_id": change_id,
        "issues": [1940],
        "category": "Added",
        "text": "Beheerders kunnen een wijziging bekijken",
        "release_impact": impact,
        "provenance": {"origin_release": origin},
    }


class ChangelogTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.repo = Path(self.temporary.name)
        (self.repo / "changelog.d").mkdir()
        (self.repo / "CHANGELOG.md").write_text("# Changelog\n\n## [Unreleased]\n\n- Legacy entry (#1900)\n\n## [3.0.0.0] — 2026-01-01\n\nOud\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_fragment(self, data: dict, name: str | None = None) -> Path:
        path = self.repo / "changelog.d" / (name or f"{data['change_id']}.json")
        path.write_text(json.dumps(data, ensure_ascii=False) + "\n", encoding="utf-8")
        return path

    def test_preview_is_sorted_and_internal_is_not_rendered(self) -> None:
        self.write_fragment(fragment("zeta.added"))
        internal = fragment("alpha.internal", impact="internal")
        internal["text"] = "Interne wijziging zonder effect voor beheerders"
        self.write_fragment(internal)
        rendered = changelog.preview(self.repo)
        self.assertIn("### Added", rendered)
        self.assertNotIn("Interne wijziging", rendered)
        self.assertIn("(#1940)", rendered)

    def test_unknown_and_missing_fields_and_html_are_rejected(self) -> None:
        data = fragment()
        data["extra"] = True
        self.write_fragment(data)
        with self.assertRaises(changelog.ChangelogError):
            changelog.validate(self.repo)
        (self.repo / "changelog.d" / "feature.test.json").unlink()
        data = fragment()
        del data["issues"]
        self.write_fragment(data)
        with self.assertRaises(changelog.ChangelogError):
            changelog.validate(self.repo)
        (self.repo / "changelog.d" / "feature.test.json").unlink()
        data = fragment()
        data["text"] = "<script>alert(1)</script>"
        self.write_fragment(data)
        with self.assertRaises(changelog.ChangelogError):
            changelog.validate(self.repo)

    def test_repeated_id_with_different_filename_fails_closed(self) -> None:
        self.write_fragment(fragment("feature.test"))
        self.write_fragment(fragment("other.duplicate"), "z-duplicate.json")
        with self.assertRaises(changelog.ChangelogError):
            changelog.validate(self.repo)

    def test_prepare_preserves_legacy_content_and_records_ids_once(self) -> None:
        self.write_fragment(fragment())
        preview = changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=False)
        self.assertIn("Legacy entry (#1900)", preview)
        self.assertIn("Beheerders kunnen", preview)
        self.assertFalse((self.repo / changelog.MANIFEST).exists())
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        output = (self.repo / "CHANGELOG.md").read_text(encoding="utf-8")
        self.assertIn("## [Unreleased]", output)
        self.assertIn("## [3.18.0.0] — 2026-11-14", output)
        self.assertIn("Legacy entry (#1900)", output)
        manifest = json.loads((self.repo / changelog.MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(manifest["releases"][0]["change_ids"], ["feature.test"])
        before_repeat = snapshot(self.repo)
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        self.assertEqual(before_repeat, snapshot(self.repo))

    def test_interrupted_prepare_recovers_from_journal(self) -> None:
        self.write_fragment(fragment())
        real_write = changelog._write_atomic

        def interrupt_manifest(path: Path, content: bytes) -> None:
            if path.name == "release-manifest.json":
                raise OSError("simulated interruption")
            real_write(path, content)

        with patch.object(changelog, "_write_atomic", side_effect=interrupt_manifest):
            with self.assertRaises(OSError):
                changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        self.assertTrue((self.repo / changelog.JOURNAL).exists())
        result = changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        self.assertIn("Hersteljournaal verwerkt", result)
        self.assertEqual(json.loads((self.repo / changelog.JOURNAL).read_text())["status"], "completed")
        manifest = json.loads((self.repo / changelog.MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(manifest["releases"][0]["change_ids"], ["feature.test"])

    def test_hotfix_backport_origin_is_not_announced_twice(self) -> None:
        self.write_fragment(fragment(origin="3.17.4.1"))
        preview = changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=False)
        self.assertNotIn("Beheerders kunnen", preview)
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        manifest = json.loads((self.repo / changelog.MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(manifest["changes"]["feature.test"]["version"], "3.17.4.1")
        self.assertEqual(manifest["releases"][0]["change_ids"], [])

    def test_repeated_fragment_is_not_announced_in_a_later_release(self) -> None:
        self.write_fragment(fragment())
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        preview = changelog.prepare(self.repo, "3.19.0.0", "2026-12-01", apply=False)
        self.assertEqual(preview.count("Beheerders kunnen"), 1)

    def test_backport_content_mismatch_is_rejected(self) -> None:
        self.write_fragment(fragment(origin="3.17.4.1"))
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply=True)
        changed = fragment(origin="3.17.4.1")
        changed["text"] = "Andere inhoud met hetzelfde stabiele ID"
        self.write_fragment(changed)
        with self.assertRaises(changelog.ChangelogError):
            changelog.preview(self.repo)

    def test_every_write_boundary_recovers_and_dry_run_never_writes(self) -> None:
        self.write_fragment(fragment())
        exercise_failures(self, self.repo, changelog, lambda repo, apply: changelog.prepare(
            repo, "3.18.0.0", "2026-11-14", apply), 4)

    def test_completed_retry_rejects_modified_content_or_inputs(self) -> None:
        self.write_fragment(fragment())
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        for relative in (changelog.CHANGELOG, changelog.MANIFEST, "changelog.d/feature.test.json"):
            with self.subTest(relative=relative):
                path = self.repo / relative
                before = path.read_bytes()
                path.write_bytes(before + b" ")
                changed = snapshot(self.repo)
                for apply in (False, True):
                    with self.assertRaises(transaction.TransactionError):
                        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply)
                    self.assertEqual(changed, snapshot(self.repo))
                path.write_bytes(before)

    def test_fresh_worktree_retry_uses_committed_proof_without_writes(self) -> None:
        self.write_fragment(fragment())
        expected = changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", False)
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        (self.repo / changelog.JOURNAL).unlink()
        before = snapshot(self.repo)
        for apply in (False, True):
            self.assertEqual(changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply), expected)
            self.assertEqual(before, snapshot(self.repo))
        for relative in (changelog.CHANGELOG, changelog.MANIFEST, "changelog.d/feature.test.json"):
            path = self.repo / relative
            original = path.read_bytes()
            path.write_bytes(original + b" ")
            changed = snapshot(self.repo)
            for apply in (False, True):
                with self.assertRaises(changelog.ChangelogError):
                    changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", apply)
                self.assertEqual(changed, snapshot(self.repo))
            path.write_bytes(original)

    def test_legacy_release_without_exact_proof_fails_closed(self) -> None:
        changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        (self.repo / changelog.JOURNAL).unlink()
        path = self.repo / changelog.MANIFEST
        manifest = json.loads(path.read_text())
        manifest["releases"][0].pop("proof")
        path.write_text(json.dumps(manifest))
        before = snapshot(self.repo)
        with self.assertRaises(changelog.ChangelogError):
            changelog.prepare(self.repo, "3.18.0.0", "2026-11-14", True)
        self.assertEqual(before, snapshot(self.repo))


if __name__ == "__main__":
    unittest.main()
