#!/usr/bin/env bash
# Rood/groen-bewijs voor de repositoryhooks, uitsluitend in tijdelijke lokale git-repositories.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/git-hooks-1810.XXXXXX")"
trap 'rm -rf "$temp_root"' EXIT

# Git leest geen systeem- of gebruikersconfiguratie en schrijft alleen repo-lokale testconfig.
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null

fake_bin="$temp_root/bin"
mkdir -p "$fake_bin"
cat > "$fake_bin/gitleaks" <<'GITLEAKS'
#!/bin/sh
if [ -n "${GITLEAKS_ARGS_FILE:-}" ]; then
  printf '%s\n' "$*" >> "$GITLEAKS_ARGS_FILE"
fi
exit 0
GITLEAKS
chmod +x "$fake_bin/gitleaks"

forbidden_marker='FORBIDDEN_SYNTHETIC_MARKER'
never_match_pattern='SAFE_PATTERN_NEVER_MATCHES'
pattern_default="^${never_match_pattern}$"

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

assert_no_marker_in_output() {
  output_file="$1"
  if grep -q "$forbidden_marker" "$output_file"; then
    fail "a hook printed matched content"
  fi
}

create_repo() {
  repo="$1"
  pattern="$2"
  mkdir -p "$repo/.githooks" "$repo/scripts/ci" "$repo/docs/api-standaarden"
  git init -q "$repo"
  git -C "$repo" checkout -q -b develop
  git -C "$repo" config user.name "Hook Test"
  git -C "$repo" config user.email "trainer@voorbeeld.nl"
  git -C "$repo" config core.hooksPath .githooks
  cp "$repo_root/.githooks/pre-commit" "$repo/.githooks/pre-commit"
  cp "$repo_root/.githooks/pre-push" "$repo/.githooks/pre-push"
  cp "$repo_root/scripts/ci/check-versie-synchroon.sh" "$repo/scripts/ci/check-versie-synchroon.sh"
  chmod +x "$repo/.githooks/pre-commit" "$repo/.githooks/pre-push" "$repo/scripts/ci/check-versie-synchroon.sh"
  printf '%s\n' '/.githooks/sensitive-patterns.txt' > "$repo/.gitignore"
  printf '%s\n' "$pattern" > "$repo/.githooks/sensitive-patterns.txt"
  for project in FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj; do
    mkdir -p "$repo/$(dirname "$project")"
    cat > "$repo/$project" <<'CSPROJ'
<Project>
  <PropertyGroup>
    <Version>3.16.0.0</Version>
    <AssemblyVersion>3.16.0.0</AssemblyVersion>
    <FileVersion>3.16.0.0</FileVersion>
  </PropertyGroup>
</Project>
CSPROJ
  done
  printf 'info:\n  version: 3.16.0.0\n' > "$repo/docs/api-standaarden/openapi.yaml"
  printf '{\n  "version": "3.16.0.0"\n}\n' > "$repo/docs/api-standaarden/openapi.json"
  printf '%s\n' 'baseline' > "$repo/README.md"
  git -C "$repo" add .gitignore .githooks/pre-commit .githooks/pre-push scripts/ci/check-versie-synchroon.sh FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj docs/api-standaarden/openapi.yaml docs/api-standaarden/openapi.json README.md
  if ! git -C "$repo" commit -q -m baseline >"$temp_root/commit.log" 2>&1; then
    fail "temporary baseline commit failed"
  fi
}

run_precommit() {
  repo="$1"
  (cd "$repo" && PATH="$fake_bin:$PATH" ./.githooks/pre-commit)
}

run_pre_push() {
  repo="$1"
  local_sha="$2"
  remote_sha="$3"
  args_file="$4"
  printf 'refs/heads/test %s refs/heads/test %s\n' "$local_sha" "$remote_sha" \
    | (cd "$repo" && PATH="$fake_bin:$PATH" GITLEAKS_ARGS_FILE="$args_file" ./.githooks/pre-push origin local-test-remote)
}

# Missing and empty config block both hooks without printing matched values.
repo="$temp_root/config"
create_repo "$repo" "$pattern_default"
rm "$repo/.githooks/sensitive-patterns.txt"
printf '%s\n' 'ordinary content' > "$repo/config-check.txt"
git -C "$repo" add config-check.txt
if run_precommit "$repo" >"$temp_root/missing-precommit.log" 2>&1; then fail "pre-commit allowed missing local configuration"; fi
if printf '\n' | (cd "$repo" && PATH="$fake_bin:$PATH" ./.githooks/pre-push origin local-test-remote) >"$temp_root/missing-prepush.log" 2>&1; then fail "pre-push allowed missing local configuration"; fi
: > "$repo/.githooks/sensitive-patterns.txt"
if run_precommit "$repo" >"$temp_root/empty-precommit.log" 2>&1; then fail "pre-commit allowed empty local configuration"; fi
if printf '\n' | (cd "$repo" && PATH="$fake_bin:$PATH" ./.githooks/pre-push origin local-test-remote) >"$temp_root/empty-prepush.log" 2>&1; then fail "pre-push allowed empty local configuration"; fi

# An ordinary staged README must not pass when the only rules are blank, comments, or invalid.
# Invoke the actual hooks; the local pattern file is never committed or printed.
for config_case in whitespace comments invalid; do
  case "$config_case" in
    whitespace) printf '  \t  \n\t\n' > "$repo/.githooks/sensitive-patterns.txt" ;;
    comments) printf '  # local comment\n\t# another comment\n' > "$repo/.githooks/sensitive-patterns.txt" ;;
    invalid) printf '  # local comment\n(\n' > "$repo/.githooks/sensitive-patterns.txt" ;;
  esac
  printf '%s\n' "ordinary $config_case content" > "$repo/README.md"
  git -C "$repo" add README.md
  if run_precommit "$repo" >"$temp_root/$config_case-precommit.log" 2>&1; then fail "pre-commit allowed $config_case configuration"; fi
  if printf '\n' | (cd "$repo" && PATH="$fake_bin:$PATH" ./.githooks/pre-push origin local-test-remote) >"$temp_root/$config_case-prepush.log" 2>&1; then fail "pre-push allowed $config_case configuration"; fi
  assert_no_marker_in_output "$temp_root/$config_case-precommit.log"
  assert_no_marker_in_output "$temp_root/$config_case-prepush.log"
done

# Real pre-commit: a configured synthetic match blocks; safe content passes without revealing it.
repo="$temp_root/precommit"
create_repo "$repo" "^${forbidden_marker}$"
printf '%s\n' 'ordinary content' > "$repo/safe.txt"
git -C "$repo" add safe.txt
if ! run_precommit "$repo" >"$temp_root/precommit-safe.log" 2>&1; then fail "pre-commit rejected ordinary staged content"; fi
printf '%s\n' "$forbidden_marker" > "$repo/matched.txt"
git -C "$repo" add matched.txt
if run_precommit "$repo" >"$temp_root/precommit-match.log" 2>&1; then fail "pre-commit accepted a configured synthetic match"; fi
assert_no_marker_in_output "$temp_root/precommit-match.log"

# The existing version checker reads staged metadata, not a later worktree repair.
repo="$temp_root/version"
create_repo "$repo" "$pattern_default"
csproj="$repo/FunctionApp/fa-dev-sportlink-01.csproj"
sed 's/3\.16\.0\.0/3.16.0.1/g' "$csproj" > "$temp_root/version-mismatch.csproj"
cp "$temp_root/version-mismatch.csproj" "$csproj"
git -C "$repo" add FunctionApp/fa-dev-sportlink-01.csproj
sed 's/3\.16\.0\.1/3.16.0.0/g' "$csproj" > "$temp_root/version-fixed.csproj"
cp "$temp_root/version-fixed.csproj" "$csproj"
if run_precommit "$repo" >"$temp_root/staged-version-mismatch.log" 2>&1; then fail "staged version mismatch passed after only a worktree repair"; fi

# Missing checker is a hard failure when an application project is staged.
rm "$repo/scripts/ci/check-versie-synchroon.sh"
if run_precommit "$repo" >"$temp_root/missing-version-checker.log" 2>&1; then fail "pre-commit passed without the required version checker"; fi

# A coherent staged update passes all nine project-version fields.
repo="$temp_root/version-good"
create_repo "$repo" "$pattern_default"
for project in FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj; do
  sed 's/3\.16\.0\.0/3.16.0.1/g' "$repo/$project" > "$temp_root/version-good.csproj"
  cp "$temp_root/version-good.csproj" "$repo/$project"
done
git -C "$repo" add FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj
if ! run_precommit "$repo" >"$temp_root/staged-version-good.log" 2>&1; then fail "coherent staged version update failed"; fi

# New-branch push scans every unpublished commit, including an earlier change removed at the tip.
repo="$temp_root/push-range"
create_repo "$repo" "$pattern_default"
base_sha="$(git -C "$repo" rev-parse HEAD)"
git -C "$repo" update-ref refs/remotes/origin/develop "$base_sha"
printf '%s\n' "$forbidden_marker" > "$repo/transient.txt"
git -C "$repo" add transient.txt
if ! git -C "$repo" commit -q -m "temporary synthetic marker" >"$temp_root/push-commit-one.log" 2>&1; then fail "temporary first push-range commit failed"; fi
first_sha="$(git -C "$repo" rev-parse HEAD)"
rm "$repo/transient.txt"
git -C "$repo" add -u transient.txt
if ! git -C "$repo" commit -q -m "remove synthetic marker" >"$temp_root/push-commit-two.log" 2>&1; then fail "temporary second push-range commit failed"; fi
tip_sha="$(git -C "$repo" rev-parse HEAD)"
printf '%s\n' "^${forbidden_marker}$" > "$repo/.githooks/sensitive-patterns.txt"
args_file="$temp_root/gitleaks-args.log"
if run_pre_push "$repo" "$tip_sha" 0000000000000000000000000000000000000000 "$args_file" >"$temp_root/new-branch-match.log" 2>&1; then fail "new-branch push passed after a marker was removed at its tip"; fi
assert_no_marker_in_output "$temp_root/new-branch-match.log"
if ! grep -q -- '--not --remotes=origin' "$args_file"; then fail "gitleaks did not receive the complete new-branch range"; fi

# A new branch with no unpublished commits succeeds; no false range is invented.
if ! run_pre_push "$repo" "$base_sha" 0000000000000000000000000000000000000000 "$temp_root/empty-range.log" >"$temp_root/empty-range-output.log" 2>&1; then fail "empty new-branch range was rejected"; fi

# Existing-branch range scans only commits after the remote tip, still with the actual hook.
printf '%s\n' 'ordinary follow-up' > "$repo/follow-up.txt"
git -C "$repo" add follow-up.txt
if ! git -C "$repo" commit -q -m "ordinary follow-up" >"$temp_root/push-commit-three.log" 2>&1; then fail "temporary existing-branch commit failed"; fi
followup_sha="$(git -C "$repo" rev-parse HEAD)"
printf '%s\n' "^${forbidden_marker}$" > "$repo/.githooks/sensitive-patterns.txt"
if ! run_pre_push "$repo" "$followup_sha" "$tip_sha" "$temp_root/existing-range-gitleaks.log" >"$temp_root/existing-range-output.log" 2>&1; then fail "safe existing-branch range was rejected"; fi

# Deletion ref updates contain no new commit and remain a no-op when the config is present.
printf 'refs/heads/test %s refs/heads/test %s\n' 0000000000000000000000000000000000000000 "$base_sha" \
  | (cd "$repo" && PATH="$fake_bin:$PATH" ./.githooks/pre-push origin local-test-remote) >"$temp_root/deletion-output.log" 2>&1 || fail "branch deletion update failed"

echo "OK — hook guards reject missing configuration and staged mismatches, and scan complete new-branch ranges."
