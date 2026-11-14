"""Identity checks that preserve immutable request snapshot fields."""

from __future__ import annotations

import sqlite3
from typing import Any

from .store import StoreError, load_request


def finding_request(connection: sqlite3.Connection, identity: dict[str, Any]) -> sqlite3.Row:
    required = ("request_id", "repository", "pr", "base_sha", "head_sha", "session", "attempt", "generation")
    missing = [key for key in required if key not in identity]
    if missing:
        raise StoreError("Ontbrekende identiteitsvelden: " + ", ".join(missing))
    request = load_request(connection, identity["request_id"])
    for field in ("repository", "pr", "base_sha", "head_sha"):
        if str(identity[field]) != str(request[field]):
            raise StoreError("Bevinding hoort niet bij deze aanvraag en SHA.")
    if int(identity["generation"]) < 1 or int(identity["attempt"]) < 1:
        raise StoreError("Ongeldige poging of generatie.")
    return request


def exact_request(
    connection,
    identity: dict[str, Any],
    session_column: str | None,
    *,
    offered: bool = False,
    states: tuple[str, ...] | None = None,
    request_id: str | None = None,
) -> sqlite3.Row:
    require(identity, ("request_id", "repository", "pr", "base_sha", "head_sha",
                        "session", "attempt", "generation"))
    if request_id and identity["request_id"] != request_id:
        raise StoreError("Aanvraagidentiteit komt niet overeen.")
    request = load_request(connection, identity["request_id"])
    for field in ("repository", "pr", "base_sha", "head_sha"):
        if str(identity[field]) != str(request[field]):
            raise StoreError("Aanvraagidentiteit of aangeboden SHA komt niet overeen.")
    if int(identity["attempt"]) != request["attempt"] or int(identity["generation"]) != request["generation"]:
        raise StoreError("Aanvraagidentiteit is verouderd; actie geweigerd.")
    if session_column and identity["session"] != request[session_column]:
        raise StoreError("Sessie is niet de exact toegewezen actor.")
    if offered and request["state"] != "offered":
        raise StoreError("Aanvraag is niet beschikbaar om te claimen.")
    if states and request["state"] not in states:
        raise StoreError("Aanvraagstatus staat deze actie niet toe.")
    return request



def require(data, fields):
    missing = [name for name in fields if name not in data]
    if missing:
        raise StoreError("Ontbrekende verplichte velden: " + ", ".join(missing))


def validate_assignment(connection, data):
    active = connection.execute(
        "SELECT 1 FROM requests WHERE repository=? AND pr=? AND state IN ('offered','claimed','reviewing')",
        (data["repository"], data["pr"]),
    ).fetchone()
    if active:
        raise StoreError("Er bestaat al een actieve reviewaanvraag voor deze PR; draag die expliciet over.")
    if data["kind"] == "substantive" and connection.execute(
        "SELECT 1 FROM requests WHERE repository=? AND pr=? AND head_sha=? AND kind='substantive'",
        (data["repository"], data["pr"], data["head_sha"]),
    ).fetchone():
        raise StoreError("Deze SHA heeft al een inhoudelijke aanvraag; gebruik intrekking/herstel en reassign.")
