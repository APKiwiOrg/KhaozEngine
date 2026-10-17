#!/bin/sh
# release-lock.sh - hold main still while an engine release is in flight (issue #1439).
#
#   KE_RELEASE_LOCK_OWNER=<id> scripts/release-lock.sh acquire vX.Y.Z
#   KE_RELEASE_LOCK_OWNER=<id> scripts/release-lock.sh release [--force]
#   scripts/release-lock.sh status
#
# Every worktree shares one git common dir, so one lock covers every session on the machine. The
# .githooks pre-push and reference-transaction hooks refuse moving or pushing main while a fresh lock
# is held by an owner other than KE_RELEASE_LOCK_OWNER. The lock format and staleness rule live in
# scripts/release-lock-standard.sh.
#
# acquire  takes the lock. Idempotent for the same owner, which may also move it to a new tag. Refuses
#          a fresh lock held by another owner (exit 1). Takes over a stale lock with a warning.
# release  removes the caller's lock. Refuses another owner's fresh lock unless --force (exit 1).
# status   prints the holder, tag and age. Exit 0 when free or stale, 1 when a fresh lock is held.
# Usage errors exit 2.
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$here/release-lock-standard.sh"

usage() {
  echo "usage: KE_RELEASE_LOCK_OWNER=<id> $0 acquire vX.Y.Z | release [--force] | status" >&2
  exit 2
}

lock=$(rl_lock_path) || { echo "release-lock: not inside a git repository." >&2; exit 2; }
owner=${KE_RELEASE_LOCK_OWNER:-}

need_owner() {
  [ -n "$owner" ] || { echo "release-lock: set KE_RELEASE_LOCK_OWNER to the release thread id." >&2; exit 2; }
  case "$owner" in *[!A-Za-z0-9._:@/+-]*) echo "release-lock: KE_RELEASE_LOCK_OWNER may hold only letters, digits and ._:@/+-" >&2; exit 2 ;; esac
}

# Sets fresh=1 when the lock read by rl_read is well formed and younger than the stale limit.
judge() {
  fresh=0
  [ -n "$rl_epoch" ] || return 0
  rl_age_of
  [ "$rl_age" -lt "$RL_STALE_SECONDS" ] && fresh=1
  return 0
}

describe() {
  if [ -n "$rl_epoch" ]; then
    printf '%s held by %s on %s since %s (age %s)' "$rl_tag" "$rl_owner" "$rl_host" "$rl_started" "$(rl_fmt_age "$rl_age")"
  else
    printf 'malformed lock at %s' "$lock"
  fi
}

write_lock() { # tag
  tmp="$lock.tmp.$$"
  {
    echo "tag=$1"
    echo "owner=$owner"
    echo "host=$(hostname 2>/dev/null || uname -n)"
    echo "started=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "started_epoch=$(date -u +%s)"
  } > "$tmp"
}

cmd=${1:-}
case "$cmd" in
  acquire)
    [ $# -eq 2 ] || usage
    tag=$2
    case "$tag" in v[0-9]*.[0-9]*.[0-9]*) ;; *) echo "release-lock: tag must be vMAJOR.MINOR.PATCH, got '$tag'." >&2; exit 2 ;; esac
    need_owner
    write_lock "$tag"
    # ln refuses an existing target, so two racing acquires cannot both win.
    if ln "$tmp" "$lock" 2>/dev/null; then
      rm -f "$tmp"
      echo "release-lock: acquired $tag for $owner."
      exit 0
    fi
    if rl_read "$lock"; then judge; else rm -f "$tmp"; echo "release-lock: lock vanished mid-acquire, retry." >&2; exit 1; fi
    if [ "$rl_owner" = "$owner" ] && [ "$fresh" = 1 ]; then
      if [ "$rl_tag" = "$tag" ]; then
        rm -f "$tmp"
        echo "release-lock: already held, $(describe)."
        exit 0
      fi
      mv -f "$tmp" "$lock"
      echo "release-lock: moved $owner's lock from $rl_tag to $tag."
      exit 0
    fi
    if [ "$fresh" = 1 ]; then
      rm -f "$tmp"
      echo "release-lock: refused. Release $(describe)." >&2
      echo "              Wait for it to finish. A lock older than six hours is stale and can be taken." >&2
      exit 1
    fi
    echo "release-lock: taking over a stale lock, $(describe)." >&2
    mv -f "$tmp" "$lock"
    echo "release-lock: acquired $tag for $owner."
    ;;
  release)
    force=0
    case "${2:-}" in --force) force=1 ;; '') ;; *) usage ;; esac
    [ $# -le 2 ] || usage
    if ! rl_read "$lock"; then echo "release-lock: no lock is held."; exit 0; fi
    judge
    if [ "$force" = 1 ]; then
      rm -f "$lock"
      echo "release-lock: force released, $(describe)."
      exit 0
    fi
    need_owner
    if [ "$rl_owner" != "$owner" ]; then
      echo "release-lock: refused. The lock belongs to someone else, $(describe)." >&2
      echo "              Only its owner releases it, or use --force after confirming the release stopped." >&2
      exit 1
    fi
    rm -f "$lock"
    echo "release-lock: released $rl_tag for $owner."
    ;;
  status)
    [ $# -eq 1 ] || usage
    if ! rl_read "$lock"; then echo "release-lock: free."; exit 0; fi
    judge
    if [ "$fresh" = 1 ]; then
      echo "release-lock: held. Release $(describe)."
      exit 1
    fi
    if [ -n "$rl_epoch" ]; then
      echo "release-lock: stale, ignored by the hooks. Release $(describe)."
    else
      echo "release-lock: $(describe), ignored by the hooks."
    fi
    exit 0
    ;;
  *) usage ;;
esac
