"""Failure injection shared by release preparation tests."""

from pathlib import Path
import shutil
import tempfile
from unittest.mock import patch


def snapshot(repo: Path) -> dict:
    return {str(path.relative_to(repo)): (path.read_bytes(), path.stat().st_mode)
            for path in repo.rglob("*") if path.is_file()}


def exercise_failures(test, repo, module, run, boundaries):
    with tempfile.TemporaryDirectory() as temporary:
        expected_repo = Path(temporary) / "expected"
        shutil.copytree(repo, expected_repo)
        expected_preview = run(expected_repo, False)
        run(expected_repo, True)
        expected = snapshot(expected_repo)
        for boundary in range(1, boundaries + 1):
            for after in (False, True):
                with test.subTest(boundary=boundary, after=after):
                    case = Path(temporary) / f"case-{boundary}-{after}"
                    shutil.copytree(repo, case)
                    calls = 0
                    real_write = module._write_atomic

                    def interrupt(path, content):
                        nonlocal calls
                        calls += 1
                        if calls == boundary and not after:
                            raise OSError("simulated interruption before write")
                        real_write(path, content)
                        if calls == boundary and after:
                            raise OSError("simulated interruption after write")

                    with patch.object(module, "_write_atomic", side_effect=interrupt):
                        with test.assertRaises(OSError):
                            run(case, True)
                    interrupted = snapshot(case)
                    test.assertEqual(run(case, False), expected_preview)
                    test.assertEqual(snapshot(case), interrupted)
                    run(case, True)
                    test.assertEqual(snapshot(case), expected)
                    run(case, True)
                    test.assertEqual(snapshot(case), expected)
