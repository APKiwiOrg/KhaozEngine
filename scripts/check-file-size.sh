#!/bin/sh
# Engine copy for BASELINE MANAGEMENT ONLY (--init/--update/--preview). Enforcement in this repo is
# the KhaozEngine.CodeHealth.Analyzers KESIZE analyzer at compile time, wired in Directory.Build.props.
# The canonical enforcement copy (hooks + CI) is game-template/scripts/check-file-size.sh, which is
# also the semantic authority the analyzer mirrors. Keep the body identical when refreshing.
# check-file-size.sh - a RATCHET on source-file size, not a cap.
#
# Why a ratchet and not a cap: every game in the fleet grew its fattest file the same way, and it was
# always the frame-loop owner or a screen (the class that already holds OnUpdate/OnDraw and the live
# state is the cheapest place to put the next feature). A hard cap does not fix that. Its natural
# response is splitting at line N to appease the linter, which yields two god halves and is strictly
# worse than one. So this check does not ask "is this file big", it asks "did this file get BIGGER".
#
# Two rules:
#   1. A file recorded in .filesize-baseline must not exceed its recorded size. It may shrink freely
#      (no baseline edit needed). It may never grow.
#   2. A file NOT in the baseline must stay under the cap (default 800 lines, FILESIZE_CAP overrides).
#
# So existing debt is frozen where it stands and pressure lands only on the files that are already
# bad, while new files get a soft ceiling. Nobody is forced into a big-bang refactor and nobody can
# make it worse. See docs/CODE-LAYOUT-STANDARD.md for the convention this backs.
#
# Modes (the local hook and CI share one implementation so they cannot drift on what counts):
#   (default)   staged mode - checks the STAGED content of staged .cs files. What .githooks/pre-commit
#               runs at commit time.
#   --tree      whole-tree mode - checks every tracked .cs file as it stands. What CI runs, and what
#               .githooks/pre-push also runs before a push leaves the machine, so a commit that bypassed
#               the local hook (--no-verify, another IDE, the GitHub web UI) still gets caught before
#               it ever reaches CI.
#   --file <path>   single-file mode - reads candidate CONTENT from stdin (not from disk) and checks it
#               against <path>'s baseline entry, or the cap if <path> is unlisted. Lets a caller ask "if
#               this content landed at this path, would it violate the ratchet" without writing
#               anything anywhere. This is what the agent write-time hook
#               (.claude/settings.json / .codex/settings.json) invokes to simulate a Write/Edit tool
#               call's result before it lands: PreToolUse fires before the edit reaches disk, so the
#               hook builds the candidate content itself (the Write tool's whole content, or the
#               current file with the Edit tool's old_string/new_string substitution applied) and pipes
#               it in here rather than reading the file, which at that point still holds the old content.
#   --preview   print what --init would freeze, writing nothing. Works with or without an existing
#               baseline, so it also answers "what would this repo freeze if it adopted the ratchet".
#   --init      write .filesize-baseline from the current tree. Adoption only; refuses to overwrite.
#   --update    ratchet DOWN: lower entries that shrank, drop entries now under the cap or deleted.
#               Never raises an entry and never adds one, so it cannot be used to bless growth.
#               Blessing a genuinely new large file is a hand-edit of the baseline, which is a
#               deliberate act with a reviewable diff. Policy: once a baselined file has shrunk, run
#               --update in the SAME branch so the baseline follows the new low-water mark instead of
#               sitting stale (over-generous to the next growth) until someone else happens to notice.
#
# Override a blocked commit with FILESIZE_OK=1 (the same idiom as TEMPLATE_DRIFT_OK / BACKLOG_FILE_OK).
# That override is a git-hook-level idiom only: the agent write-time hook that calls --file has no such
# escape hatch, matching how the em-dash and TODO.md/ROADMAP.md agent guards are also unconditional.
set -eu

CAP=${FILESIZE_CAP:-800}
BASELINE=.filesize-baseline
mode=${1:-staged}

# Generated and vendored code is not ours to shrink. Resource designers in particular are regenerated
# wholesale and routinely run to thousands of lines.
is_excluded() {
  case "$1" in
    obj/*|bin/*|vendor/*|*/obj/*|*/bin/*|*/vendor/*) return 0 ;;
    *.Designer.cs|*.g.cs|*.generated.cs|*.AssemblyInfo.cs) return 0 ;;
    *) return 1 ;;
  esac
}

# Echo the recorded limit for a path, or nothing when it is not baselined. Tolerates paths with spaces.
baseline_for() {
  [ -f "$BASELINE" ] || return 0
  awk -v want="$1" '
    /^[[:space:]]*#/ { next }
    $1 ~ /^[0-9]+$/ {
      p = $0
      sub(/^[[:space:]]*[0-9]+[[:space:]]+/, "", p)
      if (p == want) { print $1; exit }
    }
  ' "$BASELINE"
}

# Echo the standard "<path>: <lines> lines, ..." sentence for one file: over its baseline (when $3 is
# non-empty) or over the cap and unbaselined (when $3 is empty). Shared by batch mode (--tree/staged,
# writing into $violations) and --file mode (a single check), so the two cannot drift on wording.
# Parameter names are deliberately distinct from any caller's own f/lines/limit/path: POSIX sh
# functions do not scope variables, so reusing those names here would silently clobber the caller's copy.
violation_line() {
  vpath=$1; vlines=$2; vlimit=$3
  if [ -n "$vlimit" ]; then
    printf '  %s: %s lines, baseline is %s (this file may shrink, not grow)\n' "$vpath" "$vlines" "$vlimit"
  else
    printf '  %s: %s lines, over the %s-line cap and not in %s\n' "$vpath" "$vlines" "$CAP" "$BASELINE"
  fi
}

# The explanation appended after any violation listing, in batch mode and --file mode alike, so the
# two cannot drift on what the ratchet tells you when it fires. This is "the ratchet's own message":
# other callers (the agent write-time hook included) surface it verbatim rather than writing their own.
print_ratchet_footer() {
  echo "" >&2
  echo "            This is a ratchet: existing debt is frozen, it just may not get worse." >&2
  echo "            Put the new code in its own type rather than growing the file. For a frame-loop" >&2
  echo "            or screen class that is the usual offender, see docs/CODE-LAYOUT-STANDARD.md." >&2
  echo "            Do NOT split at an arbitrary line to satisfy this check: two god halves are worse" >&2
  echo "            than one. Deliberate exception: FILESIZE_OK=1." >&2
}

# Collect the tracked, non-excluded .cs files. Empty output is a legitimate state (the placeholder
# template, a repo before its first project), and every mode below no-ops cleanly on it.
list_sources() {
  # Note the explicit if/then rather than "is_excluded x && continue": under set -e an AND-list whose
  # left side fails takes the whole list's status, which would kill this subshell on the first file
  # that is NOT excluded. Same reason for every other guard in this script.
  git ls-files -z '*.cs' 2>/dev/null | tr '\0' '\n' | while IFS= read -r f; do
    [ -n "$f" ] || continue
    if is_excluded "$f"; then continue; fi
    printf '%s\n' "$f"
  done
}

# Every tracked, non-excluded source file over the cap, as "<lines> <path>", largest first. The single
# definition of "what gets frozen", shared by --init and --preview so a preview cannot disagree with
# what --init then writes.
over_cap() {
  list_sources | while IFS= read -r f; do
    lines=$(wc -l < "$f" | tr -d ' ')
    if [ "$lines" -gt "$CAP" ]; then printf '%s %s\n' "$lines" "$f"; fi
  done | sort -rn
}

write_baseline_header() {
  cat <<'EOF'
# .filesize-baseline - frozen sizes for source files that already exceed the size cap.
#
# Format: "<lines> <path>", largest first. Managed by scripts/check-file-size.sh:
#   scripts/check-file-size.sh --init     create this file (adoption only)
#   scripts/check-file-size.sh --update   ratchet down after shrinking something
#
# A listed file may shrink freely without editing this file. It may not grow. Ratchet an entry down
# with --update once real work has landed. Adding an entry or raising a number is a deliberate
# hand-edit, on purpose: it should show up in review as "we are blessing a new large file".
#
# This file is per-repo and is NOT part of the verbatim template layer.
EOF
}

case "$mode" in
  --tree|staged)
    # No baseline at all means this repo has not adopted the ratchet yet, so pass. This is what keeps
    # propagating the template layer from reddening every game's CI the moment it lands: adoption is
    # "run --init and commit the result", a separate deliberate step. A scaffolded repo gets its
    # baseline (an empty one) at scaffold time, so a NEW repo is enforced from its first commit. The
    # notice is CI-only: nagging on every local commit in an unadopted repo would just train people to
    # ignore the hook.
    if [ ! -f "$BASELINE" ]; then
      if [ "$mode" = "--tree" ]; then
        echo "check-file-size: no $BASELINE in this repo, size ratchet not adopted yet (skipping)." >&2
        echo "            Adopt it with: scripts/check-file-size.sh --init && git add $BASELINE" >&2
      fi
      exit 0
    fi

    violations=$(mktemp)
    shrunk=$(mktemp)
    trap 'rm -f "$violations" "$shrunk"' EXIT

    if [ "$mode" = "--tree" ]; then
      files=$(list_sources)
      measure() { wc -l < "$1" | tr -d ' '; }
    else
      # Staged content, not the working tree: the check must describe the commit being made. ACMR skips
      # deletions (a deleted file cannot violate a size rule).
      files=$(git diff --cached --name-only --diff-filter=ACMR -z -- '*.cs' 2>/dev/null | tr '\0' '\n' \
        | while IFS= read -r f; do
            [ -n "$f" ] || continue
            if is_excluded "$f"; then continue; fi
            printf '%s\n' "$f"
          done)
      measure() { git show ":$1" 2>/dev/null | wc -l | tr -d ' '; }
    fi

    [ -n "$files" ] || exit 0

    printf '%s\n' "$files" | while IFS= read -r f; do
      [ -n "$f" ] || continue
      lines=$(measure "$f")
      [ -n "$lines" ] || continue
      limit=$(baseline_for "$f")
      if [ -n "$limit" ]; then
        if [ "$lines" -gt "$limit" ]; then
          violation_line "$f" "$lines" "$limit" >> "$violations"
        elif [ "$lines" -lt "$limit" ]; then
          printf '  %s: %s lines, baseline is %s\n' "$f" "$lines" "$limit" >> "$shrunk"
        fi
      elif [ "$lines" -gt "$CAP" ]; then
        violation_line "$f" "$lines" "" >> "$violations"
      fi
    done

    if [ -s "$violations" ]; then
      echo "check-file-size: source files grew past their limit:" >&2
      cat "$violations" >&2
      print_ratchet_footer
      exit 1
    fi

    # Informational only, and only in CI's whole-tree pass, so a commit is never blocked or nagged for
    # the good outcome. Tells you the ratchet has slack to take up.
    if [ "$mode" = "--tree" ] && [ -s "$shrunk" ]; then
      echo "check-file-size: these files are now smaller than their baseline:" >&2
      cat "$shrunk" >&2
      echo "            Run 'scripts/check-file-size.sh --update' to ratchet the baseline down." >&2
    fi
    ;;

  --file)
    path=${2:-}
    if [ -z "$path" ]; then
      echo "check-file-size: --file requires a path argument" >&2
      exit 2
    fi
    # Same no-op as --tree: a repo that has not adopted the ratchet, or a generated/vendored path, has
    # nothing to enforce. Silent (not the --tree adoption notice), since a write-time hook firing on
    # every keystroke is the wrong place to nag about adoption.
    [ -f "$BASELINE" ] || exit 0
    if is_excluded "$path"; then exit 0; fi
    lines=$(wc -l | tr -d ' ')
    limit=$(baseline_for "$path")
    if [ -n "$limit" ]; then
      if [ "$lines" -gt "$limit" ]; then
        echo "check-file-size: source file grew past its limit:" >&2
        violation_line "$path" "$lines" "$limit" >&2
        print_ratchet_footer
        exit 1
      fi
    elif [ "$lines" -gt "$CAP" ]; then
      echo "check-file-size: source file grew past its limit:" >&2
      violation_line "$path" "$lines" "" >&2
      print_ratchet_footer
      exit 1
    fi
    ;;

  --init)
    if [ -f "$BASELINE" ]; then
      echo "check-file-size: $BASELINE already exists. --init is for adoption only." >&2
      echo "            To ratchet it down after shrinking a file, use --update." >&2
      exit 2
    fi
    tmp=$(mktemp)
    write_baseline_header > "$tmp"
    over_cap >> "$tmp"
    mv "$tmp" "$BASELINE"
    n=$(grep -cE '^[0-9]+ ' "$BASELINE" || true)
    echo "check-file-size: wrote $BASELINE with $n file(s) over the $CAP-line cap."
    ;;

  --update)
    if [ ! -f "$BASELINE" ]; then
      echo "check-file-size: no $BASELINE to update. Create one with --init." >&2
      exit 2
    fi
    tmp=$(mktemp)
    write_baseline_header > "$tmp"
    # Walk the EXISTING entries only. A file that shrank under the cap or was deleted drops out; one
    # that shrank but is still over the cap gets its lower number. Nothing is ever added or raised.
    grep -E '^[0-9]+ ' "$BASELINE" 2>/dev/null | while IFS= read -r entry; do
      recorded=${entry%% *}
      path=${entry#* }
      [ -f "$path" ] || continue
      lines=$(wc -l < "$path" | tr -d ' ')
      [ "$lines" -gt "$CAP" ] || continue
      if [ "$lines" -lt "$recorded" ]; then
        printf '%s %s\n' "$lines" "$path"
      else
        printf '%s %s\n' "$recorded" "$path"
      fi
    done | sort -rn >> "$tmp"
    mv "$tmp" "$BASELINE"
    n=$(grep -cE '^[0-9]+ ' "$BASELINE" || true)
    echo "check-file-size: ratcheted $BASELINE down to $n file(s) over the $CAP-line cap."
    ;;

  --preview)
    over_cap
    ;;

  *)
    echo "check-file-size: unknown mode '$mode' (expected: staged, --tree, --file <path>, --preview, --init, --update)" >&2
    exit 2
    ;;
esac
