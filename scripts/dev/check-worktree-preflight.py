#!/usr/bin/env python3
"""Controleer worktree-isolatie en lokale hookconfiguratie zonder waarden te tonen."""
import argparse
import os
from pathlib import Path
import subprocess


def git(root, *args):
    return subprocess.run(['git', '-C', str(root), *args], check=True,
                          capture_output=True, text=True).stdout.strip()


def inspect(root, branch):
    if not root.is_absolute():
        return ['Het worktreepad moet absoluut zijn.']
    root = root.resolve()
    errors = []
    if Path(git(root, 'rev-parse', '--show-toplevel')).resolve() != root:
        errors.append('Het opgegeven pad is niet de worktree-root.')
    if git(root, 'branch', '--show-current') != branch:
        errors.append('De worktree staat niet op de toegewezen branch.')
    entries = git(root, 'worktree', 'list', '--porcelain').split('\n\n')
    matching = [entry for entry in entries if f'branch refs/heads/{branch}' in entry.splitlines()]
    if len(matching) != 1 or matching[0].splitlines()[0] != f'worktree {root}':
        errors.append('De branch heeft geen eenduidige worktree-registratie.')
    if git(root, 'config', '--get', 'core.hooksPath') != '.githooks':
        errors.append('De verplichte hookmap is niet ingesteld.')
    for name in ('pre-commit', 'pre-push'):
        if not os.access(root / '.githooks' / name, os.X_OK):
            errors.append('Een verplichte hook ontbreekt of is niet uitvoerbaar.')
    config = root / '.githooks' / 'sensitive-patterns.txt'
    if not config.is_file() or config.is_symlink():
        errors.append('De lokale scanconfiguratie ontbreekt of is een symlink.')
        return errors
    ignored = subprocess.run(['git', '-C', str(root), 'check-ignore', '-q', str(config)])
    if ignored.returncode != 0:
        errors.append('De lokale scanconfiguratie is niet gitignored.')
    patterns = [line for line in config.read_text().splitlines()
                if line.strip() and not line.lstrip().startswith('#')]
    if not patterns:
        errors.append('De lokale scanconfiguratie bevat geen actieve regels.')
    else:
        # Patrooninhoud gaat uitsluitend via stdin, nooit via argv, een tijdelijk bestand of uitvoer.
        checked = subprocess.run(['grep', '-E', '-f', '-', os.devnull],
                                 input='\n'.join(patterns) + '\n', text=True,
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if checked.returncode not in (0, 1):
            errors.append('De lokale scanconfiguratie bevat een ongeldige regel.')
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worktree', required=True, type=Path)
    parser.add_argument('--branch', required=True)
    args = parser.parse_args()
    try:
        errors = inspect(args.worktree, args.branch)
    except (OSError, UnicodeError, subprocess.SubprocessError):
        errors = ['Worktree of lokale scanconfiguratie kon niet veilig worden gecontroleerd.']
    for error in errors:
        print(error)
    if not errors:
        print('Worktree, uitvoerbare hooks en genegeerde scanconfiguratie gecontroleerd.')
    return int(bool(errors))


if __name__ == '__main__':
    raise SystemExit(main())
