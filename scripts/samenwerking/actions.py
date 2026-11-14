"""Audited remote-action intents and input-only crash reconciliation."""

from __future__ import annotations

import sqlite3
from typing import Any

from .store import StoreError, append_event, transaction


class ActionCoordinator:
    def __init__(self, connection: sqlite3.Connection, name: str, identity_check: Any):
        self.db = connection
        self.name = name
        self.identity_check = identity_check

    def intent(self, identity: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("action_id", "idempotency_key", "action_kind", "target", "intent"))
        with transaction(self.db):
            request = self.identity_check(identity, None, states=("ready",))
            if self.db.execute(
                "SELECT 1 FROM action_intents WHERE idempotency_key=? OR action_id=?",
                (data["idempotency_key"], data["action_id"]),
            ).fetchone():
                raise StoreError("Action-intent of idempotency-key bestaat al.")
            event = append_event(
                self.db, "remote_action_intended", request["request_id"], identity["session"],
                {key: data[key] for key in ("action_id", "idempotency_key", "action_kind", "target", "intent")},
                generation=request["generation"],
            )
            self.db.execute(
                "INSERT INTO action_intents VALUES(?,?,?,?,?,?,?,?,?,'pending',NULL,NULL,?,?)",
                (data["action_id"], data["idempotency_key"], request["request_id"],
                 data["action_kind"], data["target"], data["intent"], identity["session"],
                 request["attempt"], request["generation"], event, event),
            )
            return {"action_id": data["action_id"], "state": "pending", "event_id": event}

    def reconcile(self, data: dict[str, Any]) -> dict[str, Any]:
        _require(data, ("action_id", "outcome", "observed_reference", "evidence"))
        if data["outcome"] not in ("applied", "not_applied", "ambiguous"):
            raise StoreError("Uitkomst moet applied, not_applied of ambiguous zijn.")
        if not str(data["evidence"]).strip():
            raise StoreError("Reconciliatie vereist waarnemingsbewijs.")
        with transaction(self.db):
            action = self.db.execute(
                "SELECT * FROM action_intents WHERE action_id=?", (data["action_id"],)
            ).fetchone()
            if action is None:
                raise StoreError("Action-intent niet gevonden.")
            if action["state"] in ("applied", "not_applied"):
                if action["state"] != data["outcome"]:
                    raise StoreError("Definitieve reconciliatie kan niet worden overschreven.")
                return {"action_id": data["action_id"], "state": action["state"], "idempotent": True}
            actor = str(data.get("actor_session", self.name))
            event = append_event(
                self.db, "remote_action_reconciled", action["request_id"], actor,
                {"action_id": data["action_id"], "outcome": data["outcome"],
                 "observed_reference": data["observed_reference"], "evidence": data["evidence"]},
                generation=action["generation"],
            )
            self.db.execute(
                "UPDATE action_intents SET state=?,observed_outcome=?,observed_reference=?,updated_event=? "
                "WHERE action_id=?",
                (data["outcome"], data["outcome"], data["observed_reference"], event, data["action_id"]),
            )
            return {"action_id": data["action_id"], "state": data["outcome"], "event_id": event}


def _require(data: dict[str, Any], fields: tuple[str, ...]) -> None:
    missing = [name for name in fields if name not in data]
    if missing:
        raise StoreError("Ontbrekende verplichte velden: " + ", ".join(missing))
