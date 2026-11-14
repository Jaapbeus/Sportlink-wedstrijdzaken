"""Acceptance tests for transactionality, fencing, findings, and recovery."""

from __future__ import annotations

import sqlite3
import tempfile
import threading
import unittest
from pathlib import Path

from .coordinator import Coordinator
from .store import StoreError, connect, initialize


BASE = "a" * 40
HEAD = "b" * 40
MERGE = "c" * 40


class CoordinatorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.db_path = Path(self.temp.name) / "coordinator.sqlite"
        self.db = connect(self.db_path)
        initialize(self.db, "test-coordinator", "owner-1")
        self.coordinator = Coordinator(self.db)
        self.request_id = "request-1"
        self.coordinator.create_request(self._request())
        self.identity = self._identity()

    def tearDown(self) -> None:
        self.db.close()
        self.temp.cleanup()

    def _request(self, **overrides: object) -> dict[str, object]:
        value: dict[str, object] = {
            "request_id": self.request_id if hasattr(self, "request_id") else "request-1",
            "repository": "org/repo", "issue": 1940, "pr": 77, "branch": "feature",
            "base_sha": BASE, "head_sha": HEAD, "merge_base_sha": MERGE,
            "implementer_session": "implementer-1", "reviewer_session": "reviewer-1",
            "scope": "isolated test scope", "kind": "substantive", "required_checks": ["CI"],
            "mandatory_tasks": [{"task_id": "verify-" + str(overrides.get("request_id", self.request_id)),
                "purpose": "independent behavior verification", "assigned_session": overrides.get("reviewer_session", "reviewer-1"),
                "reviewer_verification": True}],
        }
        value.update(overrides)
        return value

    def _identity(self, **overrides: object) -> dict[str, object]:
        value: dict[str, object] = {
            "request_id": self.request_id, "repository": "org/repo", "pr": 77,
            "base_sha": BASE, "head_sha": HEAD, "session": "reviewer-1",
            "attempt": 1, "generation": 1,
        }
        value.update(overrides)
        return value

    def _claim_and_check(self) -> None:
        self.coordinator.claim(self.identity)
        self._verify_task()
        self.coordinator.record_check(self.identity, {
            "check_name": "CI", "head_sha": HEAD, "state": "success", "evidence": "run/1",
        })

    def _verify_task(self, identity=None, task_id=None):
        identity = identity or self.identity
        task_id = task_id or "verify-" + self.request_id
        self.coordinator.register_task(identity, {"task_id": task_id})
        self.coordinator.complete_task(identity, {"task_id": task_id, "evidence": "behavior inspected and reproduced", "result": "success"})

    def test_concurrent_duplicate_claims_only_one_succeeds(self) -> None:
        barrier = threading.Barrier(2)
        results: list[str] = []
        lock = threading.Lock()

        def claim() -> None:
            connection = connect(self.db_path)
            try:
                barrier.wait()
                try:
                    Coordinator(connection).claim(self.identity)
                    outcome = "claimed"
                except StoreError:
                    outcome = "rejected"
                with lock:
                    results.append(outcome)
            finally:
                connection.close()

        threads = [threading.Thread(target=claim) for _ in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=5)
        self.assertCountEqual(results, ["claimed", "rejected"])
        self.assertTrue(all(not thread.is_alive() for thread in threads))

    def test_stale_identity_is_rejected_after_confirmed_transfer(self) -> None:
        self.coordinator.reassign({
            "request_id": self.request_id, "old_actor_stopped": True,
            "new_reviewer_session": "reviewer-2", "reason": "operator confirmed stop",
        })
        with self.assertRaisesRegex(StoreError, "verouderd"):
            self.coordinator.claim(self.identity)
        replacement = self._identity(session="reviewer-2", attempt=2, generation=2)
        self.coordinator.claim(replacement)

    def test_mandatory_verification_cannot_be_cancelled(self) -> None:
        self.coordinator.claim(self.identity)
        self.coordinator.register_task(self.identity, {
            "task_id": "verify-" + self.request_id,
        })
        with self.assertRaisesRegex(StoreError, "kan niet worden geannuleerd"):
            self.coordinator.cancel_task(self.identity, {
                "task_id": "verify-" + self.request_id, "reason": "skip", "confirmed_stopped": True,
            })
        with self.assertRaisesRegex(StoreError, "lopen nog"):
            self.coordinator.finish(self.identity)

    def test_clean_result_does_not_erase_unexpected_finding(self) -> None:
        self._claim_and_check()
        self.coordinator.record_finding(self.identity | {"session": "reviewer-surprise"}, {
            "finding_id": "finding-1", "severity": "P2", "evidence": "repro evidence",
        })
        self.coordinator.finish(self.identity)
        with self.assertRaisesRegex(StoreError, "bevindingen open"):
            self.coordinator.ready(self.identity)
        findings = self.coordinator.status(self.request_id)["findings"]
        self.assertEqual(len(findings), 1)
        self.assertEqual(findings[0]["disposition"], None)
        self.assertTrue(findings[0]["unexpected"])

    def test_finding_needs_disposition_by_independent_reviewer(self) -> None:
        self._claim_and_check()
        self.coordinator.record_finding(self.identity, {
            "finding_id": "finding-1", "severity": "P1", "evidence": "repro evidence",
        })
        with self.assertRaisesRegex(StoreError, "exact toegewezen actor"):
            self.coordinator.disposition_finding(
                self._identity(session="implementer-1"),
                {"finding_id": "finding-1", "disposition": "verified", "evidence": "checked"},
            )
        self.coordinator.disposition_finding(self.identity, {
            "finding_id": "finding-1", "disposition": "verified", "evidence": "fix commit checked",
        })
        self.coordinator.finish(self.identity)
        self.assertEqual(self.coordinator.ready(self.identity)["state"], "ready")

    def test_late_unexpected_finding_invalidates_readiness_and_is_retained(self) -> None:
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        self.coordinator.ready(self.identity)
        late = self.identity | {"session": "old-reviewer-session"}
        result = self.coordinator.record_finding(late, {
            "finding_id": "late-finding", "severity": "P2", "evidence": "late output",
        })
        self.assertTrue(result["unexpected"])
        status = self.coordinator.status(self.request_id)
        self.assertEqual(status["request"]["state"], "invalidated")
        self.assertEqual(status["findings"][0]["finding_id"], "late-finding")

    def test_database_restart_reconciles_half_completed_remote_action(self) -> None:
        self._claim_and_check()
        self.coordinator.finish(self.identity)
        self.coordinator.ready(self.identity)
        self.coordinator.action_intent(self.identity, {
            "action_id": "action-1", "idempotency_key": "key-1", "action_kind": "publish",
            "target": "PR-77", "intent": "publish sanitized result",
        })
        self.db.close()
        self.db = connect(self.db_path)
        self.coordinator = Coordinator(self.db)
        reconciled = self.coordinator.reconcile({
            "action_id": "action-1", "outcome": "applied", "observed_reference": "comment-9",
            "evidence": "observed existing remote result", "actor_session": "recovery-session",
        })
        self.assertEqual(reconciled["state"], "applied")
        duplicate = self.coordinator.reconcile({
            "action_id": "action-1", "outcome": "applied", "observed_reference": "comment-9",
            "evidence": "same observation",
        })
        self.assertTrue(duplicate["idempotent"])
        with self.assertRaisesRegex(StoreError, "Definitieve reconciliatie"):
            self.coordinator.reconcile({
                "action_id": "action-1", "outcome": "not_applied", "observed_reference": "",
                "evidence": "conflict",
            })

    def test_lane_serializes_and_selects_fifo_eligible_candidate(self) -> None:
        self.coordinator.lane_enqueue({
            "candidate_id": "first", "repository": "org/repo", "pr": 77, "dependencies": [],
        })
        self.coordinator.lane_enqueue({
            "candidate_id": "second", "repository": "org/repo", "pr": 78, "dependencies": ["first"],
        })
        reservation = self.coordinator.lane_reserve({
            "candidate_id": "first", "actor_session": "integrator", "base_sha": BASE, "head_sha": HEAD,
        })
        with self.assertRaisesRegex(StoreError, "al gereserveerd"):
            self.coordinator.lane_reserve({
                "candidate_id": "first", "actor_session": "other", "base_sha": BASE, "head_sha": HEAD,
            })
        self.coordinator.lane_release({
            "candidate_id": "first", "reservation_id": reservation["reservation_id"],
            "actor_session": "integrator", "generation": reservation["generation"], "outcome": "completed",
        })
        self.assertEqual(self.coordinator.lane_reserve({
            "candidate_id": "second", "actor_session": "integrator", "base_sha": BASE, "head_sha": HEAD,
        })["state"], "reserved")

    def test_event_history_cannot_be_rewritten(self) -> None:
        event_id = self.coordinator.events(self.request_id)[0]["event_id"]
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE events SET actor='changed' WHERE event_id=?", (event_id,))


if __name__ == "__main__":
    unittest.main()
