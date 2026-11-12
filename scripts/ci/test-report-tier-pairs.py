#!/usr/bin/env python3
"""Focused tests for the read-only tier pairing inventory."""

from __future__ import annotations

import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("report-tier-pairs.py")
SPEC = importlib.util.spec_from_file_location("report_tier_pairs", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
REPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORT)


class PairPathsTests(unittest.TestCase):
    def test_same_relative_path_is_reported_as_an_exact_cross_tier_pair(self) -> None:
        result = REPORT.pair_paths(
            ["FunctionApp/Planner/Service.cs", "FunctionApp.Postgres/Planner/Service.cs"],
            "test-ref",
            "test-commit",
        )
        self.assertEqual(
            result["exact_pairs"],
            [
                {
                    "sql_server": "FunctionApp/Planner/Service.cs",
                    "postgres": "FunctionApp.Postgres/Planner/Service.cs",
                }
            ],
        )
        self.assertEqual(result["unique_basename_candidates"], [])

    def test_exact_relative_path_takes_priority_then_unique_basename_is_candidate(self) -> None:
        result = REPORT.pair_paths(
            [
                "FunctionApp/one/Shared.cs",
                "FunctionApp/two/Shared.cs",
                "FunctionApp.Postgres/one/Shared.cs",
                "FunctionApp.Postgres/three/Shared.cs",
            ],
            "test-ref",
            "test-commit",
        )
        self.assertEqual(
            result["exact_pairs"],
            [
                {
                    "sql_server": "FunctionApp/one/Shared.cs",
                    "postgres": "FunctionApp.Postgres/one/Shared.cs",
                }
            ],
        )
        self.assertEqual(
            result["unique_basename_candidates"],
            [
                {
                    "sql_server": "FunctionApp/two/Shared.cs",
                    "postgres": "FunctionApp.Postgres/three/Shared.cs",
                }
            ],
        )

    def test_duplicate_basename_is_reported_as_ambiguous(self) -> None:
        result = REPORT.pair_paths(
            [
                "FunctionApp/one/Shared.cs",
                "FunctionApp/two/Shared.cs",
                "FunctionApp.Postgres/three/Shared.cs",
            ],
            "test-ref",
            "test-commit",
        )
        self.assertEqual(result["unique_basename_candidates"], [])
        self.assertEqual(
            result["ambiguous_basename_groups"],
            [
                {
                    "basename": "Shared.cs",
                    "sql_server": [
                        "FunctionApp/one/Shared.cs",
                        "FunctionApp/two/Shared.cs",
                    ],
                    "postgres": ["FunctionApp.Postgres/three/Shared.cs"],
                }
            ],
        )

    def test_renamed_file_is_not_invented_as_a_pair(self) -> None:
        result = REPORT.pair_paths(
            [
                "FunctionApp/Sync/SportlinkSyncPipeline.cs",
                "FunctionApp.Postgres/Sync/PostgresSyncPipeline.cs",
            ],
            "test-ref",
            "test-commit",
        )
        self.assertEqual(result["exact_pairs"], [])
        self.assertEqual(result["unique_basename_candidates"], [])
        self.assertEqual(
            result["unmatched_sql_server"],
            ["FunctionApp/Sync/SportlinkSyncPipeline.cs"],
        )
        self.assertEqual(
            result["unmatched_postgres"],
            ["FunctionApp.Postgres/Sync/PostgresSyncPipeline.cs"],
        )

    def test_path_inventory_does_not_claim_semantic_equivalence(self) -> None:
        result = REPORT.pair_paths(
            ["FunctionApp/Sync/Pipeline.cs", "FunctionApp.Postgres/Other/Pipeline.cs"],
            "test-ref",
            "test-commit",
        )
        self.assertIn("does not compare file contents", result["interpretation"])
        self.assertEqual(len(result["unique_basename_candidates"]), 1)


class GitRefTests(unittest.TestCase):
    def test_inventory_reads_tracked_paths_at_explicit_commit(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            subprocess.run(["git", "init", "--quiet", "-b", "main"], cwd=root, check=True)
            subprocess.run(["git", "config", "user.name", "Test Author"], cwd=root, check=True)
            subprocess.run(
                ["git", "config", "user.email", "trainer@voorbeeld.nl"],
                cwd=root,
                check=True,
            )
            for relative in (
                "FunctionApp/Service.cs",
                "FunctionApp.Postgres/Service.cs",
                "FunctionApp/SqlOnly.cs",
            ):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("synthetic fixture\n", encoding="utf-8")
            subprocess.run(["git", "add", "FunctionApp", "FunctionApp.Postgres"], cwd=root, check=True)
            subprocess.run(["git", "commit", "--quiet", "-m", "fixture"], cwd=root, check=True)
            commit = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=root, check=True, capture_output=True, text=True
            ).stdout.strip()

            result = REPORT.inventory_for_ref(commit, cwd=root)

        self.assertEqual(result["ref"], commit)
        self.assertEqual(result["resolved_commit"], commit)
        self.assertEqual(len(result["exact_pairs"]), 1)
        self.assertEqual(result["unmatched_sql_server"], ["FunctionApp/SqlOnly.cs"])


if __name__ == "__main__":
    unittest.main()
