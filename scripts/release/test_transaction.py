from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import transaction
from test_support import snapshot


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.repo = Path(self.temporary.name)
        self.paths = ("first.txt", "second.txt")
        self.identity = {"target": "3.18.0.0"}
        self.inputs = {"fragment": "original"}
        self.journal = "artifacts/release/test-state.json"
        self.updates = {relative: "after\r\n" for relative in self.paths}
        for relative in self.paths:
            (self.repo / relative).write_bytes(b"before\r\n")

    def tearDown(self):
        self.temporary.cleanup()

    def interrupt(self):
        def stop(path, content):
            if path.name == "second.txt":
                raise OSError("interrupted")
            transaction.write_atomic(path, content)
        with self.assertRaises(OSError):
            transaction.commit(self.repo, self.journal, self.identity, self.updates, self.inputs, stop)

    def resume(self, apply, identity=None, inputs=None):
        return transaction.resume(self.repo, self.journal, identity or self.identity,
                                  self.paths, inputs or self.inputs, apply)

    def test_pending_edits_arguments_or_inputs_fail_before_any_write(self):
        self.interrupt()
        for field in ("file", "identity", "inputs"):
            with self.subTest(field=field):
                target = self.repo / "second.txt"
                before = target.read_bytes()
                if field == "file":
                    target.write_bytes(b"manual edit")
                identity = {"target": "3.19.0.0"} if field == "identity" else None
                inputs = {"fragment": "modified"} if field == "inputs" else None
                interrupted = snapshot(self.repo)
                for apply in (False, True):
                    with self.assertRaises(transaction.TransactionError):
                        self.resume(apply, identity, inputs)
                    self.assertEqual(snapshot(self.repo), interrupted)
                target.write_bytes(before)
        self.assertEqual(self.resume(True), self.updates)
        self.assertEqual((self.repo / "first.txt").read_bytes(), b"after\r\n")

    def test_corrupt_journal_or_unexpected_paths_fail_closed(self):
        self.interrupt()
        journal = self.repo / self.journal
        source = journal.read_bytes()
        for corruption in ("truncated", "duplicate", "path", "before", "status"):
            with self.subTest(corruption=corruption):
                data = json.loads(source)
                if corruption == "truncated":
                    text = "{"
                elif corruption == "duplicate":
                    text = source.decode().replace('"schema_version": 1', '"schema_version": 1, "schema_version": 1')
                else:
                    if corruption == "path":
                        data["after"]["../outside.txt"] = "unexpected"
                    elif corruption == "before":
                        data["before"]["first.txt"] = 42
                    else:
                        data["status"] = "unknown"
                    text = json.dumps(data)
                journal.write_text(text)
                before = snapshot(self.repo)
                for apply in (False, True):
                    with self.assertRaises(transaction.TransactionError):
                        self.resume(apply)
                    self.assertEqual(snapshot(self.repo), before)
        journal.write_bytes(source)
        self.resume(True)

    def test_size_limit_fails_before_creating_journal_or_targets(self):
        before = snapshot(self.repo)
        with patch.object(transaction, "MAX_STATE_BYTES", 1):
            with self.assertRaises(transaction.TransactionError):
                transaction.commit(self.repo, self.journal, self.identity, self.updates, self.inputs)
        self.assertEqual(snapshot(self.repo), before)

    def test_missing_file_is_distinct_from_an_empty_file(self):
        (self.repo / "second.txt").unlink()
        self.interrupt()
        (self.repo / "second.txt").write_bytes(b"")
        before = snapshot(self.repo)
        with self.assertRaises(transaction.TransactionError):
            self.resume(True)
        self.assertEqual(snapshot(self.repo), before)


if __name__ == "__main__":
    unittest.main()
