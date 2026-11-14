"""Single-writer bounded local write-ahead state for one release operation per tool.

Completed state is retained to prove byte-exact retries. A new operation may
replace completed state; pending state must first be recovered with its original
arguments. State and target replacements are fsynced before advancing.
"""

from __future__ import annotations

import json
import os
import stat
import tempfile
from pathlib import Path
from typing import Callable

MAX_STATE_BYTES = 32 * 1024 * 1024
Writer = Callable[[Path, bytes], None]


class TransactionError(Exception):
    """Recovery cannot prove that the operation is safe to resume."""


def _sync_directory(path: Path) -> None:
    if os.name == "posix":
        descriptor = os.open(path, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def _ensure_directory(path: Path) -> None:
    missing = []
    cursor = path
    while not cursor.exists():
        missing.append(cursor)
        cursor = cursor.parent
    for directory in reversed(missing):
        directory.mkdir()
        _sync_directory(directory.parent)


def write_atomic(path: Path, content: bytes) -> None:
    _ensure_directory(path.parent)
    descriptor, temporary = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            if path.exists():
                os.chmod(temporary, stat.S_IMODE(path.stat().st_mode))
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        _sync_directory(path.parent)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _read(path: Path) -> str | None:
    return path.read_bytes().decode("utf-8") if path.exists() else None


def _encode(state: dict) -> bytes:
    content = (json.dumps(state, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    if len(content) > MAX_STATE_BYTES:
        raise TransactionError("Hersteljournaal overschrijdt de limiet van 32 MiB")
    return content


def _unique_pairs(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise TransactionError("Dubbele sleutel in hersteljournaal")
        result[key] = value
    return result


def _load(path: Path, paths: tuple[str, ...]) -> dict:
    try:
        if path.stat().st_size > MAX_STATE_BYTES:
            raise TransactionError("Hersteljournaal overschrijdt de limiet van 32 MiB")
        state = json.loads(path.read_bytes(), object_pairs_hook=_unique_pairs)
    except (ValueError, UnicodeError) as exc:
        raise TransactionError("Ongeldig hersteljournaal") from exc
    fields = {"schema_version", "identity", "inputs", "status", "before", "after"}
    if not isinstance(state, dict) or set(state) != fields:
        raise TransactionError("Ongeldige velden in hersteljournaal")
    if type(state["schema_version"]) is not int or state["schema_version"] != 1:
        raise TransactionError("Ongesteund hersteljournaal")
    if state["status"] not in ("pending", "completed"):
        raise TransactionError("Ongeldige herstelstatus")
    for field in ("identity", "inputs"):
        if not isinstance(state[field], dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in state[field].items()):
            raise TransactionError("Ongeldige herstelidentiteit of invoer")
    for field in ("before", "after"):
        values = state[field]
        if not isinstance(values, dict) or set(values) != set(paths):
            raise TransactionError("Onverwachte bestanden in hersteljournaal")
        if any(not isinstance(value, str) and not (field == "before" and value is None) for value in values.values()):
            raise TransactionError("Ongeldige bestandsinhoud in hersteljournaal")
    return state


def _verify(repo: Path, state: dict) -> None:
    for relative, after in state["after"].items():
        allowed = (after,) if state["status"] == "completed" else (state["before"][relative], after)
        if _read(repo / relative) not in allowed:
            raise TransactionError(f"Bestand is sinds het hersteljournaal gewijzigd: {relative}")


def _finish(repo: Path, journal: str, state: dict, writer: Writer) -> None:
    _verify(repo, state)
    for relative, content in state["after"].items():
        if _read(repo / relative) != content:
            writer(repo / relative, content.encode("utf-8"))
    _verify(repo, {**state, "status": "completed"})
    writer(repo / journal, _encode({**state, "status": "completed"}))


def resume(repo: Path, journal: str, identity: dict[str, str], paths: tuple[str, ...],
           inputs: dict[str, str], apply: bool, writer: Writer = write_atomic) -> dict[str, str] | None:
    path = repo / journal
    if not path.exists():
        return None
    state = _load(path, paths)
    if state["identity"] != identity:
        if state["status"] == "pending":
            raise TransactionError("Onvoltooide bewerking: herstel eerst met exact dezelfde argumenten")
        return None
    if state["inputs"] != inputs:
        raise TransactionError("Invoer is sinds het hersteljournaal gewijzigd")
    _verify(repo, state)
    if apply and state["status"] == "pending":
        _finish(repo, journal, state, writer)
    return state["after"]


def commit(repo: Path, journal: str, identity: dict[str, str], updates: dict[str, str],
           inputs: dict[str, str], writer: Writer = write_atomic) -> None:
    state = {
        "schema_version": 1, "identity": identity, "inputs": inputs, "status": "pending",
        "before": {relative: _read(repo / relative) for relative in updates}, "after": updates,
    }
    writer(repo / journal, _encode(state))
    _finish(repo, journal, state, writer)
