#!/usr/bin/env bash
# check-security-patterns.sh (#1810)
# Scans tracked files without exposing matched content or paths in public Actions output.
set -euo pipefail

MODE="${1:-}"
CLASS=""
FILE_SCOPE="general"
PATTERN_LIST=""
PLACEHOLDER_PATTERN=""

case "$MODE" in
  pii)
    CLASS="PII"
    PATTERN_LIST='
\b06[-\s]?[0-9]{8}\b
\+316[0-9]{8}
[a-z0-9._%+\-]+@hotmail\.(com|nl)
[a-z0-9._%+\-]+@(gmail\.com|outlook\.(com|nl)|ziggo\.nl|kpnmail\.nl|casema\.nl|home\.nl)
BCZ[A-Z][A-Z0-9]{5}
'
    ;;
  pii-docs)
    CLASS="documentation PII"
    FILE_SCOPE="docs"
    PATTERN_LIST='[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}'
    PLACEHOLDER_PATTERN='@(github\.com|anthropic\.com|example\.(com|nl|org)|voorbeeld\.nl|allstars-fc\.test)$'
    ;;
  history-email)
    CLASS="commit-message email"
    FILE_SCOPE="history"
    PATTERN_LIST='[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}'
    PLACEHOLDER_PATTERN='@(github\.com|anthropic\.com|example\.(com|nl|org)|voorbeeld\.nl)$'
    ;;
  history-phone)
    CLASS="commit-message phone"
    FILE_SCOPE="history"
    PATTERN_LIST='\b06[-\s]?[0-9]{8}\b|\+316[0-9]{8}'
    ;;
  url-logging)
    CLASS="URL logging"
    FILE_SCOPE="csharp"
    PATTERN_LIST='Log(Trace|Debug|Information|Warning|Error|Critical)\([^;]*\{(Url|ApiUrl|Uri|RequestUri|FullUrl)\}'
    ;;
  exception-output)
    CLASS="exception output"
    FILE_SCOPE="csharp"
    PATTERN_LIST='Console\.(Error\.)?Write(Line)?\([^;]*\{(ex|e|err|error|exception)(\.[A-Za-z]+(\(\))?)?\}'
    ;;
  infra-structural)
    CLASS="infrastructure"
    FILE_SCOPE="infra"
    PLACEHOLDER_PATTERN='\[clubcode\]|\[swa-url\]|\[TENANT_ID\]|\[CLIENT_ID\]|\[sql-servernaam\]|\[beheerder\]|<clubcode>|func-\[clubcode\]|swa-\[clubcode\]'
    PATTERN_LIST='
func-[a-z]{2,}-sportlink
swa-[a-z]{2,}-sportlink
st[a-z]{3,}sportlink
ai-[a-z]{2,}-sportlink
[a-z0-9-]+\.[0-9]+\.azurestaticapps\.net
[a-z0-9-]+-sportlink\.azurewebsites\.net
[a-z0-9][a-z0-9-]{2,}\.database\.windows\.net
'
    ;;
  infra-private)
    CLASS="private infrastructure"
    FILE_SCOPE="infra"
    PLACEHOLDER_PATTERN='\[clubcode\]|\[swa-url\]|\[TENANT_ID\]|\[CLIENT_ID\]|\[sql-servernaam\]|\[beheerder\]|<clubcode>|func-\[clubcode\]|swa-\[clubcode\]'
    ;;
  *)
    echo "::error::Security pattern scanner received an invalid mode." >&2
    exit 2
    ;;
esac

for tool in git perl mktemp; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "::error::A required security pattern scanner component is unavailable." >&2
    exit 2
  }
done

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "::error::Security pattern scanner could not inspect tracked files." >&2
  exit 2
fi

if [ "$MODE" = "infra-private" ]; then
  configured_patterns="${CLUB_EXTRA_PATTERNS:-}"
  while IFS= read -r pattern || [ -n "$pattern" ]; do
    [ -z "$pattern" ] && continue
    case "$pattern" in
      \#*) continue ;;
    esac
    if [ -n "$PATTERN_LIST" ]; then
      PATTERN_LIST="${PATTERN_LIST}"$'\n'"${pattern}"
    else
      PATTERN_LIST="$pattern"
    fi
  done <<< "$configured_patterns"

  if [ -z "$PATTERN_LIST" ]; then
    echo "Private infrastructure pattern scan: SKIPPED (configuration unavailable)."
    exit 0
  fi
fi

if [ -z "$PATTERN_LIST" ]; then
  echo "::error::Security pattern scanner has no configured rules for the ${CLASS} class." >&2
  exit 2
fi

if [ "$FILE_SCOPE" = "history" ]; then
  # Stream only the existing last-20-message window through the matcher. Commit contents are
  # never written to a temporary file, passed as arguments, or included in diagnostics.
  if git log -20 -z --format=%B 2>/dev/null | SECURITY_PATTERNS="$PATTERN_LIST" \
    PLACEHOLDER_PATTERN="$PLACEHOLDER_PATTERN" SECURITY_CLASS="$CLASS" perl -e '
      no re "eval";
      my $source = defined $ENV{"SECURITY_PATTERNS"} ? $ENV{"SECURITY_PATTERNS"} : "";
      my @source_patterns = grep { length $_ } split /\n/, $source;
      exit 2 unless @source_patterns;
      my @patterns;
      for my $pattern (@source_patterns) {
        my $compiled = eval { qr/$pattern/i };
        exit 2 if $@ || !defined $compiled;
        push @patterns, $compiled;
      }
      my $placeholder_source = defined $ENV{"PLACEHOLDER_PATTERN"} ? $ENV{"PLACEHOLDER_PATTERN"} : "";
      my $placeholder;
      if (length $placeholder_source) {
        $placeholder = eval { qr/$placeholder_source/i };
        exit 2 if $@ || !defined $placeholder;
      }
      local $/;
      my $history = <STDIN>;
      exit 2 unless defined $history;
      my @messages = split /\0/, $history;
      my $count = 0;
      for my $message (@messages) {
        next unless length $message;
        my $matched = 0;
        for my $line (split /\n/, $message) {
          next if $line =~ /Co-Authored-By/i;
          for my $pattern (@patterns) {
            while ($line =~ /($pattern)/g) {
              next if defined $placeholder && $1 =~ $placeholder;
              $matched = 1;
              last;
            }
            last if $matched;
          }
          last if $matched;
        }
        $count++ if $matched;
      }
      my $class = defined $ENV{"SECURITY_CLASS"} ? $ENV{"SECURITY_CLASS"} : "commit history";
      print "$class: $count of the last 20 commit messages matched.\n";
      exit($count ? 1 : 0);
    ' 2>/dev/null; then
    exit 0
  else
    status=$?
    if [ "$status" -eq 1 ]; then
      exit 1
    fi
    echo "::error::Security pattern scanner could not complete the ${CLASS} check." >&2
    exit 2
  fi
fi

# Compile patterns in memory from environment variables. Never put configured patterns in argv,
# temporary files, standard output, or diagnostics.
if ! SECURITY_PATTERNS="$PATTERN_LIST" PLACEHOLDER_PATTERN="$PLACEHOLDER_PATTERN" perl -e '
  my $source = defined $ENV{"SECURITY_PATTERNS"} ? $ENV{"SECURITY_PATTERNS"} : "";
  my @patterns = grep { length $_ } split /\n/, $source;
  exit 2 unless @patterns;
  for my $pattern (@patterns) {
    my $compiled = eval { qr/$pattern/ };
    exit 2 if $@ || !defined $compiled;
  }
  my $excluded = defined $ENV{"PLACEHOLDER_PATTERN"} ? $ENV{"PLACEHOLDER_PATTERN"} : "";
  if (length $excluded) {
    my $compiled = eval { qr/$excluded/ };
    exit 2 if $@ || !defined $compiled;
  }
' >/dev/null 2>&1; then
  echo "::error::Security pattern scanner could not validate the ${CLASS} rules." >&2
  exit 2
fi

umask 077
if ! FILE_LIST=$(mktemp "${TMPDIR:-/tmp}/security-pattern-files.XXXXXX" 2>/dev/null); then
  echo "::error::Security pattern scanner could not prepare its tracked-file list." >&2
  exit 2
fi
trap 'rm -f "$FILE_LIST"' EXIT
if ! git ls-files -z > "$FILE_LIST"; then
  echo "::error::Security pattern scanner could not enumerate tracked files." >&2
  exit 2
fi

MATCHED_FILES=0
if SECURITY_PATTERNS="$PATTERN_LIST" PLACEHOLDER_PATTERN="$PLACEHOLDER_PATTERN" \
  FILE_SCOPE="$FILE_SCOPE" SECURITY_CLASS="$CLASS" perl -e '
    no re "eval";
    my $source = defined $ENV{"SECURITY_PATTERNS"} ? $ENV{"SECURITY_PATTERNS"} : "";
    my @source_patterns = grep { length $_ } split /\n/, $source;
    exit 2 unless @source_patterns;
    my @patterns;
    for my $pattern (@source_patterns) {
      my $compiled = eval { qr/$pattern/ };
      exit 2 if $@ || !defined $compiled;
      push @patterns, $compiled;
    }

    my $placeholder_source = defined $ENV{"PLACEHOLDER_PATTERN"} ? $ENV{"PLACEHOLDER_PATTERN"} : "";
    my $placeholder;
    if (length $placeholder_source) {
      $placeholder = eval { qr/$placeholder_source/ };
      exit 2 if $@ || !defined $placeholder;
    }

    my $scope = defined $ENV{"FILE_SCOPE"} ? $ENV{"FILE_SCOPE"} : "general";
    my $class = defined $ENV{"SECURITY_CLASS"} ? $ENV{"SECURITY_CLASS"} : "security";
    open my $paths, "<:raw", $ARGV[0] or exit 2;
    local $/ = "\0";
    my $matched_files = 0;
    while (defined(my $path = <$paths>)) {
      chop $path;
      next if $path =~ /\.(?:png|jpg|jpeg|gif|ico|woff|ttf|eot|svg|pdf|zip|dll|exe|pdb|nupkg)$/;
      next if $scope eq "infra" && $path =~ /sensitive-patterns\.template\.txt$/;
      next if $scope eq "docs" && !($path =~ m{(?:^|/)docs/[\s\S]+\.md$} ||
        $path =~ m{^(?:CHANGELOG|README|SETUP|CONTRIBUTING)\.md$});
      if ($scope eq "csharp") {
        next unless $path =~ /\.cs$/;
        next if $path =~ /(?:^|\/)[^\/]*Tests\//;
      }

      open my $content, "<:raw", "./$path" or exit 2;
      my $matched = 0;
      {
        local $/ = "\n";
        $! = 0;
        while (my $line = <$content>) {
          next if $scope eq "infra" && defined $placeholder && $line =~ $placeholder;
          for my $pattern (@patterns) {
            while ($line =~ /($pattern)/g) {
              next if $scope eq "docs" && defined $placeholder && $1 =~ $placeholder;
              $matched = 1;
              last;
            }
            last if $matched;
          }
          last if $matched;
        }
        exit 2 if $!;
      }
      close $content or exit 2;
      $matched_files++ if $matched;
    }
    close $paths or exit 2;
    print "$class: $matched_files tracked file(s) matched.\n";
    exit($matched_files ? 1 : 0);
  ' -- "$FILE_LIST" 2>/dev/null; then
  exit 0
else
  status=$?
  if [ "$status" -eq 1 ]; then
    exit 1
  fi
  echo "::error::Security pattern scanner could not complete the ${CLASS} check." >&2
  exit 2
fi
