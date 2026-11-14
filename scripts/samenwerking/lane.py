"""Single local FIFO develop lane; it records reservations but cannot fence remotes."""

from __future__ import annotations

import json
import sqlite3
import uuid
from typing import Any

from .store import StoreError, append_event, transaction


class LaneCoordinator:
    def __init__(self, connection: sqlite3.Connection, name: str):
        self.db = connection
        self.name = name

    def enqueue(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("candidate_id", "repository", "pr", "dependencies"))
        dependencies = data["dependencies"]
        if not isinstance(dependencies, list) or data["candidate_id"] in dependencies:
            raise StoreError("dependencies moet een lijst zonder zelfverwijzing zijn.")
        with transaction(self.db):
            for dependency in dependencies:
                if not self.db.execute(
                    "SELECT 1 FROM lane_entries WHERE candidate_id=?", (dependency,)
                ).fetchone():
                    raise StoreError("Een lane-afhankelijkheid bestaat niet.")
            event = append_event(
                self.db, "lane_candidate_enqueued", None, self.name,
                {"candidate_id": data["candidate_id"], "pr": int(data["pr"]),
                 "dependencies": dependencies},
            )
            self.db.execute(
                "INSERT INTO lane_entries(candidate_id,repository,pr,dependencies,state,created_event,updated_event) "
                "VALUES(?,?,?,?,'queued',?,?)",
                (data["candidate_id"], data["repository"], int(data["pr"]),
                 json.dumps(dependencies), event, event),
            )
            for dependency in dependencies:
                self.db.execute(
                    "INSERT INTO lane_dependencies VALUES(?,?)", (data["candidate_id"], dependency)
                )
            return {"candidate_id": data["candidate_id"], "state": "queued", "event_id": event}

    def reserve(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("candidate_id", "actor_session", "base_sha", "head_sha"))
        _full_sha(data["base_sha"], "base_sha")
        _full_sha(data["head_sha"], "head_sha")
        with transaction(self.db):
            active = self.db.execute("SELECT 1 FROM lane_entries WHERE state='reserved'").fetchone()
            if active:
                raise StoreError("De develop-integratielane is al gereserveerd.")
            head = self.db.execute(
                "SELECT e.* FROM lane_entries e WHERE e.state='queued' "
                "AND NOT EXISTS (SELECT 1 FROM lane_dependencies d JOIN lane_entries p "
                "ON p.candidate_id=d.dependency_id WHERE d.candidate_id=e.candidate_id "
                "AND p.state!='completed') ORDER BY e.sequence LIMIT 1"
            ).fetchone()
            if head is None or head["candidate_id"] != data["candidate_id"]:
                raise StoreError("Kandidaat is niet de eerstvolgende FIFO-kandidaat met voldane afhankelijkheden.")
            generation = head["generation"] + 1
            reservation = str(uuid.uuid4())
            event = append_event(
                self.db, "lane_reserved", None, data["actor_session"],
                {"candidate_id": data["candidate_id"], "reservation_id": reservation,
                 "base_sha": data["base_sha"], "head_sha": data["head_sha"]},
                generation=generation,
            )
            self.db.execute(
                "UPDATE lane_entries SET state='reserved',actor_session=?,expected_base_sha=?, "
                "expected_head_sha=?,generation=?,reservation_id=?,updated_event=? WHERE candidate_id=?",
                (data["actor_session"], data["base_sha"], data["head_sha"], generation,
                 reservation, event, data["candidate_id"]),
            )
            return {"candidate_id": data["candidate_id"], "reservation_id": reservation,
                    "generation": generation, "state": "reserved", "event_id": event}

    def release(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("candidate_id", "reservation_id", "actor_session", "generation", "outcome"))
        if data["outcome"] not in ("completed", "withdrawn"):
            raise StoreError("Lane-reservering vereist completed of expliciet withdrawn.")
        with transaction(self.db):
            entry = self.db.execute(
                "SELECT * FROM lane_entries WHERE candidate_id=?", (data["candidate_id"],)
            ).fetchone()
            if entry is None or entry["state"] != "reserved":
                raise StoreError("Er is geen actieve lane-reservering voor deze kandidaat.")
            if (entry["reservation_id"] != data["reservation_id"]
                    or entry["actor_session"] != data["actor_session"]
                    or entry["generation"] != int(data["generation"])):
                raise StoreError("Lane-identiteit is verouderd of komt niet overeen.")
            if data["outcome"] == "withdrawn" and not str(data.get("reason", "")).strip():
                raise StoreError("Intrekken vereist een reden.")
            event = append_event(
                self.db, "lane_reservation_released", None, data["actor_session"],
                {"candidate_id": data["candidate_id"], "outcome": data["outcome"],
                 "reason": data.get("reason")}, generation=entry["generation"],
            )
            state = "completed" if data["outcome"] == "completed" else "withdrawn"
            self.db.execute(
                "UPDATE lane_entries SET state=?,updated_event=? WHERE candidate_id=?",
                (state, event, data["candidate_id"]),
            )
            return {"candidate_id": data["candidate_id"], "state": state, "event_id": event}


def _require(data: dict[str, Any], fields: tuple[str, ...]) -> None:
    missing = [name for name in fields if name not in data]
    if missing:
        raise StoreError("Ontbrekende verplichte velden: " + ", ".join(missing))


def _full_sha(value: Any, label: str) -> None:
    if not isinstance(value, str) or len(value) != 40 or any(c not in "0123456789abcdef" for c in value.lower()):
        raise StoreError(f"{label} moet een volledige SHA-1 zijn.")
