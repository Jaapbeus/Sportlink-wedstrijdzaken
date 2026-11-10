#!/usr/bin/env bash
# Regression proof for tier-duplication coverage of local C# source files.
set -euo pipefail

source_root="$(git rev-parse --show-toplevel)"
temp_root="$(mktemp -d)"
trap 'rm -rf "$temp_root"' EXIT
fixture="$temp_root/repository"
log_file="$temp_root/guard.log"

export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null

mkdir -p "$fixture/scripts/ci/lib" \
  "$fixture/FunctionApp" "$fixture/FunctionApp.Postgres"
cp "$source_root/scripts/ci/check-tier-duplicatie.sh" "$fixture/scripts/ci/"
cp "$source_root/scripts/ci/lib/plafond.sh" "$fixture/scripts/ci/lib/"
cat > "$fixture/scripts/ci/codekwaliteit-plafonds.txt" <<'EOF'
tier-duplicatie-regels 1 synthetic-fixture
EOF
cat > "$fixture/.gitignore" <<'EOF'
FunctionApp.Postgres/obj/
EOF

git -C "$fixture" init --quiet -b main
git -C "$fixture" config user.name "Test Author"
git -C "$fixture" config user.email "trainer@voorbeeld.nl"
printf 'namespace Baseline;\n' > "$fixture/FunctionApp/Baseline.cs"
printf 'namespace Baseline;\n' > "$fixture/FunctionApp.Postgres/Baseline.cs"
git -C "$fixture" add .
git -C "$fixture" commit --quiet -m "synthetic baseline"

run_guard() {
  (cd "$fixture" && bash scripts/ci/check-tier-duplicatie.sh) > "$log_file" 2>&1
}

expect_green() {
  if ! run_guard; then
    cat "$log_file"
    echo "FAIL: expected the synthetic baseline to pass."
    exit 1
  fi
  if ! grep -Fq 'OK — tier-duplicatie stijgt niet.' "$log_file"; then
    cat "$log_file"
    echo "FAIL: the guard did not report its green result."
    exit 1
  fi
}

expect_over_limit() {
  if run_guard; then
    echo "FAIL: expected the tier-duplication guard to reject the fixture."
    exit 1
  fi
  if ! grep -Fq '::error::Tier-duplicatie gestegen' "$log_file"; then
    cat "$log_file"
    echo "FAIL: guard failed for a reason other than the duplication ceiling."
    exit 1
  fi
}

expect_green

# Ignored build output must not enter the metric.
mkdir -p "$fixture/FunctionApp/obj" "$fixture/FunctionApp.Postgres/obj"
cat > "$fixture/FunctionApp/obj/Generated.cs" <<'EOF'
namespace IgnoredBuildOutput;
public class Generated
{
    public static string Value => "synthetic";
}
EOF
cp "$fixture/FunctionApp/obj/Generated.cs" "$fixture/FunctionApp.Postgres/obj/Generated.cs"
expect_green

# A new paired behavior is counted before staging, and still counted after staging.
mkdir -p "$fixture/FunctionApp/Planner" "$fixture/FunctionApp.Postgres/Planner"
cat > "$fixture/FunctionApp/Planner/NewShared.cs" <<'EOF'
namespace Fixture;
public class NewShared
{
    public static string Value => "synthetic";
}
EOF
cp "$fixture/FunctionApp/Planner/NewShared.cs" "$fixture/FunctionApp.Postgres/Planner/NewShared.cs"
expect_over_limit

git -C "$fixture" add FunctionApp/Planner/NewShared.cs FunctionApp.Postgres/Planner/NewShared.cs
expect_over_limit

git -C "$fixture" reset --quiet -- FunctionApp/Planner/NewShared.cs FunctionApp.Postgres/Planner/NewShared.cs
rm "$fixture/FunctionApp/Planner/NewShared.cs" "$fixture/FunctionApp.Postgres/Planner/NewShared.cs"
expect_green

echo "OK — paired nonignored C# files are counted before and after staging; ignored output is excluded."
