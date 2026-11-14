#!/usr/bin/env python3
"""Validate and aggregate immutable changelog fragments."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import date as calendar_date
from pathlib import Path
from typing import Any

import transaction
from transaction import write_atomic as _write_atomic


VERSION_RE = re.compile(r"\A\d+\.\d+\.\d+\.\d+\Z")
CHANGE_ID_RE = re.compile(r"\A[a-z0-9][a-z0-9._-]{2,79}\Z")
TEXT_RE = re.compile(r"\A[^<>`\[\]*_~|\\\x00-\x1f\x7f]+\Z")
FRAGMENT_FIELDS = {"schema_version", "change_id", "issues", "category", "text", "release_impact", "provenance"}
MANIFEST_FIELDS = {"schema_version", "releases", "changes"}
CATEGORIES = {"Added", "Changed", "Fixed", "Security", "Removed"}
IMPACTS = {"user_facing", "internal"}
CHANGELOG = "CHANGELOG.md"
MANIFEST = "changelog.d/release-manifest.json"
JOURNAL = "artifacts/release/changelog-state.json"


class ChangelogError(Exception):
    """Invalid release input or unsafe recovery state."""


def _json(path: Path) -> Any:
    def unique_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result = {}
        for key, value in pairs:
            if key in result:
                raise ChangelogError(f"Dubbele JSON-sleutel: {key} in {path}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_pairs)
    except (OSError, json.JSONDecodeError) as exc:
        raise ChangelogError(f"Kan JSON niet lezen: {path}") from exc


def _exact_keys(value: dict[str, Any], expected: set[str], label: str) -> None:
    missing = expected - value.keys()
    extra = value.keys() - expected
    if missing or extra:
        raise ChangelogError(f"{label}: ontbrekende velden {sorted(missing)}; onbekende velden {sorted(extra)}")


def _validate_fragment(data: Any, path: Path) -> dict[str, Any]:
    if not isinstance(data, dict):
        raise ChangelogError(f"Fragment moet een JSON-object zijn: {path}")
    _exact_keys(data, FRAGMENT_FIELDS, str(path))
    if type(data["schema_version"]) is not int or data["schema_version"] != 1:
        raise ChangelogError(f"Ongesteunde schema_version in {path}")
    change_id = data["change_id"]
    if not isinstance(change_id, str) or not CHANGE_ID_RE.fullmatch(change_id):
        raise ChangelogError(f"Ongeldige change_id in {path}")
    issues = data["issues"]
    if not isinstance(issues, list) or not issues or any(type(item) is not int or item <= 0 for item in issues):
        raise ChangelogError(f"issues moet een niet-lege lijst positieve issue-nummers zijn in {path}")
    if len(issues) != len(set(issues)):
        raise ChangelogError(f"Dubbele issue-nummers in {path}")
    if not isinstance(data["category"], str) or data["category"] not in CATEGORIES:
        raise ChangelogError(f"Ongeldige category in {path}")
    text = data["text"]
    if not isinstance(text, str) or not text.strip() or not TEXT_RE.fullmatch(text) or "\n" in text or "\r" in text:
        raise ChangelogError(f"Ongeldige text in {path}; gebruik één regel zonder HTML-markup")
    if not isinstance(data["release_impact"], str) or data["release_impact"] not in IMPACTS:
        raise ChangelogError(f"Ongeldige release_impact in {path}")
    provenance = data["provenance"]
    if not isinstance(provenance, dict) or set(provenance) != {"origin_release"}:
        raise ChangelogError(f"provenance moet uitsluitend origin_release bevatten in {path}")
    origin = provenance["origin_release"]
    if origin is not None and (not isinstance(origin, str) or not VERSION_RE.fullmatch(origin)):
        raise ChangelogError(f"Ongeldige provenance.origin_release in {path}")
    if path.name != f"{change_id}.json":
        raise ChangelogError(f"Bestandsnaam moet {change_id}.json zijn")
    return data


def _load_fragments(repo: Path) -> list[dict[str, Any]]:
    directory = repo / "changelog.d"
    fragments: list[dict[str, Any]] = []
    seen: set[str] = set()
    for path in sorted(directory.glob("*.json")):
        if path.name in {Path(MANIFEST).name, "schema-v1.json"}:
            continue
        fragment = _validate_fragment(_json(path), path.relative_to(repo))
        if fragment["change_id"] in seen:
            raise ChangelogError(f"Dubbele change_id: {fragment['change_id']}")
        seen.add(fragment["change_id"])
        fragments.append(fragment)
    return fragments


def _load_manifest(repo: Path) -> dict[str, Any]:
    path = repo / MANIFEST
    if not path.exists():
        return {"schema_version": 1, "releases": [], "changes": {}}
    manifest = _json(path)
    if not isinstance(manifest, dict):
        raise ChangelogError("Releasemanifest moet een JSON-object zijn")
    _exact_keys(manifest, MANIFEST_FIELDS, MANIFEST)
    if type(manifest["schema_version"]) is not int or manifest["schema_version"] != 1 or not isinstance(manifest["releases"], list) or not isinstance(manifest["changes"], dict):
        raise ChangelogError("Ongeldig releasemanifest")
    for release in manifest["releases"]:
        if not isinstance(release, dict) or set(release) not in ({"version", "date", "change_ids"}, {"version", "date", "change_ids", "proof"}):
            raise ChangelogError("Ongeldige releasevermelding in manifest")
        if not isinstance(release["version"], str) or not VERSION_RE.fullmatch(release["version"]):
            raise ChangelogError("Ongeldig versienummer in manifest")
        try:
            parsed_date = calendar_date.fromisoformat(release["date"])
        except (TypeError, ValueError):
            raise ChangelogError("Ongeldige releasedatum in manifest")
        if parsed_date.isoformat() != release["date"]:
            raise ChangelogError("Ongeldige releasedatum in manifest")
        if not isinstance(release["change_ids"], list) or any(not isinstance(item, str) for item in release["change_ids"]) or len(release["change_ids"]) != len(set(release["change_ids"])):
            raise ChangelogError("Ongeldige change_ids in manifest")
        if "proof" in release:
            _validate_proof(release["proof"])
    for change_id, record in manifest["changes"].items():
        if not CHANGE_ID_RE.fullmatch(change_id) or not isinstance(record, dict) or set(record) != {"version", "fingerprint"}:
            raise ChangelogError("Ongeldige ID-registratie in manifest")
        if not isinstance(record["version"], str) or not VERSION_RE.fullmatch(record["version"]):
            raise ChangelogError("Ongeldig versieveld in ID-registratie")
        if not isinstance(record["fingerprint"], str) or not re.fullmatch(r"[0-9a-f]{64}", record["fingerprint"]):
            raise ChangelogError("Ongeldige fingerprint in ID-registratie")
    return manifest


def _validate_proof(proof: Any) -> None:
    if not isinstance(proof, dict) or set(proof) != {"changelog_sha256", "manifest_sha256", "inputs"}:
        raise ChangelogError("Ongeldig releasebewijs in manifest")
    if not isinstance(proof["inputs"], dict):
        raise ChangelogError("Ongeldige fragmentregistratie in releasebewijs")
    hashes = [proof["changelog_sha256"], proof["manifest_sha256"], *proof["inputs"].values()]
    if any(not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value) for value in hashes):
        raise ChangelogError("Ongeldige SHA256 in releasebewijs")


def _manifest_text(manifest: dict) -> str:
    return json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"


def _sha256(content: str) -> str:
    return hashlib.sha256(content.encode("utf-8")).hexdigest()


def _prepared(repo: Path, manifest: dict, version: str, date: str, inputs: dict) -> dict | None:
    matches = [entry for entry in manifest["releases"] if entry["version"] == version]
    if not matches:
        return None
    if len(matches) != 1 or matches[0]["date"] != date or "proof" not in matches[0]:
        raise ChangelogError("Bestaande release mist exact herstelbewijs of heeft een andere datum")
    proof = matches[0]["proof"]
    source = (repo / CHANGELOG).read_bytes().decode("utf-8")
    manifest_source = (repo / MANIFEST).read_bytes().decode("utf-8")
    unsigned = json.loads(json.dumps(manifest))
    next(entry for entry in unsigned["releases"] if entry["version"] == version).pop("proof")
    if (proof["inputs"] != inputs or proof["changelog_sha256"] != _sha256(source)
            or proof["manifest_sha256"] != _sha256(_manifest_text(unsigned))
            or manifest_source != _manifest_text(manifest)):
        raise ChangelogError("Bestaande release wijkt af van het vastgelegde exacte releasebewijs")
    return {CHANGELOG: source, MANIFEST: manifest_source}


def _fingerprint(fragment: dict[str, Any]) -> str:
    fields = {key: fragment[key] for key in ("schema_version", "change_id", "issues", "category", "text", "release_impact")}
    canonical = json.dumps(fields, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def _render(fragments: list[dict[str, Any]], consumed: dict[str, dict[str, str]]) -> tuple[str, list[str], dict[str, dict[str, str]]]:
    entries: dict[str, list[dict[str, Any]]] = {category: [] for category in CATEGORIES}
    new_ids: list[str] = []
    registry = dict(consumed)
    for fragment in fragments:
        change_id = fragment["change_id"]
        origin = fragment["provenance"]["origin_release"]
        fingerprint = _fingerprint(fragment)
        existing = registry.get(change_id)
        if existing:
            if (origin is not None and origin != existing["version"]) or fingerprint != existing["fingerprint"]:
                raise ChangelogError(f"change_id {change_id} is eerder verwerkt met andere provenance of inhoud")
            continue
        new_ids.append(change_id)
        if origin:
            registry[change_id] = {"version": origin, "fingerprint": fingerprint}
            continue
        registry[change_id] = {"version": "", "fingerprint": fingerprint}
        if fragment["release_impact"] == "user_facing":
            issues = ", ".join(f"#{number}" for number in fragment["issues"])
            entries[fragment["category"]].append({**fragment, "issues_text": f"({issues})"})
    blocks = []
    for category in ("Added", "Changed", "Fixed", "Security", "Removed"):
        if entries[category]:
            lines = [f"### {category}"]
            lines.extend(f"- {entry['text']} {entry['issues_text']}" for entry in entries[category])
            blocks.append("\n".join(lines))
    return "\n\n".join(blocks), new_ids, registry


def _split_unreleased(changelog: str) -> tuple[str, str, str]:
    marker = "## [Unreleased]"
    if changelog.count(marker) != 1:
        raise ChangelogError("CHANGELOG.md moet precies één kop ## [Unreleased] bevatten")
    before, remainder = changelog.split(marker, 1)
    match = re.search(r"(?m)^## \[[^\]]+\].*$", remainder)
    body = remainder[:match.start()] if match else remainder
    tail = remainder[match.start():] if match else ""
    return before, body.strip(), tail


def _fragment_inputs(repo: Path) -> dict[str, str]:
    return {
        path.relative_to(repo).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted((repo / "changelog.d").glob("*.json"))
        if path.name not in {Path(MANIFEST).name, "schema-v1.json"}
    }


def _preview_result(updates: dict[str, str]) -> str:
    return updates[CHANGELOG] + "\n--- RELEASE MANIFEST ---\n" + updates[MANIFEST]


def validate(repo: Path) -> list[dict[str, Any]]:
    fragments = _load_fragments(repo)
    _load_manifest(repo)
    return fragments


def preview(repo: Path) -> str:
    fragments = validate(repo)
    manifest = _load_manifest(repo)
    rendered, _, _ = _render(fragments, manifest["changes"])
    return rendered or "(geen nieuwe gebruikersgerichte wijzigingen)"


def prepare(repo: Path, version: str, date: str, apply: bool) -> str:
    if not VERSION_RE.fullmatch(version):
        raise ChangelogError("Versie moet MAJOR.MINOR.PATCH.REVISION zijn")
    try:
        parsed_date = calendar_date.fromisoformat(date)
    except ValueError as exc:
        raise ChangelogError("Datum moet YYYY-MM-DD zijn")
    if parsed_date.isoformat() != date:
        raise ChangelogError("Datum moet YYYY-MM-DD zijn")
    identity = {"version": version, "date": date}
    inputs = _fragment_inputs(repo)
    restored = transaction.resume(repo, JOURNAL, identity, (CHANGELOG, MANIFEST), inputs, apply, _write_atomic)
    if restored is not None:
        return "Hersteljournaal verwerkt; exacte release-inhoud bevestigd." if apply else _preview_result(restored)
    fragments = validate(repo)
    manifest = _load_manifest(repo)
    prepared = _prepared(repo, manifest, version, date, inputs)
    if prepared is not None:
        return _preview_result(prepared)
    generated, new_ids, registry = _render(fragments, manifest["changes"])
    source = (repo / CHANGELOG).read_text(encoding="utf-8")
    if re.search(rf"(?m)^## \[{re.escape(version)}\]", source):
        raise ChangelogError("Versiekop bestaat al zonder exact herstelbewijs; hersteljournaal ontbreekt")
    before, legacy, tail = _split_unreleased(source)
    combined = "\n\n".join(part for part in (legacy, generated) if part)
    sections = []
    if combined:
        sections = [combined]
    next_changelog = f"{before}## [Unreleased]\n\n## [{version}] — {date}\n\n" + ("\n\n".join(sections) + "\n\n" if sections else "") + tail
    for record in registry.values():
        if not record["version"]:
            record["version"] = version
    manifest["changes"] = dict(sorted(registry.items()))
    this_release_ids = sorted(change_id for change_id in new_ids if registry[change_id]["version"] == version)
    manifest["releases"].append({"version": version, "date": date, "change_ids": this_release_ids})
    manifest["releases"].sort(key=lambda item: (item["date"], item["version"]))
    proof = {"changelog_sha256": _sha256(next_changelog),
             "manifest_sha256": _sha256(_manifest_text(manifest)), "inputs": inputs}
    next(entry for entry in manifest["releases"] if entry["version"] == version)["proof"] = proof
    manifest_text = _manifest_text(manifest)
    updates = {CHANGELOG: next_changelog, MANIFEST: manifest_text}
    if not apply:
        return _preview_result(updates)
    transaction.commit(repo, JOURNAL, identity, updates, inputs, _write_atomic)
    return f"Release {version} voorbereid; {len(new_ids)} change-ID's geregistreerd."


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate", "preview", "prepare"))
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--version")
    parser.add_argument("--date")
    parser.add_argument("--apply", action="store_true", help="Schrijf wijzigingen; prepare is standaard alleen preview")
    args = parser.parse_args(argv)
    try:
        repo = args.repo.resolve(strict=True)
        if args.command == "validate":
            fragments = validate(repo)
            print(f"OK — {len(fragments)} changelogfragment(en) gevalideerd.")
        elif args.command == "preview":
            print(preview(repo))
        else:
            if not args.version or not args.date:
                raise ChangelogError("prepare vereist --version en --date")
            print(prepare(repo, args.version, args.date, args.apply))
        return 0
    except (ChangelogError, transaction.TransactionError, OSError) as exc:
        print(f"Fout: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
