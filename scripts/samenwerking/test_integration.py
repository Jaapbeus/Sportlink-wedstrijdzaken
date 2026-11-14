"""Real commit-graph acceptance for bounded integration and independent attestation."""
from pathlib import Path
import subprocess
import unittest

from . import test_coordinator as baseline
from .integration import digest
from .store import StoreError


class IntegrationTests(unittest.TestCase):
    setUp = baseline.CoordinatorTests.setUp
    tearDown = baseline.CoordinatorTests.tearDown
    _request = baseline.CoordinatorTests._request
    _identity = baseline.CoordinatorTests._identity
    _verify_task = baseline.CoordinatorTests._verify_task
    _claim_and_check = baseline.CoordinatorTests._claim_and_check

    def git(self, *args, check=True):
        result = subprocess.run(['git', '-C', str(self.repo), *args], capture_output=True, check=check)
        return result.stdout.decode().strip()

    def commit(self, path, text):
        (self.repo / path).write_text(text)
        self.git('add', path)
        self.git('commit', '-qm', 'isolated fixture')
        return self.git('rev-parse', 'HEAD')

    def graph(self, conflict=False):
        self.repo = Path(self.temp.name) / 'git-fixture'
        self.repo.mkdir()
        self.git('init', '-q')
        self.git('config', 'user.name', 'Jan de Vries')
        self.git('config', 'user.email', 'trainer@voorbeeld.nl')
        old = self.commit('base.txt', 'base\n')
        reviewed = self.commit('base.txt' if conflict else 'feature.txt', 'feature behavior\n')
        self.git('checkout', '-q', '--detach', old)
        new = self.commit('base.txt' if conflict else 'inherited.txt', 'shared contract change\n')
        self.git('merge', '--no-edit', reviewed, check=not conflict)
        if conflict:
            self.commit('base.txt', 'manual resolution\n')
        candidate = self.git('rev-parse', 'HEAD')
        return old, reviewed, new, candidate

    def source(self, old, reviewed):
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        self.coordinator.ready(self.identity)
        data = self._request(request_id='source', base_sha=old, head_sha=reviewed, merge_base_sha=old)
        self.coordinator.create_request(data)
        identity = self._identity(request_id='source', base_sha=old, head_sha=reviewed)
        self.coordinator.claim(identity)
        self._verify_task(identity, 'verify-source')
        self.coordinator.record_check(identity, {'check_name': 'CI', 'head_sha': reviewed, 'state': 'success', 'evidence': 'source run'})
        self.coordinator.finish(identity)
        self.coordinator.ready(identity)
        return identity

    def candidate(self, source='source', request_id='integration', **overrides):
        data = self._request(request_id=request_id, kind='integration', source_request_id=source,
                             repository_path=str(self.repo), base_sha=self.new, head_sha=self.candidate_sha,
                             merge_base_sha=self.new, **overrides)
        self.coordinator.create_request(data)
        return self._identity(request_id=request_id, base_sha=self.new, head_sha=self.candidate_sha)

    def attest(self, identity, **overrides):
        evidence = self.coordinator.status(identity['request_id'])['integration']['evidence']['evidence']
        data = {'evidence_sha256': digest(evidence), 'inherited_changes_reviewed': True,
                'assessment': 'enumerated inherited commits; shared contract callers checked',
                'refs': {key: evidence[key] for key in
                         ('old_base', 'reviewed_head', 'new_base', 'candidate_head', 'expected_tree', 'candidate_tree')}}
        data.update(overrides)
        return self.coordinator.attest_integration(identity, data)

    def complete(self, identity):
        self.coordinator.claim(identity)
        self._verify_task(identity, 'verify-' + identity['request_id'])
        self.coordinator.record_check(identity, {'check_name': 'CI', 'head_sha': identity['head_sha'], 'state': 'success', 'evidence': 'candidate run'})

    def test_preserved_contribution_requires_current_independent_interaction_review(self):
        old, reviewed, self.new, self.candidate_sha = self.graph()
        source = self.source(old, reviewed)
        identity = self.candidate()
        self.complete(identity)
        with self.assertRaisesRegex(StoreError, 'interactieattest'):
            self.coordinator.finish(identity)
        with self.assertRaises(StoreError):
            self.attest(identity | {'session': 'implementer-1'})
        with self.assertRaises(StoreError):
            self.attest(identity, inherited_changes_reviewed=False)
        with self.assertRaises(StoreError):
            self.attest(identity, refs={})
        self.attest(identity)
        self.coordinator.finish(identity)
        self.assertEqual(self.coordinator.ready(identity)['state'], 'ready')
        self.coordinator.record_finding(source, {'finding_id': 'source-late', 'severity': 'P2', 'evidence': 'late source defect'})
        with self.assertRaises(StoreError):
            self.coordinator.action_intent(identity, {'action_id': 'a', 'idempotency_key': 'a', 'action_kind': 'publish', 'target': 'PR', 'intent': 'publish'})

    def test_manual_contribution_change_and_conflicts_cannot_be_integration(self):
        old, reviewed, self.new, self.candidate_sha = self.graph()
        self.source(old, reviewed)
        self.candidate_sha = self.commit('feature.txt', 'changed behavior\n')
        with self.assertRaisesRegex(StoreError, 'inhoudelijke review'):
            self.candidate()
        self.assertEqual(self.db.execute('SELECT integration_attempts FROM counters').fetchone()[0], 0)

    def test_old_clean_review_cannot_skip_a_newer_substantive_request(self):
        old, reviewed, self.new, self.candidate_sha = self.graph()
        self.source(old, reviewed)
        self.coordinator.create_request(self._request(request_id='new-substantive', head_sha='e' * 40))
        with self.assertRaisesRegex(StoreError, 'actieve'):
            self.candidate()

    def test_integration_counters_do_not_reset_on_new_source(self):
        old, reviewed, self.new, self.candidate_sha = self.graph()
        self.source(old, reviewed)
        first = self.candidate()
        self.complete(first)
        self.attest(first)
        self.coordinator.finish(first)
        self.coordinator.ready(first)
        self.git('checkout', '-q', '--detach', self.new)
        self.new = self.commit('inherited-2.txt', 'second base change\n')
        self.git('merge', '--no-edit', self.candidate_sha)
        self.candidate_sha = self.git('rev-parse', 'HEAD')
        second = self.candidate(source='integration', request_id='integration-2')
        self.complete(second)
        self.attest(second)
        self.coordinator.finish(second)
        self.coordinator.ready(second)
        with self.assertRaisesRegex(StoreError, 'twee integratiepogingen'):
            self.candidate(source='integration-2', request_id='integration-3')
        counters = self.db.execute('SELECT * FROM counters').fetchone()
        self.assertEqual(counters['substantive_rounds'], 2)
        self.assertEqual(counters['integration_attempts'], 2)

    def test_manually_resolved_conflict_requires_substantive_round(self):
        old, reviewed, self.new, self.candidate_sha = self.graph(conflict=True)
        self.source(old, reviewed)
        with self.assertRaisesRegex(StoreError, 'inhoudelijke review'):
            self.candidate()
