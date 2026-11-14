"""JSON-lines CLI; all commands are local and never call GitHub."""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from typing import Any

from .coordinator import Coordinator
from .store import StoreError, connect, initialize


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Lokale transactieregistratie voor samenwerking (protocol 1, shadow-modus)."
    )
    parser.add_argument("--db", required=True, help="Absoluut databasepad buiten de repository.")
    parser.add_argument("command", choices=(
        "init", "request-create", "claim", "task-register", "task-complete", "task-cancel",
        "finding-record", "finding-disposition", "check-record", "finish", "ready",
        "reassign", "retract", "result-correct", "lane-enqueue", "lane-reserve", "lane-release",
        "action-intent", "reconcile", "status", "events",
    ))
    return parser


def _read_input() -> dict[str, Any]:
    raw = sys.stdin.read()
    if not raw.strip():
        return {}
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise StoreError("JSON-invoer moet één object zijn.")
    return value


def dispatch(command: str, value: dict[str, Any], db: sqlite3.Connection) -> Any:
    if command == "init":
        return initialize(db, str(value.get("coordinator", "")).strip())
    coordinator = Coordinator(db)
    if command == "request-create":
        return coordinator.create_request(value)
    if command == "claim":
        return coordinator.claim(value)
    if command == "task-register":
        return coordinator.register_task(value["identity"], value["task"])
    if command == "task-complete":
        return coordinator.complete_task(value["identity"], value["task"])
    if command == "task-cancel":
        return coordinator.cancel_task(value["identity"], value["task"])
    if command == "finding-record":
        return coordinator.record_finding(value["identity"], value["finding"])
    if command == "finding-disposition":
        return coordinator.disposition_finding(value["identity"], value["disposition"])
    if command == "check-record":
        return coordinator.record_check(value["identity"], value["check"])
    if command == "finish":
        return coordinator.finish(value["identity"], value.get("result"))
    if command == "ready":
        return coordinator.ready(value["identity"])
    if command == "reassign":
        return coordinator.reassign(value)
    if command == "retract":
        return coordinator.retract(value)
    if command == "result-correct":
        return coordinator.correct_result(value)
    if command == "lane-enqueue":
        return coordinator.lane_enqueue(value)
    if command == "lane-reserve":
        return coordinator.lane_reserve(value)
    if command == "lane-release":
        return coordinator.lane_release(value)
    if command == "action-intent":
        return coordinator.action_intent(value["identity"], value["action"])
    if command == "reconcile":
        return coordinator.reconcile(value)
    if command == "status":
        return coordinator.status(str(value.get("request_id", "")))
    if command == "events":
        request_id = value.get("request_id")
        return coordinator.events(request_id if request_id else None)
    raise StoreError("Onbekende opdracht.")


def main(argv: list[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    connection: sqlite3.Connection | None = None
    try:
        connection = connect(arguments.db)
        result = dispatch(arguments.command, _read_input(), connection)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        return 0
    except (StoreError, KeyError, TypeError, ValueError, json.JSONDecodeError, sqlite3.Error) as exc:
        message = str(exc)
        if isinstance(exc, KeyError):
            message = f"Ontbrekend invoerveld: {exc.args[0]}"
        elif isinstance(exc, sqlite3.Error):
            message = "Databasebewerking mislukt; controleer de database en probeer opnieuw."
        elif isinstance(exc, TypeError):
            message = "Ongeldige JSON-structuur voor deze opdracht."
        print(json.dumps({"error": message}, ensure_ascii=False), file=sys.stderr)
        return 2
    finally:
        if connection is not None:
            connection.close()


if __name__ == "__main__":
    raise SystemExit(main())
