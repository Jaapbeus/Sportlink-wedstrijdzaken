#!/usr/bin/env python3
"""Safely set the synchronized application and OpenAPI version."""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml

VERSION_RE = re.compile(r"\A\d+\.\d+\.\d+\.\d+\Z")
CSPROJS = (
    "FunctionApp/fa-dev-sportlink-01.csproj",
    "BlazorAdmin/BlazorAdmin.csproj",
    "FunctionApp.Postgres/FunctionApp.Postgres.csproj",
)
YAML = "docs/api-standaarden/openapi.yaml"
JSON_PATH = "docs/api-standaarden/openapi.json"


class VersionError(Exception):
    """Version input does not match repository state."""


def _csproj_update(path: Path, expected: str, target: str) -> str:
    source = path.read_text(encoding="utf-8")
    root = ET.fromstring(source)
    fields = ("Version", "AssemblyVersion", "FileVersion")
    for field in fields:
        nodes = root.findall(f".//{field}")
        if len(nodes) != 1 or nodes[0].text != expected:
            raise VersionError(f"{path}: verwacht exact één <{field}>{expected}</{field}>")
    updated = source
    for field in fields:
        pattern = re.compile(rf"(<{field}>){re.escape(expected)}(</{field}>)")
        updated, count = pattern.subn(rf"\g<1>{target}\g<2>", updated)
        if count != 1:
            raise VersionError(f"{path}: tekstuele vervanging van {field} is niet uniek")
    return updated


def _yaml_update(source: str, expected: str, target: str) -> str:
    try:
        document = yaml.safe_load(source)
    except yaml.YAMLError as exc:
        raise VersionError("OpenAPI YAML is ongeldig") from exc
    if not isinstance(document, dict) or not isinstance(document.get("info"), dict) or document["info"].get("version") != expected:
        raise VersionError("OpenAPI YAML info.version wijkt af van de verwachte API-versie")
    match = re.search(r"(?ms)^info:\s*\n(.*?)(?=^[^ \t#\n][^\n]*:\s*(?:#.*)?$|\Z)", source)
    if not match:
        raise VersionError("OpenAPI YAML mist een info-object")
    body = match.group(1)
    pattern = re.compile(r"(?m)^(  version:\s*)" + re.escape(expected) + r"(\s*(?:#.*)?)$")
    updated_body, count = pattern.subn(rf"\g<1>{target}\g<2>", body)
    if count != 1:
        raise VersionError("OpenAPI YAML info.version wijkt af of is niet uniek")
    return source[:match.start(1)] + updated_body + source[match.end(1):]


def build_updates(repo: Path, expected: str, expected_api: str, target: str) -> dict[str, str]:
    if not VERSION_RE.fullmatch(expected) or not VERSION_RE.fullmatch(expected_api) or not VERSION_RE.fullmatch(target):
        raise VersionError("Versies moeten MAJOR.MINOR.PATCH.REVISION zijn")
    updates = {}
    for relative in CSPROJS:
        updates[relative] = _csproj_update(repo / relative, expected, target)
    yaml_source = (repo / YAML).read_text(encoding="utf-8")
    updates[YAML] = _yaml_update(yaml_source, expected_api, target)
    try:
        document = yaml.safe_load(updates[YAML])
    except yaml.YAMLError as exc:
        raise VersionError("Gewijzigde OpenAPI YAML is ongeldig") from exc
    if not isinstance(document, dict) or not isinstance(document.get("info"), dict) or document["info"].get("version") != target:
        raise VersionError("OpenAPI YAML heeft na wijziging geen verwachte info.version")
    json_source = (repo / JSON_PATH).read_text(encoding="utf-8")
    try:
        existing_json = json.loads(json_source)
    except json.JSONDecodeError as exc:
        raise VersionError("OpenAPI JSON is ongeldig") from exc
    if existing_json != yaml.safe_load(yaml_source):
        raise VersionError("OpenAPI JSON is niet semantisch gelijk aan de huidige YAML")
    updates[JSON_PATH] = json.dumps(document, ensure_ascii=False, indent=2) + "\n"
    return updates


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--expected-current", required=True)
    parser.add_argument("--expected-api-version", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--apply", action="store_true", help="Schrijf de versiewijzigingen; standaard alleen preview")
    args = parser.parse_args(argv)
    try:
        repo = args.repo.resolve(strict=True)
        updates = build_updates(repo, args.expected_current, args.expected_api_version, args.version)
        if args.apply:
            for relative, content in updates.items():
                (repo / relative).write_text(content, encoding="utf-8")
            print(f"Versie bijgewerkt naar {args.version} in {len(updates)} bestanden.")
        else:
            print(f"Preview: {len(updates)} bestanden krijgen versie {args.version}; gebruik --apply om te schrijven.")
        return 0
    except (VersionError, OSError, ET.ParseError) as exc:
        print(f"Fout: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
