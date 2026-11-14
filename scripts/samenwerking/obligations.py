"""Immutable predeclared obligations and reviewer acceptance of child output."""
from __future__ import annotations

from typing import Any

from .store import StoreError, append_event, decode


class Obligations:
    def __init__(self, db):
        self.db = db

    @staticmethod
    def validate(data):
        checks = data.get("required_checks")
        if (not isinstance(checks, list) or not checks
                or any(not isinstance(x, str) or not x.strip() for x in checks)
                or len(set(checks)) != len(checks)):
            raise StoreError("required_checks vereist een niet-lege lijst unieke namen.")
        manifest = data.get("mandatory_tasks")
        if not isinstance(manifest, list) or not manifest:
            raise StoreError("mandatory_tasks vereist vooraf vastgelegde verificatie.")
        ids = []
        verification = False
        for task in manifest:
            if not isinstance(task, dict) or any(
                not isinstance(task.get(key), str) or not task[key].strip()
                for key in ("task_id", "purpose", "assigned_session")
            ):
                raise StoreError("Manifest vereist task_id, purpose en assigned_session.")
            ids.append(task["task_id"])
            if task.get("reviewer_verification") is True:
                if task["assigned_session"] != data["reviewer_session"]:
                    raise StoreError("Reviewer-verificatie moet aan de onafhankelijke reviewer zijn toegewezen.")
                verification = True
        if len(set(ids)) != len(ids) or not verification:
            raise StoreError("Manifest vereist unieke taken en expliciete reviewer_verification.")
        return checks, manifest

    def install(self, request_id, manifest, event):
        for task in manifest:
            self.db.execute(
                "INSERT INTO tasks(task_id,request_id,classification,purpose,assigned_session,"
                "state,generation,created_event,updated_event) VALUES(?,?,'mandatory',?,?,'pending',1,?,?)",
                (task["task_id"], request_id, task["purpose"], task["assigned_session"], event, event),
            )
            self.db.execute("INSERT INTO task_manifest VALUES(?,?,?)",
                            (request_id, task["task_id"], int(task.get("reviewer_verification") is True)))

    def task(self, request, data):
        task = self.db.execute("SELECT * FROM tasks WHERE request_id=? AND task_id=?",
                               (request["request_id"], data.get("task_id"))).fetchone()
        if task is None:
            raise StoreError("Taak ontbreekt in de aanvraag.")
        return task

    def start(self, request, identity, data):
        task = self.db.execute("SELECT * FROM tasks WHERE task_id=?", (data.get("task_id"),)).fetchone()
        if task is None:
            if data.get("classification") != "optional":
                raise StoreError("Verplichte taken moeten vooraf in het manifest staan.")
            if any(not isinstance(data.get(k), str) or not data[k].strip()
                   for k in ("task_id", "purpose", "assigned_session")):
                raise StoreError("Optionele taak mist identiteit of doel.")
            self.db.execute(
                "INSERT INTO tasks(task_id,request_id,classification,purpose,assigned_session,"
                "state,generation,created_event,updated_event) VALUES(?,?,'optional',?,?,'pending',?,0,0)",
                (data["task_id"], request["request_id"], data["purpose"], data["assigned_session"], request["generation"]),
            )
            task = self.task(request, data)
        if task["request_id"] != request["request_id"] or task["state"] != "pending":
            raise StoreError("Taak is niet beschikbaar in deze aanvraag.")
        for key in ("classification", "purpose", "assigned_session"):
            if key in data and data[key] != task[key]:
                raise StoreError("Taak wijkt af van het onveranderlijke manifest.")
        event = append_event(self.db, "task_started", request["request_id"], identity["session"],
                             {"task_id": task["task_id"]}, generation=request["generation"])
        self.db.execute("UPDATE tasks SET state='running',updated_event=? WHERE task_id=?", (event, task["task_id"]))
        self.db.execute("UPDATE requests SET state='reviewing' WHERE request_id=?", (request["request_id"],))
        return {"task_id": task["task_id"], "state": "running", "event_id": event}

    def output(self, request, identity, data):
        task = self.task(request, data)
        if not isinstance(data.get("evidence"), str) or not data["evidence"].strip():
            raise StoreError("Taakuitvoer vereist bewijs.")
        if data.get("result") not in ("success", "failure", "unknown", "cancelled"):
            raise StoreError("Taakuitvoer vereist een expliciete result-status.")
        accepted = (data["result"] == "success" and identity["session"] == task["assigned_session"]
                    and identity["attempt"] == request["attempt"]
                    and identity["generation"] == request["generation"] == task["generation"]
                    and task["state"] == "running" and request["state"] in ("claimed", "reviewing"))
        event = append_event(self.db, "task_output_recorded", request["request_id"], identity["session"],
                             {"task_id": task["task_id"], "identity": identity,
                              "evidence": data["evidence"], "result": data["result"], "eligible": accepted}, generation=identity["generation"])
        if data["result"] == "failure":
            finding_id = f"task-output-{event}"
            self.db.execute("INSERT INTO findings VALUES(?,?,?,?,?,?,?,?,?,?)",
                (finding_id, request["repository"], request["pr"], request["request_id"], identity["session"],
                 int(identity["generation"]), "untriaged", f"Mislukte taakuitvoer: event {event}", int(identity["session"] != task["assigned_session"] or identity["attempt"] != request["attempt"]
                     or identity["generation"] != request["generation"]), event))
            self.db.execute("UPDATE requests SET state='invalidated' WHERE repository=? AND pr=? "
                            "AND state IN ('review_returned','ready')", (request["repository"], request["pr"]))
            append_event(self.db, "failed_task_output_requires_triage", request["request_id"], identity["session"],
                         {"finding_id": finding_id, "output_event_id": event}, generation=identity["generation"])
        return {"event_id": event, "eligible": accepted, "state": "awaiting_reviewer"}

    def complete(self, request, identity, data):
        task = self.task(request, data)
        if task["state"] != "running" or task["generation"] != request["generation"]:
            raise StoreError("Taak is niet actief in deze generatie.")
        if not isinstance(data.get("evidence"), str) or not data["evidence"].strip():
            raise StoreError("Onafhankelijke verificatie vereist bewijs.")
        if data.get("result") != "success":
            raise StoreError("Alleen expliciet succesvol geverifieerde taken kunnen worden voltooid.")
        if task["assigned_session"] != identity["session"]:
            row = self.db.execute("SELECT * FROM events WHERE event_id=? AND event_type='task_output_recorded'",
                                  (data.get("output_event_id"),)).fetchone()
            output = decode(row["payload"]) if row else {}
            bound = output.get("identity", {})
            if (not row or row["request_id"] != request["request_id"] or not output.get("eligible")
                    or output.get("task_id") != task["task_id"] or row["actor"] != task["assigned_session"]
                    or bound.get("attempt") != request["attempt"]
                    or bound.get("generation") != request["generation"]):
                raise StoreError("Child-uitvoer mist exacte taak-, sessie-, poging- of generatiebinding.")
        event = append_event(self.db, "task_completed", request["request_id"], identity["session"],
                             data, generation=request["generation"])
        self.db.execute("UPDATE tasks SET state='completed',evidence=?,updated_event=? WHERE task_id=?",
                        (data["evidence"], event, task["task_id"]))
        return {"task_id": task["task_id"], "state": "completed", "event_id": event}

    def assert_manifest(self, request_id):
        rows = self.db.execute("SELECT m.verification,t.* FROM task_manifest m JOIN tasks t "
                               "ON t.task_id=m.task_id WHERE m.request_id=?", (request_id,)).fetchall()
        if not rows or not any(row["verification"] for row in rows):
            raise StoreError("Verplicht verificatiemanifest ontbreekt.")
        if any(row["state"] != "completed" for row in rows):
            raise StoreError("Verificatieblokkade: verplichte taken lopen nog of missen voltooid bewijs.")

    def confirm_children_stopped(self, request, data):
        running = self.db.execute("SELECT DISTINCT assigned_session FROM tasks WHERE request_id=? AND state='running'",
                                  (request["request_id"],)).fetchall()
        children = {row[0] for row in running} - {request["reviewer_session"]}
        stopped = data.get("stopped_sessions", [])
        if not isinstance(stopped, list) or not children.issubset(set(stopped)):
            raise StoreError("Overdracht vereist bevestigde stop van alle actieve child-sessies.")
