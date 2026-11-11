#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HELPER="$ROOT/scripts/ci/check-security-patterns.sh"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEMP_ROOT"' EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

new_repo() {
  REPO="$TEMP_ROOT/$1"
  mkdir -p "$REPO"
  git init --quiet "$REPO"
}

track_repo() {
  git -C "$REPO" add --all
}

run_mode() {
  mode="$1"
  shift
  if (cd "$REPO" && env "$@" /bin/bash "$HELPER" "$mode") > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
    SCAN_STATUS=0
  else
    SCAN_STATUS=$?
  fi
}

assert_match_count() {
  expected_class="$1"
  expected_count="$2"
  [ "$SCAN_STATUS" -eq 1 ] || fail "expected a finding result"
  grep -Fq "$expected_class: $expected_count tracked file(s) matched." "$TEMP_ROOT/stdout" \
    || fail "class count was not reported for $expected_class: $(cat "$TEMP_ROOT/stdout")"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "finding result wrote unexpected diagnostics"
}

assert_no_match_count() {
  expected_class="$1"
  [ "$SCAN_STATUS" -eq 0 ] || fail "expected a clean result"
  grep -Fq "$expected_class: 0 tracked file(s) matched." "$TEMP_ROOT/stdout" \
    || fail "clean class count was not reported"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "clean result wrote unexpected diagnostics"
}

assert_history_match_count() {
  expected_class="$1"
  expected_count="$2"
  [ "$SCAN_STATUS" -eq 1 ] || fail "expected a history finding result"
  grep -Fq "$expected_class: $expected_count of the last 20 commit messages matched." "$TEMP_ROOT/stdout" \
    || fail "history class count was not reported"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "history finding wrote unexpected diagnostics"
}

assert_history_no_match_count() {
  expected_class="$1"
  [ "$SCAN_STATUS" -eq 0 ] || fail "expected a clean history result"
  grep -Fq "$expected_class: 0 of the last 20 commit messages matched." "$TEMP_ROOT/stdout" \
    || fail "clean history count was not reported"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "clean history result wrote unexpected diagnostics"
}

# Synthetic PII-shaped fixtures exercise tracked names containing spaces, newlines and a leading dash.
new_repo pii-paths
space_file="$REPO/dir with spaces/input.txt"
newline_file="$REPO/line"$'\n'"break.txt"
dash_file="$REPO/-leading-dash.txt"
phone_prefix='06-'
phone_suffix='00000000'
plus_prefix='+'
plus_suffix='31600000000'
mail_user='synthetic'
mail_at='@'
mail_domain='hotmail'
mail_suffix='.com'
mkdir -p "$(dirname "$space_file")"
mkdir -p "$(dirname "$newline_file")"
printf '%s%s\n' "$mail_user$mail_at$mail_domain" "$mail_suffix" > "$space_file"
printf '%s%s\n' "$phone_prefix" "$phone_suffix" > "$newline_file"
printf '%s%s\n' "$plus_prefix" "$plus_suffix" > "$dash_file"
mkdir -p "$REPO/other"
printf '%s%s%s\n' "$mail_user" "$mail_at" 'gmail.com' > "$REPO/other/personal.txt"
member_prefix='BCZ'
member_middle='Q'
member_suffix='12345'
printf '%s%s%s\n' "$member_prefix" "$member_middle" "$member_suffix" > "$REPO/other/member.txt"
printf '%s%s\n' "$phone_prefix" "$phone_suffix" > "$REPO/other/ignored.png"
printf 'x%s%sx\n' "$phone_prefix" "$phone_suffix" > "$REPO/other/boundary.txt"
track_repo
run_mode pii
assert_match_count PII 5
for marker in "$phone_suffix" "$mail_domain" "$member_prefix" "dir with spaces" $'line\nbreak' '-leading-dash'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "PII scan output exposed synthetic fixture data or a path"
  fi
done

# A clean repository remains a successful, explicit zero-count result.
new_repo clean
printf 'ordinary synthetic documentation\n' > "$REPO/readme.txt"
track_repo
run_mode pii
assert_no_match_count PII

# All existing structural infrastructure pattern forms retain positive matches.
new_repo infra-structural
function_prefix='func-'
function_suffix='-sportlink'
swa_prefix='swa-'
storage_prefix='st'
storage_middle='abc'
insights_prefix='ai-'
dot='.'
static_prefix='synthetic'
static_middle='1'
function_host_suffix='-sportlink'
sql_host_prefix='synthetic'
mkdir -p "$REPO/resources"
printf '%s%s%s\n' "$function_prefix" 'aa' "$function_suffix" > "$REPO/resources/a.txt"
printf '%s%s%s\n' "$swa_prefix" 'aa' "$function_suffix" > "$REPO/resources/b.txt"
printf '%s%s%s\n' "$storage_prefix" "$storage_middle" 'sportlink' > "$REPO/resources/c.txt"
printf '%s%s%s\n' "$insights_prefix" 'aa' "$function_suffix" > "$REPO/resources/d.txt"
printf '%s%s%s%s%s%s%s\n' "$static_prefix" "$dot" "$static_middle" "$dot" 'azurestaticapps' "$dot" 'net' > "$REPO/resources/e.txt"
printf '%s%s%s%s%s\n' "$static_prefix" "$function_host_suffix" "$dot" 'azurewebsites' '.net' > "$REPO/resources/f.txt"
printf '%s%s%s%s%s\n' "$sql_host_prefix" "$dot" 'database' "$dot" 'windows.net' > "$REPO/resources/g.txt"
printf '%s%s%s <clubcode>\n' "$function_prefix" 'aa' "$function_suffix" > "$REPO/resources/placeholder.txt"
printf '%s%s%s\n' "$function_prefix" 'aa' "$function_suffix" > "$REPO/resources/sensitive-patterns.template.txt"
track_repo
run_mode infra-structural
assert_match_count infrastructure 7
for marker in "$function_prefix" "$static_prefix" 'azurestaticapps' 'database.windows.net'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "infrastructure scan output exposed a synthetic value"
  fi
done

# The placeholder exclusion applies to the whole matching line.
new_repo infra-placeholder
printf '%s%s%s <clubcode>\n' "$function_prefix" 'aa' "$function_suffix" > "$TEMP_ROOT/placeholder.txt"
git -C "$REPO" add --all
run_mode infra-structural
assert_no_match_count infrastructure

# Configured private patterns arrive through the environment and are never echoed.
new_repo private-pattern
private_prefix='SYNTHETIC_CREDENTIAL'
private_suffix='_123'
printf '%s%s\n' "$private_prefix" "$private_suffix" > "$REPO/config.txt"
track_repo
private_pattern='SYNTHETIC_[A-Z]+_[0-9]{3}'
run_mode infra-private "CLUB_EXTRA_PATTERNS=$private_pattern"
assert_match_count 'private infrastructure' 1
for marker in "$private_prefix" 'SYNTHETIC_[A-Z]+'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "private pattern scan output exposed synthetic configuration"
  fi
done

run_mode infra-private CLUB_EXTRA_PATTERNS=
[ "$SCAN_STATUS" -eq 0 ] || fail "missing optional private patterns should be an explicit skip"
grep -Fq 'SKIPPED (configuration unavailable)' "$TEMP_ROOT/stdout" \
  || fail "missing optional private patterns were not reported as skipped"

run_mode infra-private 'CLUB_EXTRA_PATTERNS=SYNTHETIC_INVALID_PATTERN['
[ "$SCAN_STATUS" -eq 2 ] || fail "invalid configured pattern did not fail closed"
grep -Fq 'could not validate' "$TEMP_ROOT/stderr" || fail "invalid pattern did not report a generic scanner error"
if grep -Fq 'SYNTHETIC_INVALID_PATTERN' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
  fail "invalid configured pattern appeared in scanner output"
fi

# C# log scans skip test projects but still detect production templates without printing lines.
new_repo source-logs
mkdir -p "$REPO/src/Production" "$REPO/src/ExampleTests"
printf '%s%s%s%s\n' 'logger.LogInformation("URL {' 'RequestUri' '}", requestUri);' '' > "$REPO/src/Production/Source.cs"
cp "$REPO/src/Production/Source.cs" "$REPO/src/ExampleTests/Source.cs"
track_repo
run_mode url-logging
assert_match_count 'URL logging' 1
run_mode exception-output
assert_no_match_count 'exception output'
printf '%s%s%s%s\n' 'Console.Error.WriteLine("Failure {' 'ex' '.Message}", ex);' '' > "$REPO/src/Production/Source.cs"
track_repo
run_mode exception-output
assert_match_count 'exception output' 1
for marker in 'RequestUri' 'ex.Message' 'Production/Source.cs'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "source scan output exposed a synthetic source line or path"
  fi
done

# Missing scanner dependencies fail closed before scanning.
NO_PERL_PATH="$TEMP_ROOT/no-perl-bin"
mkdir -p "$NO_PERL_PATH"
ln -s "$(command -v git)" "$NO_PERL_PATH/git"
if (cd "$REPO" && PATH="$NO_PERL_PATH" /bin/bash "$HELPER" pii) > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
  fail "missing Perl scanner should fail closed"
fi
grep -Fq 'required security pattern scanner component is unavailable' "$TEMP_ROOT/stderr" \
  || fail "missing scanner did not produce a generic error: $(cat "$TEMP_ROOT/stderr")"
if grep -Fq 'Production/Source.cs' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
  fail "missing scanner output exposed a file path"
fi

# Documentation scanning keeps its exemptions and redacts both addresses and paths.
new_repo documentation-pii
mkdir -p "$REPO/docs/nested dir"
doc_path="$REPO/docs/nested dir/review"$'\n'"notes.md"
allowed_prefix='noreply'
allowed_domain='@github.com'
reserved_prefix='synthetic'
reserved_domain='@example.invalid'
second_reserved_prefix='another'
printf '%s%s %s%s %s%s\n' \
  "$allowed_prefix" "$allowed_domain" "$reserved_prefix" "$reserved_domain" \
  "$second_reserved_prefix" "$reserved_domain" > "$doc_path"
printf '%s\n' 'sample@example.org trainer@allstars-fc.test' > "$REPO/docs/allowed.md"
printf '%s%s\n' "$reserved_prefix" "$reserved_domain" > "$REPO/docs/ordinary.md"
track_repo

# Capture the former matching pipeline privately and assert it exposed synthetic data.
legacy_docs=$(cd "$REPO" && grep -Eio '[[:alnum:]._%+-]+@[[:alnum:].-]+\.[[:alpha:]]{2,}' docs/ordinary.md \
  | grep -Evi '@(github\.com|anthropic\.com|example\.(com|nl|org)|voorbeeld\.nl|allstars-fc\.test)$' || true)
printf '%s' "$legacy_docs" | grep -Fq "$reserved_prefix$reserved_domain" \
  || fail "legacy documentation matcher did not reproduce its synthetic value-leak case"
run_mode pii-docs
assert_match_count 'documentation PII' 2
for marker in "$reserved_prefix$reserved_domain" "$second_reserved_prefix$reserved_domain" 'ordinary.md' 'review'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "documentation PII output exposed synthetic content or a path"
  fi
done

# Recent history retains the last-20 window, exclusions, and warning/failure class distinctions.
new_repo history-pii
git -C "$REPO" config user.name 'Synthetic Test'
git -C "$REPO" config user.email 'synthetic@example.invalid'
email_prefix='synthetic'
email_domain='@example.invalid'
coauthor_phone='06-'
coauthor_phone_suffix='00000000'
mkdir -p "$REPO/src"
printf 'synthetic fixture\n' > "$REPO/src/input.txt"
git -C "$REPO" add --all
git -C "$REPO" commit --quiet -m "Synthetic note $email_prefix$email_domain" \
  -m "Co-Authored-By: Synthetic Agent <synthetic@example.invalid> $coauthor_phone$coauthor_phone_suffix"
legacy_history=$(git -C "$REPO" log -20 --pretty=format:'%s %b' \
  | grep -Eio '[[:alnum:]._%+-]+@[[:alnum:].-]+\.[[:alpha:]]{2,}' \
  | grep -Evi '@(github\.com|anthropic\.com|example\.(com|nl|org)|voorbeeld\.nl)$' || true)
printf '%s' "$legacy_history" | grep -Fq "$email_prefix$email_domain" \
  || fail "legacy history matcher did not reproduce its synthetic value-leak case"
run_mode history-email
assert_history_match_count 'commit-message email' 1
run_mode history-phone
assert_history_no_match_count 'commit-message phone'

# A phone in an ordinary commit subject is still a blocking finding; output stays class-only.
phone_prefix='06-'
phone_suffix='00000000'
printf 'another synthetic fixture\n' > "$REPO/src/next.txt"
git -C "$REPO" add --all
git -C "$REPO" commit --quiet -m "Synthetic phone $phone_prefix$phone_suffix"
run_mode history-phone
assert_history_match_count 'commit-message phone' 1
for marker in "$email_prefix$email_domain" "$phone_prefix$phone_suffix" 'Synthetic phone'; do
  if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "commit-history output exposed synthetic content"
  fi
done

echo "PASS: security pattern matcher handles unusual tracked paths, preserves pattern classes, and keeps public output value-free."
