# release-lock-standard.sh - the one definition of the engine release lock (issue #1439), sourced by
# scripts/release-lock.sh and by the .githooks pre-push and reference-transaction hooks so the command
# and the guards agree on where the lock lives, what it holds and when it is stale.
#
# Every worktree on a machine shares one git common dir, so the lock lives there as
# <git-common-dir>/ke-release.lock, one key=value per line:
#   tag=vX.Y.Z  owner=<KE_RELEASE_LOCK_OWNER>  host=<host>  started=<UTC ISO 8601>  started_epoch=<secs>
# A lock older than RL_STALE_SECONDS is stale: status reports it and the hooks ignore it with a warning.
#
# Sourced, not executed. Defines rl_* functions and variables only. No set -e here, because the hooks
# that source it must fail open on any unexpected error.

RL_STALE_SECONDS=21600

# Echoes the lock path for the repository in the current directory. Fails when not in a repository.
rl_lock_path() {
  _rl_common=$(git rev-parse --git-common-dir 2>/dev/null) || return 1
  [ -n "$_rl_common" ] || return 1
  printf '%s/ke-release.lock\n' "$_rl_common"
}

# Reads lock file $1 into rl_tag, rl_owner, rl_host, rl_started and rl_epoch. Fails when the file is
# missing. A file without a numeric epoch reads with rl_epoch empty, which callers treat as malformed.
rl_read() {
  rl_tag='' rl_owner='' rl_host='' rl_started='' rl_epoch=''
  [ -f "$1" ] || return 1
  while IFS='=' read -r _rl_k _rl_v; do
    case "$_rl_k" in
      tag) rl_tag=$_rl_v ;;
      owner) rl_owner=$_rl_v ;;
      host) rl_host=$_rl_v ;;
      started) rl_started=$_rl_v ;;
      started_epoch) rl_epoch=$_rl_v ;;
    esac
  done < "$1"
  case "$rl_epoch" in ''|*[!0-9]*) rl_epoch='' ;; esac
  return 0
}

# Sets rl_age to the seconds since the lock read by rl_read started.
rl_age_of() {
  rl_age=$(( $(date -u +%s) - rl_epoch ))
  [ "$rl_age" -ge 0 ] || rl_age=0
}

# Echoes seconds $1 as "Xh Ym".
rl_fmt_age() {
  printf '%sh %sm' $(( $1 / 3600 )) $(( ($1 % 3600) / 60 ))
}

# The hook check. $1 names the hook for messages. Returns 1 only when a fresh, well-formed lock is held
# by an owner other than KE_RELEASE_LOCK_OWNER, after printing the refusal. Every other outcome,
# including any error, returns 0 so the hook fails open.
rl_main_guard() {
  _rl_hook=$1
  _rl_file=$(rl_lock_path) || return 0
  [ -f "$_rl_file" ] || return 0
  rl_read "$_rl_file" || return 0
  if [ -z "$rl_epoch" ] || [ -z "$rl_owner" ]; then
    echo "$_rl_hook: ignoring a malformed release lock at $_rl_file." >&2
    return 0
  fi
  [ "$rl_owner" = "${KE_RELEASE_LOCK_OWNER:-}" ] && return 0
  rl_age_of || return 0
  _rl_agefmt=$(rl_fmt_age "$rl_age")
  if [ "$rl_age" -ge "$RL_STALE_SECONDS" ]; then
    echo "$_rl_hook: ignoring a stale release lock for $rl_tag held by $rl_owner (age $_rl_agefmt)." >&2
    return 0
  fi
  echo "$_rl_hook: refused. An engine release is in flight and main is locked." >&2
  echo "          Release $rl_tag is held by $rl_owner on $rl_host (age $_rl_agefmt)." >&2
  echo "          Wait for it to finish, or check scripts/release-lock.sh status." >&2
  return 1
}
