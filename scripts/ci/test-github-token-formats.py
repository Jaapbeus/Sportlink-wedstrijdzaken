#!/usr/bin/env python3
"""Offline regressiecontrole; synthetische tokens blijven uitsluitend in geheugen."""

import base64
import json
import os
from pathlib import Path
import random
import re
import shutil
import string
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
RULE = "github-installation-token-compatible"
LEGACY = "ghs_" + "[A-Za-z0-9]{36}"


def fixtures():
    rng = random.Random(1723)
    alphabet = string.ascii_letters + string.digits + "_-"
    encode = lambda value: base64.urlsafe_b64encode(value).decode().rstrip("=")
    header = encode(b'{"alg":"RS256","typ":"JWT"}')
    payload = encode(json.dumps({"iss": "synthetic-only", "exp": 1,
                                "padding": "_-" * 80}).encode())
    prefix = "gh" + "s_"
    classic = prefix + "".join(rng.choice(alphabet[:62]) for _ in range(36))
    start = prefix + "123456_" + header + "." + payload + "."
    # Geen geldige handtekening, geen echte App-ID/claims; nooit een bruikbaar token.
    stateless = start + "".join(rng.choice(alphabet) for _ in range(520 - len(start)))
    longer = stateless + "".join(rng.choice(alphabet) for _ in range(280))
    return classic, stateless, longer


def hook_matches(pattern, value):
    result = subprocess.run(["grep", "-Eiq", pattern], input=value + "\n",
                            text=True, capture_output=True)
    if result.returncode not in (0, 1):
        raise RuntimeError("grep-patrooncontrole mislukt")
    return result.returncode == 0


def scan(config, value):
    env = os.environ.copy()
    # Voorkom dat een machine-specifieke configuratie de regressiecontrole vervangt.
    env.pop("GITLEAKS_CONFIG", None)
    env["GITLEAKS_CONFIG_TOML"] = config
    result = subprocess.run(
        ["gitleaks", "stdin", "--no-banner", "--redact", "--report-format", "json",
         "--report-path", "-"], input="synthetic_fixture = " + value + "\n",
        text=True, capture_output=True, env=env, cwd=ROOT)
    if result.returncode not in (0, 1):
        raise RuntimeError("Gitleaks-proces mislukt")
    findings = json.loads(result.stdout or "[]")
    # Geen ruwe stdout/stderr, match, token of rapport naar testuitvoer schrijven.
    return result.returncode, {item["RuleID"] for item in findings}


class TokenFormats(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("gitleaks"):
            raise RuntimeError("Gitleaks op PATH vereist; controle niet uitgevoerd")
        lines = (ROOT / ".githooks/sensitive-patterns.template.txt").read_text().splitlines()
        cls.pattern = next(line for line in lines if line.startswith("ghs_"))
        cls.config = (ROOT / ".gitleaks.toml").read_text()
        cls.tokens = fixtures()

    def test_fixture_shape(self):
        self.assertEqual([len(t) for t in self.tokens], [40, 520, 800])
        self.assertEqual([t.count(".") for t in self.tokens], [0, 2, 2])

    def test_hook_accepts_all_formats(self):
        for index, token in enumerate(self.tokens):
            with self.subTest(format=index):
                self.assertTrue(hook_matches(self.pattern, token))
                self.assertTrue(hook_matches(self.pattern, token.upper()))

    def test_gitleaks_detects_all_formats(self):
        for index, token in enumerate(self.tokens):
            with self.subTest(format=index):
                code, rules = scan(self.config, token)
                self.assertEqual(code, 1)
                self.assertIn(RULE, rules)

    def test_non_tokens_remain_clean(self):
        for index, value in enumerate(["ordinary text", "gh" + "s_short", "A" * 520]):
            with self.subTest(case=index):
                self.assertFalse(hook_matches(self.pattern, value))
                code, rules = scan(self.config, value)
                self.assertEqual(code, 0)
                self.assertFalse(rules)

    def test_old_hook_mutation_is_detected(self):
        self.assertTrue(hook_matches(LEGACY, self.tokens[0]))
        self.assertFalse(hook_matches(LEGACY, self.tokens[1]))

    def test_old_gitleaks_mutation_is_detected(self):
        pattern = re.search(r"id = \"" + RULE + r"\".*?regex = '''(.*?)'''",
                            self.config, re.DOTALL)
        self.assertIsNotNone(pattern)
        start, end = pattern.span(1)
        mutant = self.config[:start] + LEGACY + self.config[end:]
        _, rules = scan(mutant, self.tokens[1])
        self.assertNotIn(RULE, rules)


if __name__ == "__main__":
    unittest.main()
