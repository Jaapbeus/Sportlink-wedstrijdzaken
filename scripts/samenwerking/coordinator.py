"""Fail-closed operations for a single-host, shadow-mode collaboration log."""

from __future__ import annotations

import sqlite3
from typing import Any

from .actions import ActionCoordinator
from .identity import finding_request, exact_request, validate_assignment
from .obligations import Obligations
from .authority import Authority
from .integration import Integration
from .lane import LaneCoordinator
from .queries import events as query_events, request_result, status as query_status
from .store import StoreError, append_event, load_request, transaction


class Coordinator:
    def __init__(self, connection: sqlite3.Connection):
        self.db = connection
        meta = dict(connection.execute("SELECT key, value FROM metadata"))
        if meta.get("protocol") != "1" or meta.get("schema_revision") != "2" or meta.get("mode") != "shadow":
            raise StoreError("Initialiseer eerst protocol 1 schema-revisie 2 in shadow-modus.")
        self.name = meta["coordinator"]

    def create_request(self, data: dict[str, Any]) -> dict[str, Any]:
        required = (
            "request_id", "repository", "issue", "pr", "branch", "base_sha",
            "head_sha", "merge_base_sha", "implementer_session", "reviewer_session",
            "scope", "kind",
        )
        _require(data, required)
        if data["kind"] not in ("substantive", "integration"):
            raise StoreError("Soort aanvraag moet substantive of integration zijn.")
        if any(not isinstance(data[key], str) or not data[key].strip() for key in required if key not in ("issue", "pr")):
            raise StoreError("Aanvraagidentiteit en scope mogen niet leeg zijn.")
        _positive_int(data["issue"], "issue")
        if data["implementer_session"] == data["reviewer_session"]:
            raise StoreError("Implementer en reviewer moeten aparte sessies zijn.")
        for field in ("base_sha", "head_sha", "merge_base_sha"):
            _full_sha(data[field], field)
        checks, manifest = Obligations.validate(data)
        scope = data["scope"]
        if not isinstance(scope, str) or not scope.strip():
            raise StoreError("De scope mag niet leeg zijn.")
        with transaction(self.db):
            if self.db.execute(
                "SELECT 1 FROM requests WHERE request_id = ?", (data["request_id"],)
            ).fetchone():
                raise StoreError("request_id bestaat al.")
            validate_assignment(self.db, data)
            counter = self.db.execute(
                "SELECT * FROM counters WHERE repository=? AND pr=?",
                (data["repository"], _positive_int(data["pr"], "pr")),
            ).fetchone()
            substantive = counter["substantive_rounds"] if counter else 0
            integration = counter["integration_attempts"] if counter else 0
            if data["kind"] == "substantive":
                substantive += 1
                if substantive > 3:
                    raise StoreError("De limiet van drie inhoudelijke rondes is bereikt.")
            else:
                integration += 1
                if integration > 2:
                    raise StoreError("De limiet van twee integratiepogingen is bereikt.")
            prepared = Integration(self.db).prepare(data) if data["kind"] == "integration" else None
            event = append_event(
                self.db, "request_offered", data["request_id"], self.name,
                {"request": data, "substantive_round": substantive,
                 "integration_attempt": integration}, generation=1,
            )
            self.db.execute(
                "INSERT INTO counters VALUES(?,?,?,?) ON CONFLICT(repository,pr) "
                "DO UPDATE SET substantive_rounds=excluded.substantive_rounds, "
                "integration_attempts=excluded.integration_attempts",
                (data["repository"], int(data["pr"]), substantive, integration),
            )
            self.db.execute(
                "INSERT INTO requests VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (data["request_id"], data["repository"], int(data["issue"]),
                 int(data["pr"]), data["branch"], data["base_sha"], data["head_sha"],
                 data["merge_base_sha"], data["implementer_session"],
                 data["reviewer_session"], scope.strip(), data["kind"], substantive,
                 integration, 1, 1, "offered", "unscoped", event),
            )
            if prepared:
                Integration(self.db).install(data["request_id"], prepared)
            Obligations(self.db).install(data["request_id"], manifest, event)
            for check in checks:
                self.db.execute(
                    "INSERT INTO required_checks(request_id,check_name,expected_sha) "
                    "VALUES(?,?,?)", (data["request_id"], check, data["head_sha"]),
                )
            return request_result(load_request(self.db, data["request_id"]))

    def claim(self, identity: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", offered=True)
            event = append_event(
                self.db, "reviewer_claimed", request["request_id"],
                identity["session"], {"attempt": request["attempt"], "head_sha": request["head_sha"]},
                generation=request["generation"],
            )
            self.db.execute(
                "UPDATE requests SET state='claimed' WHERE request_id=?",
                (request["request_id"],),
            )
            return {"state": "claimed", "event_id": event}

    def register_task(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            return Obligations(self.db).start(request, identity, data)

    def submit_task_output(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._finding_identity(identity)
            return Obligations(self.db).output(request, identity, data)

    def complete_task(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            return Obligations(self.db).complete(request, identity, data)

    def attest_integration(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            return Integration(self.db).attest(request, identity, data)

    def grant(self, data: dict[str, Any]) -> dict[str, Any]:
        return Authority(self.db).grant(data)

    def revoke_grant(self, data: dict[str, Any]) -> dict[str, Any]:
        return Authority(self.db).revoke(data)

    def cancel_task(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("task_id", "reason", "confirmed_stopped"))
        if not isinstance(data["confirmed_stopped"], bool) or not data["confirmed_stopped"]:
            raise StoreError("Annul alleen na bevestiging dat de taak is gestopt.")
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            task = self._task(request["request_id"], data["task_id"])
            if task["classification"] == "mandatory":
                raise StoreError("Verplichte verificatie kan niet worden geannuleerd.")
            if task["state"] != "running" or not str(data["reason"]).strip():
                raise StoreError("Alleen een actieve optionele taak met reden kan worden geannuleerd.")
            event = append_event(
                self.db, "optional_task_cancelled", request["request_id"], identity["session"],
                {"task_id": data["task_id"], "reason": data["reason"]},
                generation=request["generation"],
            )
            self.db.execute(
                "UPDATE tasks SET state='cancelled',stop_confirmed=1,cancel_reason=?,updated_event=? "
                "WHERE task_id=?", (str(data["reason"]).strip(), event, data["task_id"]),
            )
            return {"task_id": data["task_id"], "state": "cancelled", "event_id": event}

    def record_finding(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("finding_id", "severity", "evidence"))
        if not str(data["evidence"]).strip():
            raise StoreError("Een bevinding vereist bewijs.")
        with transaction(self.db):
            request = self._finding_identity(identity)
            if self.db.execute(
                "SELECT 1 FROM findings WHERE finding_id=?", (data["finding_id"],)
            ).fetchone():
                raise StoreError("finding_id bestaat al; gebruik een nieuw correctie-event.")
            unexpected = (
                identity["session"] != request["reviewer_session"]
                or int(identity["generation"]) != request["generation"]
                or int(identity["attempt"]) != request["attempt"]
            )
            event = append_event(
                self.db, "finding_recorded", request["request_id"], identity["session"],
                {"finding_id": data["finding_id"], "severity": data["severity"],
                 "evidence": data["evidence"], "unexpected": unexpected},
                generation=int(identity["generation"]),
            )
            self.db.execute(
                "INSERT INTO findings VALUES(?,?,?,?,?,?,?,?,?,?)",
                (data["finding_id"], request["repository"], request["pr"],
                 request["request_id"], identity["session"], int(identity["generation"]),
                 data["severity"], str(data["evidence"]).strip(), int(unexpected), event),
            )
            affected = self.db.execute(
                "SELECT request_id,generation FROM requests WHERE repository=? AND pr=? "
                "AND state IN ('review_returned','ready')", (request["repository"], request["pr"]),
            ).fetchall()
            for prior in affected:
                self.db.execute("UPDATE requests SET state='invalidated' WHERE request_id=?",
                                (prior["request_id"],))
                append_event(
                    self.db, "readiness_invalidated_by_finding", prior["request_id"],
                    self.name, {"finding_id": data["finding_id"]}, generation=prior["generation"],
                )
            return {"finding_id": data["finding_id"], "unexpected": unexpected, "event_id": event}

    def disposition_finding(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("finding_id", "disposition", "evidence"))
        disposition = data["disposition"]
        if disposition not in ("verified", "owner_accepted"):
            raise StoreError("Disposition moet verified of owner_accepted zijn.")
        if not str(data["evidence"]).strip():
            raise StoreError("Een disposition vereist bewijs.")
        with transaction(self.db):
            finding = self.db.execute(
                "SELECT * FROM findings WHERE finding_id=?", (data["finding_id"],)
            ).fetchone()
            if finding is None:
                raise StoreError("Bevinding niet gevonden.")
            request = load_request(self.db, finding["request_id"])
            if disposition == "verified":
                original = request
                request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
                Authority(self.db).verify_fix_binding(identity, request, original, data)
            else:
                Authority(self.db).require(identity, request, data, "owner_accepted", data["finding_id"])
            event = append_event(
                self.db, "finding_disposition_recorded", request["request_id"],
                identity["session"], {"finding_id": data["finding_id"],
                "disposition": disposition, "evidence": data["evidence"],
                "authorization_ref": data.get("authorization_ref"), "grant_id": data.get("grant_id"),
                "fix_commit": data.get("fix_commit"), "verification_sha": data.get("verification_sha"),
                "verification_request_id": data.get("verification_request_id")},
                generation=request["generation"],
            )
            self.db.execute(
                "INSERT INTO finding_dispositions(finding_id,disposition,actor_session,evidence,"
                "authorization_ref,event_id) VALUES(?,?,?,?,?,?)",
                (data["finding_id"], disposition, identity["session"],
                 str(data["evidence"]).strip(), data.get("authorization_ref"), event),
            )
            if request["state"] == "ready":
                self.db.execute(
                    "UPDATE requests SET state='invalidated' WHERE request_id=?",
                    (request["request_id"],),
                )
                append_event(
                    self.db, "readiness_invalidated_by_disposition_change", request["request_id"],
                    self.name, {"finding_id": data["finding_id"]}, generation=request["generation"],
                )
            return {"finding_id": data["finding_id"], "disposition": disposition, "event_id": event}

    def record_check(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("check_name", "head_sha", "state", "evidence"))
        if data["state"] not in ("success", "failure", "skipped"):
            raise StoreError("Checkstatus moet success, failure of skipped zijn.")
        _full_sha(data["head_sha"], "head_sha")
        if not isinstance(data["evidence"], str) or not data["evidence"].strip():
            raise StoreError("Checkbewijs mag niet leeg zijn.")
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            if data["head_sha"] != request["head_sha"]:
                raise StoreError("Checkbewijs hoort niet bij de aangeboden volledige head-SHA.")
            check = self.db.execute(
                "SELECT * FROM required_checks WHERE request_id=? AND check_name=?",
                (request["request_id"], data["check_name"]),
            ).fetchone()
            if check is None:
                raise StoreError("Deze check staat niet als verplicht geregistreerd.")
            event = append_event(
                self.db, "required_check_recorded", request["request_id"], identity["session"],
                {"check_name": data["check_name"], "head_sha": data["head_sha"],
                 "state": data["state"], "evidence": data["evidence"]},
                generation=request["generation"],
            )
            self.db.execute(
                "UPDATE required_checks SET state=?,evidence=?,event_id=? "
                "WHERE request_id=? AND check_name=?",
                (data["state"], str(data["evidence"]).strip(), event,
                 request["request_id"], data["check_name"]),
            )
            return {"check_name": data["check_name"], "state": data["state"], "event_id": event}

    def finish(self, identity: dict[str, Any], data: dict[str, Any] | None = None) -> dict[str, Any]:
        data = {} if data is None else data
        if not isinstance(data, dict):
            raise StoreError("Reviewresultaat vereist een JSON-object.")
        result = data.get("result", "completed")
        if result not in ("completed", "clean", "blocked"):
            raise StoreError("Mislukte of onbekende reviewuitkomst kan niet als voltooid worden teruggegeven.")
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("claimed", "reviewing"))
            if result == "blocked":
                return Obligations(self.db).return_blocked(request, identity, data)
            self._assert_tasks_complete(request["request_id"])
            self._assert_checks_success(request["request_id"])
            Integration(self.db).assert_valid(request)
            if result == "clean":
                Authority(self.db).assert_findings_resolved(request)
            event = append_event(
                self.db, "review_returned", request["request_id"], identity["session"],
                {"result": result,
                 "head_sha": request["head_sha"]}, generation=request["generation"],
            )
            self.db.execute(
                "UPDATE requests SET state='review_returned' WHERE request_id=?",
                (request["request_id"],),
            )
            return {"state": "review_returned", "event_id": event}

    def ready(self, identity: dict[str, Any]) -> dict[str, Any]:
        with transaction(self.db):
            request = self._identity(identity, "reviewer_session", states=("review_returned",))
            self._assert_tasks_complete(request["request_id"])
            self._assert_checks_success(request["request_id"])
            Integration(self.db).assert_valid(request)
            Authority(self.db).assert_findings_resolved(request)
            event = append_event(
                self.db, "request_ready", request["request_id"], identity["session"],
                {"head_sha": request["head_sha"], "findings_reviewed": True,
                 "checks_on_exact_head": True}, generation=request["generation"],
            )
            self.db.execute(
                "UPDATE requests SET state='ready' WHERE request_id=?", (request["request_id"],)
            )
            return {"state": "ready", "event_id": event,
                    "note": "Lokale bewijsregistratie; geen remote authenticiteit geverifieerd."}

    def reassign(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("request_id", "old_actor_stopped", "new_reviewer_session", "reason"))
        if data["old_actor_stopped"] is not True:
            raise StoreError("Overdracht geblokkeerd: stop van de oude actor is niet bevestigd.")
        if not str(data["reason"]).strip() or not str(data["new_reviewer_session"]).strip():
            raise StoreError("Nieuwe reviewer en overdrachtsreden zijn verplicht.")
        with transaction(self.db):
            request = load_request(self.db, data["request_id"])
            if request["state"] not in ("offered", "claimed", "reviewing", "review_returned", "invalidated"):
                raise StoreError("Deze aanvraag kan niet worden overgedragen.")
            if data["new_reviewer_session"] == request["implementer_session"]:
                raise StoreError("Implementer kan geen reviewer worden.")
            Obligations(self.db).confirm_children_stopped(request, data)
            generation = request["generation"] + 1
            attempt = request["attempt"] + 1
            event = append_event(
                self.db, "reviewer_reassigned", request["request_id"], self.name,
                {"old_reviewer_session": request["reviewer_session"],
                 "new_reviewer_session": data["new_reviewer_session"],
                 "reason": data["reason"], "old_actor_stopped": True, "attempt": attempt,
                 "stopped_sessions": data.get("stopped_sessions", [])},
                generation=generation,
            )
            self.db.execute(
                "UPDATE requests SET reviewer_session=?,attempt=?,generation=?,state='offered' "
                "WHERE request_id=?",
                (data["new_reviewer_session"], attempt, generation, request["request_id"]),
            )
            self.db.execute(
                "UPDATE tasks SET state='pending',evidence=NULL,generation=?,assigned_session=CASE "
                "WHEN assigned_session=? THEN ? ELSE assigned_session END WHERE request_id=?",
                (generation, request["reviewer_session"], data["new_reviewer_session"], request["request_id"]),
            )
            self.db.execute(
                "UPDATE required_checks SET state='pending',evidence=NULL WHERE request_id=?",
                (request["request_id"],),
            )
            return {"state": "offered", "attempt": attempt, "generation": generation,
                    "event_id": event}

    def retract(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("request_id", "result_event_id", "reason"))
        if not str(data["reason"]).strip():
            raise StoreError("Een intrekking vereist een reden.")
        with transaction(self.db):
            request = load_request(self.db, data["request_id"])
            original = self.db.execute(
                "SELECT * FROM events WHERE event_id=? AND request_id=? AND event_type='review_returned'",
                (data["result_event_id"], data["request_id"]),
            ).fetchone()
            if original is None:
                raise StoreError("Het genoemde reviewresultaat bestaat niet.")
            event = append_event(
                self.db, "review_result_retracted", request["request_id"], self.name,
                {"result_event_id": int(data["result_event_id"]), "reason": data["reason"]},
                generation=request["generation"],
            )
            self.db.execute(
                "UPDATE requests SET state='invalidated' WHERE request_id=?",
                (request["request_id"],),
            )
            return {"state": "invalidated", "event_id": event,
                    "retracted_event_id": int(data["result_event_id"])}

    def correct_result(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("request_id", "result_event_id", "reason", "correction"))
        if not str(data["reason"]).strip() or not str(data["correction"]).strip():
            raise StoreError("Een correctie vereist een reden en nieuwe resultaatverwijzing.")
        with transaction(self.db):
            request = load_request(self.db, data["request_id"])
            original = self.db.execute(
                "SELECT * FROM events WHERE event_id=? AND request_id=? AND event_type='review_returned'",
                (data["result_event_id"], data["request_id"]),
            ).fetchone()
            if original is None:
                raise StoreError("Het genoemde reviewresultaat bestaat niet.")
            event = append_event(
                self.db, "review_result_corrected", request["request_id"], self.name,
                {"result_event_id": int(data["result_event_id"]), "reason": data["reason"],
                 "correction": data["correction"]}, generation=request["generation"],
            )
            self.db.execute(
                "UPDATE requests SET state='invalidated' WHERE request_id=?",
                (request["request_id"],),
            )
            return {"state": "invalidated", "event_id": event,
                    "corrected_event_id": int(data["result_event_id"])}

    def lane_enqueue(self, data: dict[str, Any]) -> dict[str, Any]:
        return LaneCoordinator(self.db, self.name).enqueue(data)

    def lane_reserve(self, data: dict[str, Any]) -> dict[str, Any]:
        return LaneCoordinator(self.db, self.name).reserve(data)

    def lane_release(self, data: dict[str, Any]) -> dict[str, Any]:
        return LaneCoordinator(self.db, self.name).release(data)

    def action_intent(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        return ActionCoordinator(self.db, self.name, self._action_identity).intent(identity, data)

    def _action_identity(self, identity, session_column, **options):
        request = self._identity(identity, session_column, **options)
        if identity["session"] not in (request["reviewer_session"], request["implementer_session"]):
            raise StoreError("Actie vereist een toegewezen actor.")
        Authority(self.db).assert_findings_resolved(request)
        Integration(self.db).assert_valid(request)
        return request

    def reconcile(self, data: dict[str, Any]) -> dict[str, Any]:
        return ActionCoordinator(self.db, self.name, self._identity).reconcile(data)

    def status(self, request_id: str) -> dict[str, Any]:
        return query_status(self.db, request_id)

    def events(self, request_id: str | None) -> list[dict[str, Any]]:
        return query_events(self.db, request_id)

    def _identity(self, identity, session_column, **options):
        return exact_request(self.db, identity, session_column, **options)

    def _finding_identity(self, identity: dict[str, Any]) -> sqlite3.Row:
        return finding_request(self.db, identity)

    def _task(self, request_id: str, task_id: str) -> sqlite3.Row:
        task = self.db.execute(
            "SELECT * FROM tasks WHERE task_id=? AND request_id=?", (task_id, request_id)
        ).fetchone()
        if task is None:
            raise StoreError("Taak niet gevonden.")
        return task

    def _assert_tasks_complete(self, request_id: str) -> None:
        Obligations(self.db).assert_manifest(request_id)
        pending = self.db.execute(
            "SELECT task_id FROM tasks WHERE request_id=? AND (state IN ('pending','running') OR "
            "(classification='mandatory' AND state!='completed') OR "
            "(classification='optional' AND state='cancelled' AND stop_confirmed!=1))",
            (request_id,),
        ).fetchall()
        if pending:
            raise StoreError("Verificatieblokkade: taken lopen nog of missen voltooid bewijs.")

    def _assert_checks_success(self, request_id: str) -> None:
        if not self.db.execute("SELECT 1 FROM required_checks WHERE request_id=?", (request_id,)).fetchone():
            raise StoreError("Verplichte checks ontbreken.")
        failed = self.db.execute(
            "SELECT check_name FROM required_checks WHERE request_id=? AND "
            "(state!='success' OR expected_sha!=(SELECT head_sha FROM requests WHERE request_id=?))",
            (request_id, request_id),
        ).fetchall()
        if failed:
            raise StoreError("Verificatieblokkade: verplichte checks ontbreken, zijn niet groen of horen bij een andere SHA.")


def _require(data: dict[str, Any], fields: tuple[str, ...]) -> None:
    missing = [name for name in fields if name not in data]
    if missing:
        raise StoreError("Ontbrekende verplichte velden: " + ", ".join(missing))


def _full_sha(value: Any, label: str) -> None:
    if not isinstance(value, str) or len(value) != 40 or any(c not in "0123456789abcdef" for c in value):
        raise StoreError(f"{label} moet een volledige SHA-1 zijn.")


def _positive_int(value: Any, name: str) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError) as exc:
        raise StoreError(f"{name} moet een positief geheel getal zijn.") from exc
    if number < 1:
        raise StoreError(f"{name} moet een positief geheel getal zijn.")
    return number
