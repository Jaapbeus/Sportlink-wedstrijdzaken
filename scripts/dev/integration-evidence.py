#!/usr/bin/env python3
"""Maak herhaalbaar tree-bewijs; dit geeft nooit review- of mergegoedkeuring."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

PROJECTS = ('FunctionApp/fa-dev-sportlink-01.csproj',
            'FunctionApp.Postgres/FunctionApp.Postgres.csproj', 'BlazorAdmin/BlazorAdmin.csproj')
YAML = 'docs/api-standaarden/openapi.yaml'
JSON = 'docs/api-standaarden/openapi.json'
SHA = re.compile(r'^[a-f0-9]{40}$')
VERSION = re.compile(r'^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$')


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


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Dubbele JSON-sleutel; gewone review vereist.')
        result[key] = value
    return result


def json_members(text, start=0):
    """Geef exacte waardespans zonder opnieuw serialiseren of tekst te verliezen."""
    decoder = json.JSONDecoder()
    pos = start + 1
    while True:
        pos = re.compile(r'\s*').match(text, pos).end()
        if text[pos] == '}':
            return
        key, pos = decoder.raw_decode(text, pos)
        pos = re.compile(r'\s*:\s*').match(text, pos).end()
        begin = pos
        _, pos = decoder.raw_decode(text, pos)
        yield key, begin, pos
        pos = re.compile(r'\s*').match(text, pos).end()
        if text[pos] == '}':
            return
        pos += 1  # De volledige JSON is vooraf gevalideerd, dus dit is een komma.


def invalid_json_constant(_):
    raise ValueError('Niet-standaard JSON-constante.')


def json_version(text):
    data = json.loads(text, object_pairs_hook=unique_object, parse_constant=invalid_json_constant)
    if not isinstance(data, dict) or not isinstance(data.get('info'), dict):
        raise ValueError('OpenAPI-info ontbreekt of is ongeldig.')
    version = data['info'].get('version')
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        raise ValueError('OpenAPI-versie is ongeldig.')
    info_start = next(start for key, start, _ in json_members(text, len(text) - len(text.lstrip()))
                      if key == 'info')
    begin, end = next((start, end) for key, start, end in json_members(text, info_start)
                      if key == 'version')
    return text[:begin] + '"<VERSION>"' + text[end:], [version]


def yaml_version(text):
    # Alleen de ondubbelzinnige blokvorm wordt ondersteund; geen YAML-parser raden.
    lines = text.splitlines(keepends=True)
    root_keys, info_keys, versions = set(), set(), []
    in_info = False
    block_indent = None
    for index, line in enumerate(lines):
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        indent = len(line) - len(line.lstrip(' '))
        if block_indent is not None:
            if indent > block_indent:
                continue
            block_indent = None
        if in_info or 'info' not in root_keys:
            # Ook geneste quotes/flow kunnen schijnbare info/version-regels opslokken.
            scalar = re.match(r'^ *[^:#]+: +([^\r\n]*)', line)
            if scalar and scalar[1] and scalar[1][0] in '\"\'[{&*!':
                raise ValueError('Niet-ondersteunde scalar rond de OpenAPI-info-scope.')
            if scalar and re.fullmatch(r'[>|][1-9+-]{0,2}(?: +#.*)?', scalar[1]):
                block_indent = indent
        if not line[0].isspace():
            match = re.fullmatch(r'([A-Za-z][A-Za-z0-9_-]*):(?: ([^\r\n]*))?\r?\n?', line)
            if not match or match[1] in root_keys:
                raise ValueError('OpenAPI-root is dubbel of heeft een niet-ondersteunde vorm.')
            root_keys.add(match[1])
            in_info = match[1] == 'info'
            if in_info and match[2] is not None:
                raise ValueError('OpenAPI-info moet een eenvoudige blokmapping zijn.')
            # Een meerregelige quoted/flow-scalar kan schijnbare rootvelden bevatten.
            if match[2] and match[2][0] in '\"\'[{&*!':
                raise ValueError('OpenAPI-root heeft een niet-ondersteunde scalar.')
        elif in_info and not line.startswith('   '):
            match = re.fullmatch(r'  ([A-Za-z][A-Za-z0-9_-]*):(?: ([^\r\n]*))?\r?\n?', line)
            if not match or match[1] in info_keys:
                raise ValueError('OpenAPI-info is dubbel of heeft een niet-ondersteunde vorm.')
            info_keys.add(match[1])
            if match[1] == 'version':
                version = match[2]
                if not version or not VERSION.fullmatch(version):
                    raise ValueError('OpenAPI-versie is ongeldig.')
                versions.append(version)
                lines[index] = line.replace(version, '<VERSION>', 1)
            elif match[2] and match[2][0] in '\"\'[{&*!':
                raise ValueError('OpenAPI-info heeft een niet-ondersteunde scalar.')
    if 'info' not in root_keys or len(versions) != 1:
        raise ValueError('OpenAPI-versie ontbreekt of is dubbel.')
    return ''.join(lines), versions


def project_versions(text):
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        raise ValueError('Ongeldige project-XML.') from error
    if root.tag != 'Project' or '<!DOCTYPE' in text:
        raise ValueError('Niet-ondersteunde project-XML.')
    versions = []
    for field in ('Version', 'AssemblyVersion', 'FileVersion'):
        elements = list(root.iter(field))
        matches = list(re.finditer(f'(<{field}>)([^<]+)(</{field}>)', text))
        if len(elements) != 1 or len(matches) != 1:
            raise ValueError('Versieveld ontbreekt, is dubbel of bevat commentaar.')
        element = elements[0]
        groups = [group for group in root.findall('PropertyGroup') if element in list(group)]
        if (len(groups) != 1 or groups[0].attrib or element.attrib or len(element)
                or not VERSION.fullmatch(element.text or '') or matches[0][2] != element.text):
            raise ValueError('Versieveld heeft geen onvoorwaardelijke scalaire projectscope.')
        versions.append(element.text)
        text = text[:matches[0].start(2)] + '<VERSION>' + text[matches[0].end(2):]
    return text, versions


def version_document(path, text):
    if path in PROJECTS:
        return project_versions(text)
    if path == YAML:
        return yaml_version(text)
    return json_version(text)


def normalize(path, text):
    return version_document(path, text)[0]


def version_only(root, expected, actual, paths):
    if not paths or any(p not in (*PROJECTS, YAML, JSON) for p in paths):
        return False
    try:
        for path in paths:
            # Modi, verwijderingen, symlinks en renames zijn geen versieveldwijzigingen.
            for tree in (expected, actual):
                entry = git(root, 'ls-tree', tree, '--', path).stdout.decode()
                if not entry.startswith('100644 blob '):
                    return False
            if normalize(path, blob(root, expected, path)) != normalize(path, blob(root, actual, path)):
                return False
        versions = []
        for path in (*PROJECTS, YAML, JSON):
            versions.extend(version_document(path, blob(root, actual, path))[1])
        return len(versions) == 11 and len(set(versions)) == 1
    except (ValueError, KeyError, TypeError, StopIteration, IndexError):
        return False  # Onzekere structuur kan nooit een bewijsvrijstelling krijgen.


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
