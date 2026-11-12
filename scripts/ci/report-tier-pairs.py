#!/usr/bin/env python3
"""Report path-based SQL Server/Postgres C# file pairing candidates."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import defaultdict
from pathlib import Path


def git_output(args: list[str], cwd: Path | None = None) -> str:
    result = subprocess.run(
        ["git", *args],
        cwd=cwd,
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def pair_paths(paths: list[str], ref: str, resolved_commit: str) -> dict[str, object]:
    sql = sorted(
        path for path in paths if path.startswith("FunctionApp/") and path.endswith(".cs")
    )
    postgres = sorted(
        path
        for path in paths
        if path.startswith("FunctionApp.Postgres/") and path.endswith(".cs")
    )
    sql_by_relative = {path.removeprefix("FunctionApp/"): path for path in sql}
    postgres_by_relative = {
        path.removeprefix("FunctionApp.Postgres/"): path for path in postgres
    }

    exact_relative = sorted(sql_by_relative.keys() & postgres_by_relative.keys())
    exact_pairs = [
        {
            "sql_server": sql_by_relative[relative],
            "postgres": postgres_by_relative[relative],
        }
        for relative in exact_relative
    ]

    remaining_sql = sorted(set(sql_by_relative) - set(exact_relative))
    remaining_postgres = sorted(set(postgres_by_relative) - set(exact_relative))
    sql_by_name: dict[str, list[str]] = defaultdict(list)
    postgres_by_name: dict[str, list[str]] = defaultdict(list)
    for relative in remaining_sql:
        sql_by_name[Path(relative).name].append(relative)
    for relative in remaining_postgres:
        postgres_by_name[Path(relative).name].append(relative)

    candidates: list[dict[str, str]] = []
    ambiguous: list[dict[str, object]] = []
    unmatched_sql: list[str] = []
    unmatched_postgres: list[str] = []
    for basename in sorted(sql_by_name.keys() | postgres_by_name.keys()):
        sql_group = sql_by_name.get(basename, [])
        postgres_group = postgres_by_name.get(basename, [])
        if len(sql_group) == len(postgres_group) == 1:
            candidates.append(
                {
                    "sql_server": sql_by_relative[sql_group[0]],
                    "postgres": postgres_by_relative[postgres_group[0]],
                }
            )
        elif sql_group and postgres_group:
            ambiguous.append(
                {
                    "basename": basename,
                    "sql_server": [sql_by_relative[relative] for relative in sql_group],
                    "postgres": [
                        postgres_by_relative[relative] for relative in postgres_group
                    ],
                }
            )
        elif sql_group:
            unmatched_sql.extend(sql_by_relative[relative] for relative in sql_group)
        else:
            unmatched_postgres.extend(
                postgres_by_relative[relative] for relative in postgres_group
            )

    return {
        "ref": ref,
        "resolved_commit": resolved_commit,
        "scope": {
            "sql_server": "FunctionApp/**/*.cs",
            "postgres": "FunctionApp.Postgres/**/*.cs",
            "path_source": "git ls-tree tracked paths at the selected ref",
        },
        "pairing_definition": (
            "Exact relative paths are paired first. Remaining files with a basename unique "
            "on both sides are listed as candidates. Multiple basename matches are ambiguous."
        ),
        "interpretation": (
            "This is a path inventory only. It does not compare file contents or establish "
            "semantic equivalence; basename candidates are not confirmed pairs."
        ),
        "exact_pairs": exact_pairs,
        "unique_basename_candidates": candidates,
        "ambiguous_basename_groups": ambiguous,
        "unmatched_sql_server": sorted(unmatched_sql),
        "unmatched_postgres": sorted(unmatched_postgres),
    }


def inventory_for_ref(ref: str, cwd: Path | None = None) -> dict[str, object]:
    resolved_commit = git_output(
        ["rev-parse", "--verify", "--end-of-options", f"{ref}^{{commit}}"], cwd
    )
    paths = git_output(["ls-tree", "-r", "--name-only", resolved_commit], cwd).splitlines()
    return pair_paths(paths, ref, resolved_commit)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--ref", default="HEAD", help="Git commit/ref to inspect (default: HEAD)"
    )
    args = parser.parse_args()
    try:
        inventory = inventory_for_ref(args.ref)
    except (subprocess.CalledProcessError, OSError):
        print(f"Unable to read tracked paths for ref {args.ref!r}.", file=sys.stderr)
        return 2
    print(json.dumps(inventory, ensure_ascii=False, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
