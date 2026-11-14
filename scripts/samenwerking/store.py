"""SQLite event store and transaction helpers for the local coordinator."""

from __future__ import annotations

import json
import os
import sqlite3
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Iterator


SCHEMA_VERSION = 1


class StoreError(ValueError):
    """A safe, user-displayable store error."""


def validate_database_path(value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        raise StoreError("Het databasepad moet absoluut zijn.")
    resolved = path.resolve()
    repo = Path(__file__).resolve().parents[2]
    if resolved == repo or repo in resolved.parents:
        raise StoreError("De database moet buiten de repository staan.")
    if not path.parent.exists() or not path.parent.is_dir():
        raise StoreError("De bovenliggende map van de database bestaat niet.")
    if path.exists() and not path.is_file():
        raise StoreError("Het databasepad verwijst niet naar een bestand.")
    return resolved


def connect(path: str | Path) -> sqlite3.Connection:
    db_path = validate_database_path(str(path))
    connection = sqlite3.connect(str(db_path), timeout=15, isolation_level=None)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON")
    connection.execute("PRAGMA busy_timeout = 15000")
    connection.execute("PRAGMA journal_mode = WAL")
    connection.execute("PRAGMA synchronous = FULL")
    return connection


@contextmanager
def transaction(connection: sqlite3.Connection) -> Iterator[sqlite3.Connection]:
    connection.execute("BEGIN IMMEDIATE")
    try:
        yield connection
    except BaseException:
        connection.rollback()
        raise
    else:
        connection.commit()


def initialize(connection: sqlite3.Connection, coordinator: str) -> dict[str, Any]:
    if not coordinator.strip():
        raise StoreError("Geef een naam op voor de coordinator.")
    # executescript() manages its own transaction, so install idempotent DDL first.
    connection.executescript(_SCHEMA)
    with transaction(connection):
        version = connection.execute("PRAGMA user_version").fetchone()[0]
        if version not in (0, SCHEMA_VERSION):
            raise StoreError("Deze databaseversie wordt niet ondersteund.")
        connection.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")
        existing = connection.execute(
            "SELECT value FROM metadata WHERE key = 'protocol'"
        ).fetchone()
        if existing is None:
            connection.execute(
                "INSERT INTO metadata(key, value) VALUES('protocol', '1')"
            )
            connection.execute(
                "INSERT INTO metadata(key, value) VALUES('mode', 'shadow')"
            )
            connection.execute(
                "INSERT INTO metadata(key, value) VALUES('coordinator', ?)",
                (coordinator.strip(),),
            )
            append_event(
                connection,
                "coordinator_initialized",
                None,
                coordinator.strip(),
                {"protocol": 1, "mode": "shadow"},
            )
        elif existing[0] != "1":
            raise StoreError("De database gebruikt een ander protocol.")
        else:
            current = connection.execute(
                "SELECT value FROM metadata WHERE key = 'coordinator'"
            ).fetchone()[0]
            if current != coordinator.strip():
                raise StoreError("De coordinatornaam komt niet overeen met de database.")
    return {"protocol": 1, "mode": "shadow", "coordinator": coordinator.strip()}


def append_event(
    connection: sqlite3.Connection,
    event_type: str,
    request_id: str | None,
    actor: str,
    payload: dict[str, Any],
    *,
    generation: int | None = None,
) -> int:
    cursor = connection.execute(
        "INSERT INTO events(event_type, request_id, actor, generation, payload, created_at) "
        "VALUES(?, ?, ?, ?, ?, ?)",
        (
            event_type,
            request_id,
            actor,
            generation,
            json.dumps(payload, sort_keys=True, separators=(",", ":")),
            int(time.time()),
        ),
    )
    return int(cursor.lastrowid)


def load_request(connection: sqlite3.Connection, request_id: str) -> sqlite3.Row:
    row = connection.execute(
        "SELECT * FROM requests WHERE request_id = ?", (request_id,)
    ).fetchone()
    if row is None:
        raise StoreError("Aanvraag niet gevonden.")
    return row


def decode(value: str) -> Any:
    return json.loads(value)


_SCHEMA = """
CREATE TABLE IF NOT EXISTS metadata (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS events (
  event_id INTEGER PRIMARY KEY AUTOINCREMENT,
  event_type TEXT NOT NULL,
  request_id TEXT,
  actor TEXT NOT NULL,
  generation INTEGER,
  payload TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events
BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events
BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
CREATE TABLE IF NOT EXISTS requests (
  request_id TEXT PRIMARY KEY,
  repository TEXT NOT NULL,
  issue INTEGER NOT NULL,
  pr INTEGER NOT NULL,
  branch TEXT NOT NULL,
  base_sha TEXT NOT NULL,
  head_sha TEXT NOT NULL,
  merge_base_sha TEXT NOT NULL,
  implementer_session TEXT NOT NULL,
  reviewer_session TEXT NOT NULL,
  scope TEXT NOT NULL,
  kind TEXT NOT NULL,
  substantive_round INTEGER NOT NULL,
  integration_attempt INTEGER NOT NULL,
  attempt INTEGER NOT NULL,
  generation INTEGER NOT NULL,
  state TEXT NOT NULL,
  authority TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS counters (
  repository TEXT NOT NULL,
  pr INTEGER NOT NULL,
  substantive_rounds INTEGER NOT NULL DEFAULT 0,
  integration_attempts INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(repository, pr)
);
CREATE TABLE IF NOT EXISTS required_checks (
  request_id TEXT NOT NULL REFERENCES requests(request_id),
  check_name TEXT NOT NULL,
  expected_sha TEXT NOT NULL,
  state TEXT NOT NULL DEFAULT 'pending',
  evidence TEXT,
  event_id INTEGER,
  PRIMARY KEY(request_id, check_name)
);
CREATE TABLE IF NOT EXISTS tasks (
  task_id TEXT PRIMARY KEY,
  request_id TEXT NOT NULL REFERENCES requests(request_id),
  classification TEXT NOT NULL,
  purpose TEXT NOT NULL,
  assigned_session TEXT NOT NULL,
  state TEXT NOT NULL,
  evidence TEXT,
  stop_confirmed INTEGER NOT NULL DEFAULT 0,
  cancel_reason TEXT,
  generation INTEGER NOT NULL,
  created_event INTEGER NOT NULL,
  updated_event INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS findings (
  finding_id TEXT PRIMARY KEY,
  repository TEXT NOT NULL,
  pr INTEGER NOT NULL,
  request_id TEXT NOT NULL,
  reporter_session TEXT NOT NULL,
  offered_generation INTEGER NOT NULL,
  severity TEXT NOT NULL,
  evidence TEXT NOT NULL,
  unexpected INTEGER NOT NULL,
  created_event INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS finding_dispositions (
  disposition_id INTEGER PRIMARY KEY AUTOINCREMENT,
  finding_id TEXT NOT NULL REFERENCES findings(finding_id),
  disposition TEXT NOT NULL,
  actor_session TEXT NOT NULL,
  evidence TEXT NOT NULL,
  authorization_ref TEXT,
  event_id INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS lane_entries (
  sequence INTEGER PRIMARY KEY AUTOINCREMENT,
  candidate_id TEXT UNIQUE NOT NULL,
  repository TEXT NOT NULL,
  pr INTEGER NOT NULL,
  dependencies TEXT NOT NULL,
  state TEXT NOT NULL,
  actor_session TEXT,
  expected_base_sha TEXT,
  expected_head_sha TEXT,
  generation INTEGER NOT NULL DEFAULT 0,
  reservation_id TEXT,
  created_event INTEGER NOT NULL,
  updated_event INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS lane_dependencies (
  candidate_id TEXT NOT NULL REFERENCES lane_entries(candidate_id),
  dependency_id TEXT NOT NULL,
  PRIMARY KEY(candidate_id, dependency_id)
);
CREATE TABLE IF NOT EXISTS action_intents (
  action_id TEXT PRIMARY KEY,
  idempotency_key TEXT UNIQUE NOT NULL,
  request_id TEXT NOT NULL,
  action_kind TEXT NOT NULL,
  target TEXT NOT NULL,
  intent TEXT NOT NULL,
  actor_session TEXT NOT NULL,
  attempt INTEGER NOT NULL,
  generation INTEGER NOT NULL,
  state TEXT NOT NULL,
  observed_outcome TEXT,
  observed_reference TEXT,
  created_event INTEGER NOT NULL,
  updated_event INTEGER NOT NULL
);
"""
