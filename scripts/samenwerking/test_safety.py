"""Negative acceptance cases for omitted work, stale children, grants and lane recovery."""
import sqlite3
import time
import unittest
from unittest.mock import patch

from . import test_coordinator as baseline
from .__main__ import dispatch
from .authority import FIELDS
from .store import StoreError, initialize


class SafetyTests(unittest.TestCase):
    setUp = baseline.CoordinatorTests.setUp
    tearDown = baseline.CoordinatorTests.tearDown
    _request = baseline.CoordinatorTests._request
    _identity = baseline.CoordinatorTests._identity
    _verify_task = baseline.CoordinatorTests._verify_task
    _claim_and_check = baseline.CoordinatorTests._claim_and_check

    def test_no_request_without_nonempty_checks_and_manifest(self):
        for changes in ({"required_checks": []}, {"mandatory_tasks": []}, {"required_checks": None},
                        {"mandatory_tasks": [{"task_id": "no-review", "purpose": "child only", "assigned_session": "child"}]}):
            with self.subTest(changes=changes), self.assertRaises(StoreError):
                self.coordinator.create_request(self._request(request_id="bad", **changes))
        self.assertEqual(self.db.execute("SELECT substantive_rounds FROM counters").fetchone()[0], 1)

    def test_claim_finish_without_actual_review_is_blocked(self):
        self.coordinator.claim(self.identity)
        self.coordinator.record_check(self.identity, {
            "check_name": "CI", "head_sha": baseline.HEAD, "state": "success", "evidence": "run",
        })
        with self.assertRaisesRegex(StoreError, "verplichte taken"):
            self.coordinator.finish(self.identity)
        with self.assertRaises(StoreError):
            self.coordinator.register_task(self.identity, {"task_id": "hidden", "classification": "mandatory"})
        with self.assertRaises(StoreError):
            self.coordinator.register_task(self.identity, {"task_id": "verify-request-1", "assigned_session": "child"})

    def _child(self, name="child", task_id="child-task"):
        self.coordinator.register_task(self.identity, {
            "task_id": task_id, "classification": "optional", "purpose": "extra verification", "assigned_session": name,
        })

    def test_child_output_needs_assigned_session_and_independent_acceptance(self):
        self._claim_and_check()
        self._child()
        wrong = self.coordinator.submit_task_output(self._identity(session="wrong-child"), {
            "task_id": "child-task", "evidence": "untrusted output", "result": "success",
        })
        self.assertFalse(wrong["eligible"])
        with self.assertRaisesRegex(StoreError, "Child-uitvoer"):
            self.coordinator.complete_task(self.identity, {"task_id": "child-task", "evidence": "reviewed", "result": "success", "output_event_id": wrong["event_id"]})
        output = self.coordinator.submit_task_output(self._identity(session="child"), {"task_id": "child-task", "evidence": "child result", "result": "success"})
        with self.assertRaises(StoreError):
            self.coordinator.finish(self.identity)
        with self.assertRaises(StoreError):
            self.coordinator.complete_task(self._identity(session="child"), {"task_id": "child-task", "evidence": "self accepted"})
        self.coordinator.complete_task(self.identity, {"task_id": "child-task", "evidence": "independently reproduced", "result": "success", "output_event_id": output["event_id"]})
        self.coordinator.finish(self.identity)
        self.assertEqual(self.coordinator.ready(self.identity)["state"], "ready")

    def test_stale_child_output_retained_but_cannot_complete_replacement(self):
        self._claim_and_check()
        self._child()
        output = self.coordinator.submit_task_output(self._identity(session="child"), {"task_id": "child-task", "evidence": "old result", "result": "success"})
        self.coordinator.reassign({"request_id": self.request_id, "old_actor_stopped": True,
                                   "new_reviewer_session": "reviewer-2", "stopped_sessions": ["child"], "reason": "confirmed all actors stopped"})
        late = self.coordinator.submit_task_output(self._identity(session="child"), {"task_id": "child-task", "evidence": "late old result", "result": "success"})
        self.assertFalse(late["eligible"])
        replacement = self._identity(session="reviewer-2", generation=2, attempt=2)
        self.coordinator.claim(replacement)
        self.coordinator.register_task(replacement, {"task_id": "child-task"})
        for event in (output, late):
            with self.assertRaises(StoreError):
                self.coordinator.complete_task(replacement, {"task_id": "child-task", "evidence": "accept", "result": "success", "output_event_id": event["event_id"]})
        self.assertEqual(len([x for x in self.coordinator.events(self.request_id) if x["event_type"] == "task_output_recorded"]), 2)

    def _finding_and_grant(self):
        self._claim_and_check()
        self.coordinator.record_finding(self.identity, {"finding_id": "f", "severity": "P2", "evidence": "reproduction"})
        request = self.coordinator.status(self.request_id)["request"]
        scope = {key: request[key] for key in FIELDS}
        scope.update(action="owner_accepted", resource_id="f", authorization_ref="owner decision")
        grant = {"grant_id": "grant-1", "grantor_session": "owner-1", "grantee_session": "owner-1",
                 "request_id": self.request_id, "scope": scope, "action": "owner_accepted", "resource_id": "f",
                 "expires_at": int(time.time()) + 1000, "authorization_ref": "owner decision"}
        return grant

    def _accept(self, **overrides):
        value = {"finding_id": "f", "disposition": "owner_accepted", "evidence": "accepted risk",
                 "authorization_ref": "owner decision", "grant_id": "grant-1"}
        value.update(overrides)
        return self.coordinator.disposition_finding(self._identity(session="owner-1"), value)

    def test_owner_role_and_arbitrary_reference_are_not_a_grant(self):
        grant = self._finding_and_grant()
        with self.assertRaisesRegex(StoreError, "grant"):
            self._accept(actor_role="owner")
        with self.assertRaises(StoreError):
            self.coordinator.grant(grant | {"grantor_session": "self-declared-owner"})
        with self.assertRaises(StoreError):
            self.coordinator.grant(grant | {"scope": grant["scope"] | {"head_sha": "d" * 40}})
        self.coordinator.grant(grant)
        for wrong in ({"authorization_ref": "other decision"}, {"finding_id": "other finding"}):
            with self.assertRaises(StoreError):
                self._accept(**wrong)
        self._accept()
        self.coordinator.finish(self.identity)
        self.assertEqual(self.coordinator.ready(self.identity)["state"], "ready")

    def test_grant_expiry_and_revocation_block_eligibility_and_actions(self):
        grant = self._finding_and_grant()
        self.coordinator.grant(grant)
        self._accept()
        self.coordinator.finish(self.identity)
        with patch('scripts.samenwerking.authority.time.time', return_value=grant["expires_at"] + 1):
            with self.assertRaises(StoreError):
                self.coordinator.ready(self.identity)
        self.coordinator.ready(self.identity)
        self.coordinator.revoke_grant({"grant_id": "grant-1", "grantor_session": "owner-1", "reason": "withdraw permission"})
        with self.assertRaises(StoreError):
            self.coordinator.action_intent(self.identity, {"action_id": "a", "idempotency_key": "a", "action_kind": "publish", "target": "pr", "intent": "publish"})
        self.assertEqual(self.coordinator.status(self.request_id)["request"]["state"], "invalidated")

    def test_withdrawal_needs_stop_fence_reconciliation_and_preserves_history(self):
        for candidate in ("first", "second"):
            self.coordinator.lane_enqueue({"candidate_id": candidate, "repository": "org/repo", "pr": 77, "dependencies": []})
        reserved = self.coordinator.lane_reserve({"candidate_id": "first", "actor_session": "integrator", "base_sha": baseline.BASE, "head_sha": baseline.HEAD})
        release = reserved | {"actor_session": "integrator", "outcome": "withdrawn", "reason": "stalled"}
        with self.assertRaisesRegex(StoreError, "bevestigde stop"):
            self.coordinator.lane_release(release)
        with self.assertRaisesRegex(StoreError, "al gereserveerd"):
            self.coordinator.lane_reserve({"candidate_id": "second", "actor_session": "other", "base_sha": baseline.BASE, "head_sha": baseline.HEAD})
        self.coordinator.lane_release(release | {"confirmed_stopped": True, "stop_evidence": "actor stopped", "fence_evidence": "controlled writes fenced", "reconciliation_evidence": "remote observed no pending writes"})
        row = self.db.execute("SELECT * FROM lane_entries WHERE candidate_id='first'").fetchone()
        self.assertEqual(row["generation"], reserved["generation"] + 1)
        self.assertTrue(any(x["event_type"] == "lane_reservation_released" for x in self.coordinator.events(None)))
        self.assertEqual(self.coordinator.lane_reserve({"candidate_id": "second", "actor_session": "other", "base_sha": baseline.BASE, "head_sha": baseline.HEAD})["state"], "reserved")

    def test_integration_label_alone_cannot_bypass_three_rounds(self):
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        for index, head in ((2, "d" * 40), (3, "e" * 40)):
            request_id = "round-" + str(index)
            self.coordinator.create_request(self._request(request_id=request_id, head_sha=head))
            identity = self._identity(request_id=request_id, head_sha=head)
            self.coordinator.claim(identity)
            self._verify_task(identity, "verify-" + request_id)
            self.coordinator.record_check(identity, {"check_name": "CI", "head_sha": head, "state": "success", "evidence": "run"})
            self.coordinator.finish(identity)
        with self.assertRaisesRegex(StoreError, "drie"):
            self.coordinator.create_request(self._request(request_id="round-4", head_sha="f" * 40))
        with self.assertRaisesRegex(StoreError, "eerdere schone review"):
            self.coordinator.create_request(self._request(request_id="fake-integration", kind="integration"))
        self.assertEqual(self.db.execute("SELECT integration_attempts FROM counters").fetchone()[0], 0)

    def test_legacy_store_is_not_silently_trusted(self):
        old = sqlite3.connect(':memory:', isolation_level=None)
        old.execute('PRAGMA user_version=1')
        with self.assertRaisesRegex(StoreError, 'handmatig'):
            initialize(old, 'test-coordinator', 'owner-1')
        self.assertEqual(old.execute("SELECT count(*) FROM sqlite_master").fetchone()[0], 0)
        old.close()

    def test_failed_child_output_and_missing_success_cannot_satisfy_completion(self):
        self._claim_and_check()
        self._child()
        failed = self.coordinator.submit_task_output(self._identity(session="child"),
            {"task_id": "child-task", "evidence": "verification failed", "result": "failure"})
        self.assertFalse(failed["eligible"])
        with self.assertRaises(StoreError):
            self.coordinator.complete_task(self.identity, {"task_id": "child-task", "evidence": "checked"})
        with self.assertRaises(StoreError):
            self.coordinator.complete_task(self.identity, {"task_id": "child-task", "evidence": "checked", "result": "success", "output_event_id": failed["event_id"]})
        with self.assertRaises(StoreError):
            self.coordinator.finish(self.identity)

    def test_pending_remote_action_blocks_withdrawal_until_reconciled(self):
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        self.coordinator.ready(self.identity)
        self.coordinator.action_intent(self.identity, {"action_id": "pending", "idempotency_key": "pending", "action_kind": "publish", "target": "PR", "intent": "publish"})
        self.coordinator.lane_enqueue({"candidate_id": "c", "repository": "org/repo", "pr": 77, "dependencies": []})
        reserved = self.coordinator.lane_reserve({"candidate_id": "c", "actor_session": "integrator", "base_sha": baseline.BASE, "head_sha": baseline.HEAD})
        release = reserved | {"actor_session": "integrator", "outcome": "withdrawn", "reason": "stop", "confirmed_stopped": True,
            "stop_evidence": "stopped", "fence_evidence": "controlled paths fenced", "reconciliation_evidence": "remote observed"}
        with self.assertRaisesRegex(StoreError, "eerst definitief"):
            self.coordinator.lane_release(release)
        self.coordinator.reconcile({"action_id": "pending", "outcome": "not_applied", "observed_reference": "none", "evidence": "remote absence observed"})
        self.assertEqual(self.coordinator.lane_release(release)["state"], "withdrawn")

    def test_reassignment_cannot_omit_an_active_child(self):
        self._claim_and_check()
        self._child()
        with self.assertRaisesRegex(StoreError, "alle actieve child"):
            self.coordinator.reassign({"request_id": self.request_id, "old_actor_stopped": True,
                "new_reviewer_session": "replacement", "reason": "reviewer stopped only"})

    def test_late_failed_child_output_invalidates_clean_result_and_retains_finding(self):
        self._claim_and_check()
        self._child()
        output = self.coordinator.submit_task_output(self._identity(session="child"),
            {"task_id": "child-task", "evidence": "success", "result": "success"})
        self.coordinator.complete_task(self.identity, {"task_id": "child-task", "evidence": "independent check", "result": "success", "output_event_id": output["event_id"]})
        self.coordinator.finish(self.identity)
        self.coordinator.ready(self.identity)
        self.coordinator.submit_task_output(self._identity(session="child"),
            {"task_id": "child-task", "evidence": "late missed failure", "result": "failure"})
        status = self.coordinator.status(self.request_id)
        self.assertEqual(status["request"]["state"], "invalidated")
        self.assertEqual(status["findings"][0]["severity"], "untriaged")

    def test_failed_review_outcome_cannot_become_ready(self):
        self._claim_and_check()
        with self.assertRaisesRegex(StoreError, "reviewuitkomst"):
            self.coordinator.finish(self.identity, {"result": "failed"})
        with self.assertRaises(StoreError):
            self.coordinator.ready(self.identity)

    def test_distinct_request_ids_cannot_create_parallel_reviewers_for_one_pr(self):
        with self.assertRaisesRegex(StoreError, "actieve reviewaanvraag"):
            self.coordinator.create_request(self._request(request_id="duplicate", reviewer_session="reviewer-2"))
        with self.assertRaisesRegex(StoreError, "actieve reviewaanvraag"):
            self.coordinator.create_request(self._request(request_id="other-head", head_sha="d" * 40))
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        with self.assertRaisesRegex(StoreError, "SHA heeft al"):
            self.coordinator.create_request(self._request(request_id="duplicate-returned", reviewer_session="reviewer-2"))
        self.assertEqual(self.db.execute("SELECT substantive_rounds FROM counters").fetchone()[0], 1)

    def test_sha_canonicalization_cannot_hide_duplicate_review(self):
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        with self.assertRaisesRegex(StoreError, "SHA"):
            self.coordinator.create_request(self._request(request_id="uppercase", head_sha=baseline.HEAD.upper()))

    def _next_review(self, head="d" * 40):
        self.coordinator.create_request(self._request(request_id="r2", head_sha=head))
        identity = self._identity(request_id="r2", head_sha=head)
        self.coordinator.claim(identity)
        self._verify_task(identity, "verify-r2")
        self.coordinator.record_check(identity, {
            "check_name": "CI", "head_sha": head, "state": "success", "evidence": "run/2"})
        return identity

    def test_older_request_finding_invalidates_new_ready_and_action_boundary(self):
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        newer = self._next_review()
        self.coordinator.finish(newer, {"result": "clean"})
        self.coordinator.ready(newer)
        self.coordinator.record_finding(self.identity, {
            "finding_id": "late", "severity": "P1", "evidence": "old round result"})
        self.assertEqual(self.coordinator.status("r2")["request"]["state"], "invalidated")
        action = {"action_id": "a", "idempotency_key": "a", "action_kind": "publish",
                  "target": "PR", "intent": "publish"}
        with self.assertRaises(StoreError):
            self.coordinator.action_intent(newer, action)
        # Simulate a stale projection from an older coordinator: the final boundary scans the ledger.
        self.db.execute("UPDATE requests SET state='ready' WHERE request_id='r2'")
        with self.assertRaisesRegex(StoreError, "bevindingen open"):
            self.coordinator.action_intent(newer, action)
        self.assertEqual(self.db.execute("SELECT COUNT(*) FROM action_intents").fetchone()[0], 0)

    def test_blocked_return_preserves_work_fences_actor_and_allows_new_head(self):
        self.coordinator.claim(self.identity)
        self._child()
        self.coordinator.record_finding(self.identity, {
            "finding_id": "f", "severity": "P2", "evidence": "CI investigation"})
        blocked = {"result": "blocked", "reason": "CI missing or red", "reviewer_stopped": True,
                   "stop_evidence": "reviewer and child stopped", "fence_evidence": "writers fenced"}
        before = self.coordinator.status(self.request_id)
        for changes in ({"reviewer_stopped": False}, {"stop_evidence": ""}, {"fence_evidence": ""}, {}):
            with self.assertRaises(StoreError):
                self.coordinator.finish(self.identity, blocked | changes)
        dispatch("finish", {"identity": self.identity,
                            "result": blocked | {"stopped_sessions": ["child"]}}, self.db)
        after = self.coordinator.status(self.request_id)
        self.assertEqual(after["tasks"], before["tasks"])
        self.assertEqual(after["findings"], before["findings"])
        self.assertEqual(after["checks"], before["checks"])
        with self.assertRaisesRegex(StoreError, "verouderd"):
            self.coordinator.register_task(self.identity, {"task_id": "verify-request-1"})
        with self.assertRaises(StoreError):
            self.coordinator.ready(self._identity(generation=2))
        newer = self._next_review()
        with self.assertRaisesRegex(StoreError, "bevindingen open"):
            self.coordinator.finish(newer, {"result": "clean"})

    def test_red_check_allows_explicit_blocked_return_only(self):
        self._claim_and_check()
        self.coordinator.record_check(self.identity, {
            "check_name": "CI", "head_sha": baseline.HEAD, "state": "failure", "evidence": "run/failed"})
        with self.assertRaises(StoreError):
            self.coordinator.finish(self.identity)
        self.coordinator.finish(self.identity, {"result": "blocked", "reason": "red CI",
            "reviewer_stopped": True, "stop_evidence": "stopped", "fence_evidence": "fenced"})
        with self.assertRaises(StoreError):
            self.coordinator.ready(self._identity(generation=2))
        self._next_review()

    def test_clean_refuses_open_finding_and_verification_requires_current_fix_binding(self):
        self._claim_and_check()
        self.coordinator.record_finding(self.identity, {
            "finding_id": "f", "severity": "P2", "evidence": "reproduced"})
        with self.assertRaisesRegex(StoreError, "bevindingen open"):
            self.coordinator.finish(self.identity, {"result": "clean"})
        self.coordinator.finish(self.identity)
        newer = self._next_review()
        disposition = {"finding_id": "f", "disposition": "verified", "evidence": "fix checked",
                       "fix_commit": "d" * 40, "verification_sha": "d" * 40,
                       "verification_request_id": "r2"}
        for key in ("fix_commit", "verification_sha", "verification_request_id"):
            missing = {k: v for k, v in disposition.items() if k != key}
            with self.assertRaises(StoreError):
                self.coordinator.disposition_finding(newer, missing)
        for wrong in ({"verification_sha": baseline.HEAD}, {"verification_request_id": self.request_id},
                      {"fix_commit": baseline.HEAD}, {"fix_commit": "short"}):
            with self.assertRaises(StoreError):
                self.coordinator.disposition_finding(newer, disposition | wrong)
        with self.assertRaises(StoreError):
            self.coordinator.disposition_finding(self.identity, disposition)
        self.coordinator.disposition_finding(newer, disposition)
        self.coordinator.finish(newer, {"result": "clean"})
        self.coordinator.ready(newer)
        # Legacy unbound disposition remains in the append-only ledger but cannot authorize action.
        self.db.execute("INSERT INTO finding_dispositions(finding_id,disposition,actor_session,evidence,event_id) "
                        "VALUES('f','verified','reviewer-1','legacy',1)")
        with self.assertRaisesRegex(StoreError, "huidige aanvraag"):
            self.coordinator.action_intent(newer, {"action_id": "a", "idempotency_key": "a",
                "action_kind": "publish", "target": "PR", "intent": "publish"})
