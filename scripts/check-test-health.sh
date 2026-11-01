#!/bin/sh
# Manage the compile-time test-health ratchet from the repository root.
# Callers own build-slot scheduling. TESTHEALTH_BUILD_LOG supplies a canned report log for tests.
set -eu

BASELINE=.testhealth-baseline
mode=${1:-}
case "$mode" in
  --init|--update|--preview) ;;
  *) echo "Usage: sh scripts/check-test-health.sh --init | --update | --preview" >&2; exit 2 ;;
esac
if [ "$#" -ne 1 ]; then
  echo "check-test-health: expected exactly one mode argument" >&2
  exit 2
fi

has_entries() {
  awk '$1 ~ /^[0-9]+$/ && NF >= 3 { found = 1; exit } END { exit !found }' "$BASELINE"
}

if [ "$mode" = --init ]; then
  if [ -f "$BASELINE" ] && has_entries; then
    echo "check-test-health: $BASELINE already has entries. Use --update to shrink it." >&2
    exit 1
  fi
  # The analyzer is silent without this file, including in report mode.
  if [ ! -f "$BASELINE" ]; then : >"$BASELINE"; fi
elif [ ! -f "$BASELINE" ]; then
  echo "check-test-health: no $BASELINE. Create one with --init." >&2
  exit 2
fi

tmp=$(mktemp -d "${TMPDIR:-/tmp}/khaozengine-test-health.XXXXXX")
trap 'rm -rf "$tmp"' 0
trap 'exit 2' HUP INT TERM
log=${TESTHEALTH_BUILD_LOG:-$tmp/build.log}
if [ -z "${TESTHEALTH_BUILD_LOG:-}" ]; then
  if ! dotnet build KhaozEngine.slnx -c Release --no-incremental \
      -p:KhaozTestHealthMode=report -p:TreatWarningsAsErrors=false >"$log" 2>&1; then
    cat "$log" >&2
    echo "check-test-health: report build failed. Baseline entries were not changed." >&2
    exit 1
  fi
fi

# Each diagnostic carries the file total. Findings repeat at every location, in the build summary,
# and across projects that compile test-shared sources. Keep the largest total, never their sum.
# POSIX awk has no capture-array extension to match(), so peel the matched prefix in stages.
# KETEST003 is a report-only review aid that the analyzer never enforces, so it never enters the baseline.
awk '
  BEGIN { quote = sprintf("%c", 39) }
  {
    pattern = "(warning|error) KETEST00[12]: " quote "[^" quote "]+" quote " has [0-9]+ "
    if (!match($0, pattern)) next
    finding = substr($0, RSTART, RLENGTH)
    sub(/^(warning|error) /, "", finding)
    rule = substr(finding, 1, index(finding, ":") - 1)
    sub(/^[^:]+: /, "", finding)
    finding = substr(finding, 2)
    end = index(finding, quote)
    path = substr(finding, 1, end - 1)
    count = substr(finding, end + 6) + 0
    key = rule SUBSEP path
    if (!(key in counts) || count > counts[key]) counts[key] = count
    rules[key] = rule
    paths[key] = path
  }
  END {
    for (key in counts) printf "%s\t%s\t%d\n", paths[key], rules[key], counts[key]
  }
' "$log" >"$tmp/counts-unsorted"
LC_ALL=C sort -t "$(printf '\t')" -k1,1 -k2,2 "$tmp/counts-unsorted" >"$tmp/counts"

if [ "$mode" = --init ]; then
  cat >"$tmp/baseline" <<'HEADER'
# KhaozEngine test-health baseline for KETEST001 and KETEST002. KETEST003 is report-only and never listed.
# Entries are '<count> <rule> <path>', relative to this directory.
# This baseline may only shrink. New debt is not allowed.
# With owner approval, exempt a rule and path with a reason comment:
# exempt KETEST001 path/to/File.cs
# Ratchet down locally:
# build-slot --heavy --label testhealth-update -- sh scripts/check-test-health.sh --update

HEADER
  # Preserve adoption-time comments and exemptions below the new header.
  awk '{ print }' "$BASELINE" >>"$tmp/baseline"
  awk -F '\t' '{ print $3 " " $2 " " $1 }' "$tmp/counts" >>"$tmp/baseline"
  mv "$tmp/baseline" "$BASELINE"
  echo "check-test-health: wrote $BASELINE."
  exit 0
fi

# A supplied log with no findings over a populated baseline usually came from an incremental or failed
# build, which prints nothing for up-to-date projects. The script's own build is non-incremental and
# succeeded by this point, so an empty result from it means the debt is gone and the entries may go too.
if [ -n "${TESTHEALTH_BUILD_LOG:-}" ] && [ ! -s "$tmp/counts" ] && has_entries; then
  echo "check-test-health: no KETEST001 or KETEST002 diagnostics in the supplied log over a populated baseline. An incremental or failed build is the likely cause. Run without TESTHEALTH_BUILD_LOG to build fresh." >&2
  exit 2
fi

awk -F '\t' -v changes="$tmp/changes" -v violations="$tmp/violations" '
  FILENAME == ARGV[1] {
    key = $2 SUBSEP $1
    counts[key] = $3 + 0
    rules[key] = $2
    paths[key] = $1
    ordered[++pairs] = key
    next
  }
  {
    original[++lines] = $0
    entry = $0
    sub(/\r$/, "", entry)
    sub(/^[ \t]+/, "", entry)
    if (entry !~ /^([0-9]+|exempt)[ \t]+[^ \t]+[ \t]+.+$/) next
    split(entry, fields, /[ \t]+/)
    path = entry
    sub(/^[^ \t]+[ \t]+[^ \t]+[ \t]+/, "", path)
    key = fields[2] SUBSEP path
    if (fields[1] == "exempt") { exempt[key] = 1; next }
    keys[lines] = key
    limits[lines] = fields[1] + 0
    if (!(key in allowances)) allowances[key] = limits[lines]
    rules[key] = fields[2]
    paths[key] = path
  }
  END {
    for (i = 1; i <= lines; i++) {
      if (!(i in keys)) { print original[i]; continue }
      key = keys[i]
      count = (key in counts) ? counts[key] : 0
      if (count == 0) {
        printf "drop %s %s (was %d)\n", rules[key], paths[key], limits[i] > changes
      } else if (count < limits[i]) {
        printf "%d %s %s\n", count, rules[key], paths[key]
        printf "lower %s %s from %d to %d\n", rules[key], paths[key], limits[i], count > changes
      } else {
        print original[i]
      }
    }
    for (i = 1; i <= pairs; i++) {
      key = ordered[i]
      if (key in exempt) continue
      allowance = (key in allowances) ? allowances[key] : 0
      if (counts[key] > allowance) {
        printf "check-test-health: %s %s has %d, .testhealth-baseline allows %d.\n", \
          rules[key], paths[key], counts[key], allowance > violations
      }
    }
  }
' "$tmp/counts" "$BASELINE" >"$tmp/baseline"

if [ -f "$tmp/changes" ]; then cat "$tmp/changes"; fi
status=0
if [ -f "$tmp/violations" ]; then
  cat "$tmp/violations" >&2
  status=1
fi
if [ "$mode" = --update ]; then mv "$tmp/baseline" "$BASELINE"; fi
exit "$status"
