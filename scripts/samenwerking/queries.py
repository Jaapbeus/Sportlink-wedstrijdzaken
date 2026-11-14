"""Read-only projections of the append-only coordinator history."""

from __future__ import annotations

import sqlite3
from typing import Any

from .store import decode, load_request


def status(connection: sqlite3.Connection, request_id: str) -> dict[str, Any]:
    request = load_request(connection, request_id)
    tasks = connection.execute(
        "SELECT task_id,classification,purpose,assigned_session,state,evidence, "
        "stop_confirmed,cancel_reason FROM tasks WHERE request_id=? ORDER BY created_event",
        (request_id,),
    ).fetchall()
    checks = connection.execute(
        "SELECT check_name,expected_sha,state,evidence FROM required_checks WHERE request_id=? "
        "ORDER BY check_name", (request_id,),
    ).fetchall()
    findings = connection.execute(
        "SELECT f.finding_id,f.severity,f.unexpected,f.evidence,d.disposition,d.evidence AS disposition_evidence "
        "FROM findings f LEFT JOIN finding_dispositions d ON d.disposition_id=("
        "SELECT MAX(d2.disposition_id) FROM finding_dispositions d2 WHERE d2.finding_id=f.finding_id) "
        "WHERE f.repository=? AND f.pr=? ORDER BY f.created_event",
        (request["repository"], request["pr"]),
    ).fetchall()
    return {"request": request_result(request), "tasks": [dict(x) for x in tasks],
            "checks": [dict(x) for x in checks], "findings": [dict(x) for x in findings],
            "events": events(connection, request_id)}


def events(connection: sqlite3.Connection, request_id: str | None) -> list[dict[str, Any]]:
    if request_id is None:
        rows = connection.execute("SELECT * FROM events ORDER BY event_id").fetchall()
    else:
        rows = connection.execute(
            "SELECT * FROM events WHERE request_id=? ORDER BY event_id", (request_id,)
        ).fetchall()
    return [{**dict(row), "payload": decode(row["payload"])} for row in rows]


def request_result(row: sqlite3.Row) -> dict[str, Any]:
    return {key: row[key] for key in (
        "request_id", "repository", "issue", "pr", "branch", "base_sha", "head_sha",
        "merge_base_sha", "implementer_session", "reviewer_session", "scope", "kind",
        "substantive_round", "integration_attempt", "attempt", "generation", "state", "authority",
    )}
