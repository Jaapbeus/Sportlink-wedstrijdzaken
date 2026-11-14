"""Geïsoleerde git-fixtures voor integratiebewijs en worktree-preflight."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


proof = load('proof', 'integration-evidence.py')
preflight = load('preflight', 'check-worktree-preflight.py')


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name)
        self.git('init', '-q', '-b', 'main')
        self.git('config', 'user.name', 'Test')
        self.git('config', 'user.email', 'trainer@voorbeeld.nl')
        self.write('app.txt', 'basis\n')
        for path in proof.PROJECTS:
            self.write(path, '<Project><Version>1.0.0.0</Version><AssemblyVersion>1.0.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion></Project>\n')
        self.write(proof.YAML, 'openapi: 3.0.0\ninfo:\n  version: 1.0.0.0\n  title: Test\n')
        self.write(proof.JSON, json.dumps({'openapi': '3.0.0', 'info': {'version': '1.0.0.0', 'title': 'Test'}}))
        self.base = self.commit()
        self.git('checkout', '-q', '-b', 'feature')
        self.write('app.txt', 'een\ntwee\n')
        self.reviewed = self.commit()
        self.git('checkout', '-q', 'main')
        self.write('base.txt', 'nieuwe basis\n')
        self.new_base = self.commit()
        self.git('checkout', '-q', 'feature')
        self.git('merge', '-q', '--no-edit', 'main')
        self.candidate = self.git('rev-parse', 'HEAD')

    def git(self, *args):
        return subprocess.run(['git', '-C', str(self.repo), *args], check=True,
                              capture_output=True, text=True).stdout.strip()

    def write(self, name, text):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def commit(self):
        self.git('add', '--all')
        self.git('commit', '-qm', 'fixture')
        return self.git('rev-parse', 'HEAD')

    def result(self):
        return proof.evidence(self.repo, self.base, self.reviewed, self.new_base, self.candidate)

    def test_exact_tree_still_requires_interaction_review(self):
        result = self.result()
        self.assertEqual(result['classification'], 'exacte-integratietree')
        self.assertTrue(result['independent_interaction_review_required'])
        self.assertFalse(result['merge_authorized'])
        self.assertEqual(result['inherited_commits'], [self.new_base])

    def test_order_change_not_hidden_by_sorted_lines(self):
        self.write('app.txt', 'twee\neen\n')
        self.candidate = self.commit()
        self.assertEqual(self.result()['classification'], 'inhoudelijke-of-handmatige-beoordeling')

    def test_mode_change_is_substantive(self):
        self.git('update-index', '--chmod=+x', 'app.txt')
        self.git('commit', '-qm', 'mode')
        self.candidate = self.git('rev-parse', 'HEAD')
        self.assertIn('app.txt', self.result()['difference_paths'])

    def test_only_all_synchronized_version_fields(self):
        for path in (*proof.PROJECTS, proof.YAML, proof.JSON):
            file = self.repo / path
            file.write_text(file.read_text().replace('1.0.0.0', '1.0.0.1'))
        self.candidate = self.commit()
        self.assertEqual(self.result()['classification'], 'uitsluitend-gesynchroniseerde-versievelden')
        self.write(proof.PROJECTS[0], (self.repo / proof.PROJECTS[0]).read_text().replace('1.0.0.1', '1.0.0.2'))
        self.candidate = self.commit()
        self.assertEqual(self.result()['classification'], 'inhoudelijke-of-handmatige-beoordeling')

    def test_csproj_dependency_is_not_metadata(self):
        file = self.repo / proof.PROJECTS[0]
        file.write_text(file.read_text().replace('</Project>', '<PackageReference Include="Test" Version="2" /></Project>'))
        self.candidate = self.commit()
        self.assertEqual(self.result()['classification'], 'inhoudelijke-of-handmatige-beoordeling')

    def test_short_sha_and_wrong_base_rejected(self):
        with self.assertRaises(ValueError):
            proof.evidence(self.repo, self.base[:8], self.reviewed, self.new_base, self.candidate)
        with self.assertRaises(ValueError):
            proof.evidence(self.repo, self.new_base, self.reviewed, self.new_base, self.candidate)

    def test_text_conflict_needs_manual_review(self):
        self.git('checkout', '-q', 'main')
        self.write('app.txt', 'botsing\n')
        self.new_base = self.commit()
        self.git('checkout', '-q', 'feature')
        subprocess.run(['git', '-C', str(self.repo), 'merge', 'main'], capture_output=True)
        self.write('app.txt', 'samengevoegd\n')
        self.candidate = self.commit()
        result = self.result()
        self.assertTrue(result['merge_conflicts'])
        self.assertEqual(result['classification'], 'inhoudelijke-of-handmatige-beoordeling')

    def test_preflight_missing_invalid_empty_and_valid_patterns(self):
        self.git('config', 'core.hooksPath', '.githooks')
        for name in ('pre-commit', 'pre-push'):
            self.write('.githooks/' + name, '#!/bin/sh\nexit 0\n')
            os.chmod(self.repo / '.githooks' / name, 0o755)
        self.write('.gitignore', '.githooks/sensitive-patterns.txt\n')
        self.assertTrue(preflight.inspect(self.repo, 'feature'))
        self.write('.githooks/sensitive-patterns.txt', '# leeg\n')
        self.assertTrue(preflight.inspect(self.repo, 'feature'))
        self.write('.githooks/sensitive-patterns.txt', '(\n')
        self.assertTrue(preflight.inspect(self.repo, 'feature'))
        self.write('.githooks/sensitive-patterns.txt', 'synthetisch-patroon\n')
        self.assertEqual(preflight.inspect(self.repo, 'feature'), [])
        self.assertTrue(preflight.inspect(self.repo, 'andere-branch'))
        self.assertTrue(preflight.inspect(Path('.'), 'feature'))


if __name__ == '__main__':
    unittest.main()
