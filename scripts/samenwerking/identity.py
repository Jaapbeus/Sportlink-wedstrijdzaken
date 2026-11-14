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
