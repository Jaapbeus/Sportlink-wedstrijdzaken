#!/usr/bin/env python3
"""Maak herhaalbaar tree-bewijs; dit geeft nooit review- of mergegoedkeuring."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

PROJECTS = ('FunctionApp/fa-dev-sportlink-01.csproj',
            'FunctionApp.Postgres/FunctionApp.Postgres.csproj', 'BlazorAdmin/BlazorAdmin.csproj')
YAML = 'docs/api-standaarden/openapi.yaml'
JSON = 'docs/api-standaarden/openapi.json'
SHA = re.compile(r'^[a-f0-9]{40}$')
VERSION = re.compile(r'^\d+\.\d+\.\d+\.\d+$')


def git(root, *args, check=True):
    return subprocess.run(['git', '-C', str(root), *args], capture_output=True, check=check)


def oid(root, ref, kind='commit'):
    if not SHA.fullmatch(ref):
        raise ValueError('Alle referenties moeten volledige commit-SHAs zijn.')
    return git(root, 'rev-parse', '--verify', f'{ref}^{{{kind}}}').stdout.decode().strip()


def diff(root, left, right):
    return git(root, 'diff', '--no-ext-diff', '--no-textconv', '--binary',
               '--full-index', '--find-renames', left, right, '--').stdout


def blob(root, tree, path):
    return git(root, 'show', f'{tree}:{path}').stdout.decode('utf-8')


def normalize(path, text):
    if path in PROJECTS:
        for field in ('Version', 'AssemblyVersion', 'FileVersion'):
            pattern = f'(<{field}>)[^<]+(</{field}>)'
            if len(re.findall(pattern, text)) != 1:
                raise ValueError('Versieveld ontbreekt of is dubbel.')
            text = re.sub(pattern, r'\1<VERSION>\2', text)
        return text
    if path == YAML:
        if len(re.findall(r'^  version: .*$', text, flags=re.M)) != 1:
            raise ValueError('OpenAPI-versie ontbreekt of is dubbel.')
        return re.sub(r'^  version: .*$', '  version: <VERSION>', text, flags=re.M)
    data = json.loads(text)
    data['info']['version'] = '<VERSION>'
    return data


def version_only(root, expected, actual, paths):
    if not paths or any(p not in (*PROJECTS, YAML, JSON) for p in paths):
        return False
    for path in paths:
        # Modi, verwijderingen, symlinks en renames zijn geen versieveldwijzigingen.
        for tree in (expected, actual):
            entry = git(root, 'ls-tree', tree, '--', path).stdout.decode()
            if not entry.startswith('100644 blob '):
                return False
        if normalize(path, blob(root, expected, path)) != normalize(path, blob(root, actual, path)):
            return False
    versions = []
    for path in PROJECTS:
        text = blob(root, actual, path)
        for field in ('Version', 'AssemblyVersion', 'FileVersion'):
            found = re.findall(f'<{field}>([^<]+)</{field}>', text)
            if len(found) != 1:
                return False
            versions.extend(found)
    versions.append(json.loads(blob(root, actual, JSON))['info']['version'])
    versions.extend(re.findall(r'^  version: ([0-9.]+)\s*$', blob(root, actual, YAML), re.M))
    return len(versions) == 11 and len(set(versions)) == 1 and bool(VERSION.fullmatch(versions[0]))


def evidence(root, old_base, reviewed_head, new_base, candidate_head):
    refs = dict(zip(('old_base', 'reviewed_head', 'new_base', 'candidate_head'),
                    (old_base, reviewed_head, new_base, candidate_head)))
    for ref in refs.values():
        oid(root, ref)
    for base, head in ((old_base, reviewed_head), (new_base, candidate_head), (old_base, new_base)):
        if git(root, 'merge-base', '--is-ancestor', base, head, check=False).returncode:
            raise ValueError('Basisrelatie is niet bewezen; gewone review vereist.')
    merged = git(root, 'merge-tree', '--write-tree', new_base, reviewed_head, check=False)
    if merged.returncode not in (0, 1):
        raise ValueError('De verwachte integratietree kon niet worden berekend.')
    expected = merged.stdout.decode().splitlines()[0]
    if not SHA.fullmatch(expected):
        raise ValueError('Ongeldige integratietree.')
    actual = oid(root, candidate_head, 'tree')
    paths = git(root, 'diff', '--name-only', '-z', expected, actual, '--').stdout.decode().split('\0')
    paths = [p for p in paths if p]
    classification = 'inhoudelijke-of-handmatige-beoordeling'
    if merged.returncode == 0:
        if expected == actual:
            classification = 'exacte-integratietree'
        elif version_only(root, expected, actual, paths):
            classification = 'uitsluitend-gesynchroniseerde-versievelden'
    return { 'protocol_version': 1, **refs, 'expected_tree': expected,
             'candidate_tree': actual, 'merge_conflicts': merged.returncode == 1,
             'classification': classification, 'difference_paths': paths,
             'ordered_candidate_diff_sha256': hashlib.sha256(diff(root, expected, actual)).hexdigest(),
             'ordered_inherited_diff_sha256': hashlib.sha256(diff(root, old_base, new_base)).hexdigest(),
             'inherited_commits': git(root, 'rev-list', '--reverse', f'{old_base}..{new_base}').stdout.decode().splitlines(),
             'independent_interaction_review_required': True, 'merge_authorized': False }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True, type=Path)
    for arg in ('old-base', 'reviewed-head', 'new-base', 'candidate-head'):
        parser.add_argument('--' + arg, required=True)
    args = parser.parse_args()
    try:
        result = evidence(args.repo, args.old_base, args.reviewed_head, args.new_base, args.candidate_head)
    except (ValueError, KeyError, OSError, UnicodeError, subprocess.SubprocessError):
        print('Integratiebewijs onvolledig; geen goedkeuring. Controleer referenties en bronbestanden.')
        return 1
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
