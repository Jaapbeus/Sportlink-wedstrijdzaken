#!/usr/bin/env python3
"""check-api-md-sync.py (#1717)

AGENTS.md: een endpoint-wijziging werkt in dezelfde commit openapi.yaml, openapi.json en
docs/API.md bij. Deze guard vergelijkt de set (methode, pad) in docs/api-standaarden/openapi.json
met de endpointtabel in docs/API.md (kolom 1: methode(s), gescheiden door '/'; kolom 2: pad).
Een operatie die in de spec staat maar niet in API.md, of omgekeerd, laat de build falen.

Bewust alleen de operatieset: de beschrijvingen zijn handgeschreven tekst. Pure standaardbibliotheek
(geen PyYAML nodig): de JSON is uit de YAML gegenereerd en check-versie-synchroon.sh bewaakt dat ze
dezelfde versie hebben.
"""
import json
import pathlib
import re
import subprocess
import sys

METHODEN = {"get", "post", "put", "delete", "patch"}


def norm(pad: str) -> str:
    return re.sub(r"\{[^}]*\}", "{}", pad.split("?")[0])


def spec_operaties(tekst: str) -> set:
    spec = json.loads(tekst)
    return {(m.upper(), norm(p)) for p, ops in spec["paths"].items() for m in ops if m in METHODEN}


def doc_operaties(tekst: str) -> set:
    gevonden = set()
    for regel in tekst.splitlines():
        m = re.match(r"\|\s*`([A-Z/]+)`\s*\|\s*`([^`]+)`", regel)
        if m:
            for methode in m.group(1).split("/"):
                gevonden.add((methode, norm(m.group(2))))
    return gevonden


def main() -> int:
    root = pathlib.Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    spec = spec_operaties((root / "docs/api-standaarden/openapi.json").read_text(encoding="utf-8"))
    doc = doc_operaties((root / "docs/API.md").read_text(encoding="utf-8"))
    if not spec or not doc:
        print("::error::Geen operaties gevonden in de spec of in docs/API.md; de guard kan niet meten.")
        return 1
    fout = False
    for methode, pad in sorted(spec - doc):
        print(f"::error file=docs/API.md::{methode} {pad} staat in openapi.json maar niet in de endpointtabel van docs/API.md.")
        fout = True
    for methode, pad in sorted(doc - spec):
        print(f"::error file=docs/API.md::{methode} {pad} staat in docs/API.md maar niet in openapi.json.")
        fout = True
    if fout:
        return 1
    print(f"OK - {len(spec)} operaties: openapi.json en de endpointtabel in docs/API.md komen overeen.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
