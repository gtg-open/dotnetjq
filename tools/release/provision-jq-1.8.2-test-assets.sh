#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: provision-jq-1.8.2-test-assets.sh --rid RID --destination PATH [options]' \
    '' \
    'Options:' \
    '  --github-env FILE  append DOTNETJQ_JQ182, DOTNETJQ_ORACLE, and DOTNETJQ_UPSTREAM to FILE' \
    '' \
    'Downloads the hash-pinned official jq 1.8.2 executable for RID and checks' \
    'out the exact jq 1.8.2 source commit, including its pinned submodules.'
}

rid=
destination=
github_env=
while [ "$#" -gt 0 ]; do
  case $1 in
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --destination) [ "$#" -ge 2 ] || release_die '--destination requires a value'; destination=$2; shift 2 ;;
    --github-env) [ "$#" -ge 2 ] || release_die '--github-env requires a value'; github_env=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$rid" ] || release_die '--rid is required'
[ -n "$destination" ] || release_die '--destination is required'
release_target_field "$rid" 2 >/dev/null
for command in awk chmod cmp curl git grep mkdir mv rm sort; do
  release_require_command "$command"
done

manifest="$release_script_dir/jq-1.8.2-oracles.tsv"
[ -f "$manifest" ] || release_die "oracle manifest is missing: $manifest"
inventory_parent=${TMPDIR:-/tmp}
inventory_work_dir=$(release_make_temp_dir "$inventory_parent" jq-1.8.2-manifest)
expected_rids="$inventory_work_dir/rids.expected"
manifest_rids="$inventory_work_dir/rids.actual"
cleanup_inventory() {
  case $inventory_work_dir in
    "$inventory_parent"/.jq-1.8.2-manifest.*) rm -rf -- "$inventory_work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $inventory_work_dir" ;;
  esac
}
trap cleanup_inventory EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
release_each_target | LC_ALL=C sort >"$expected_rids"
awk -F '\t' '!/^#/ && NF { if (NF != 3) exit 2; print $1 }' "$manifest" |
  LC_ALL=C sort >"$manifest_rids" || release_die 'oracle manifest contains a malformed row'
cmp -s -- "$expected_rids" "$manifest_rids" ||
  release_die 'oracle manifest RID inventory does not exactly match release-targets.tsv'

row=$(awk -F '\t' -v rid="$rid" '$1 == rid { print; found++ } END { if (found != 1) exit 1 }' "$manifest") ||
  release_die "oracle manifest must contain exactly one row for $rid"
asset=$(printf '%s\n' "$row" | awk -F '\t' '{ print $2 }')
expected_sha256=$(printf '%s\n' "$row" | awk -F '\t' '{ print $3 }')
case $asset in *[!0-9A-Za-z._-]*|'') release_die "unsafe oracle asset name: $asset" ;; esac
case $expected_sha256 in *[!0-9a-f]*|'') release_die "malformed oracle SHA-256 for $rid" ;; esac
[ "${#expected_sha256}" -eq 64 ] || release_die "malformed oracle SHA-256 for $rid"

# Git Bash receives Windows-native paths from the runner environment. Convert
# only for POSIX tools; values exported to a Windows process are converted back.
if command -v cygpath >/dev/null 2>&1; then
  destination=$(cygpath -u -- "$destination")
fi
mkdir -p -- "$destination"
destination=$(CDPATH= cd -- "$destination" && pwd -P)
oracle_directory="$destination/oracle"
upstream="$destination/upstream"
mkdir -p -- "$oracle_directory"
case $rid in win-*) oracle="$oracle_directory/jq.exe" ;; *) oracle="$oracle_directory/jq" ;; esac

download="$oracle.download"
if [ ! -f "$oracle" ] || [ "$(release_sha256 "$oracle")" != "$expected_sha256" ]; then
  rm -f -- "$download"
  curl --fail --location --silent --show-error \
    --output "$download" \
    "https://github.com/jqlang/jq/releases/download/jq-1.8.2/$asset"
  actual_sha256=$(release_sha256 "$download")
  [ "$actual_sha256" = "$expected_sha256" ] || {
    rm -f -- "$download"
    release_die "jq 1.8.2 oracle hash mismatch for $rid: $actual_sha256"
  }
  mv -- "$download" "$oracle"
fi
chmod 0755 "$oracle"
[ "$(release_sha256 "$oracle")" = "$expected_sha256" ] ||
  release_die "stored jq 1.8.2 oracle hash mismatch for $rid"
oracle_version=$(env -u LD_PRELOAD "$oracle" --version 2>/dev/null) ||
  release_die "official jq 1.8.2 oracle cannot execute for $rid"
oracle_version=${oracle_version%$'\r'}
[ "$oracle_version" = jq-1.8.2 ] ||
  release_die "official jq oracle has unexpected version: $oracle_version"

upstream_commit=34f7186b86743a083a589741b6cea95293524108
if [ ! -d "$upstream/.git" ]; then
  [ ! -e "$upstream" ] || release_die "upstream destination exists but is not a Git checkout: $upstream"
  git init --quiet "$upstream"
  git -C "$upstream" config core.autocrlf false
  git -C "$upstream" remote add origin https://github.com/jqlang/jq.git
  git -C "$upstream" fetch --quiet --depth 1 origin "$upstream_commit"
  git -C "$upstream" checkout --quiet --detach FETCH_HEAD
fi
git -C "$upstream" config core.autocrlf false
[ "$(git -C "$upstream" rev-parse HEAD)" = "$upstream_commit" ] ||
  release_die "upstream checkout is not pinned jq commit $upstream_commit"
git -C "$upstream" -c core.autocrlf=false submodule update --quiet --init --recursive --depth 1
submodule_status=$(git -C "$upstream" submodule status --recursive)
if grep -E '^[+-]' <<<"$submodule_status" >/dev/null 2>&1; then
  release_die 'a jq submodule is missing or differs from its pinned commit'
elif [ "$?" -ne 1 ]; then
  release_die 'could not scan jq submodule status'
fi
submodule_inventory="$inventory_work_dir/submodules.txt"
git -C "$upstream" config --file .gitmodules --get-regexp path |
  awk '{ print $2 }' >"$submodule_inventory" ||
  release_die 'could not enumerate jq submodules from .gitmodules'
while IFS= read -r submodule; do
  git -C "$upstream/$submodule" config core.autocrlf false
  submodule_worktree_status=$(git -C "$upstream/$submodule" status \
    --porcelain=v1 --untracked-files=all)
  [ -z "$submodule_worktree_status" ] ||
    release_die "pinned jq submodule is dirty: $submodule"
done <"$submodule_inventory"
upstream_worktree_status=$(git -C "$upstream" status \
  --porcelain=v1 --untracked-files=all --ignore-submodules=none)
[ -z "$upstream_worktree_status" ] ||
  release_die 'pinned jq source checkout or a submodule is dirty'

environment_oracle=$oracle
environment_upstream=$upstream
if command -v cygpath >/dev/null 2>&1; then
  environment_oracle=$(cygpath -aw -- "$oracle")
  environment_upstream=$(cygpath -aw -- "$upstream")
fi
if [ -n "$github_env" ]; then
  printf 'DOTNETJQ_JQ182=%s\nDOTNETJQ_ORACLE=%s\nDOTNETJQ_UPSTREAM=%s\n' \
    "$environment_oracle" "$environment_oracle" "$environment_upstream" >>"$github_env"
fi

printf 'DOTNETJQ_JQ182=%s\nDOTNETJQ_ORACLE=%s\nDOTNETJQ_UPSTREAM=%s\n' \
  "$environment_oracle" "$environment_oracle" "$environment_upstream"
release_note "provisioned pinned jq 1.8.2 source and $rid oracle"
