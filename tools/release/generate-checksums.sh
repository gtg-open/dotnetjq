#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' 'Usage: generate-checksums.sh --directory PATH [--output FILE]'
}

directory=
output=
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --output) [ "$#" -ge 2 ] || release_die '--output requires a value'; output=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$directory" ] || release_die '--directory is required'
[ -d "$directory" ] || release_die "directory does not exist: $directory"
[ -n "$output" ] || output="$directory/SHA256SUMS"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-checksums)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-checksums.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

candidate="$work_dir/SHA256SUMS"
artifact_inventory="$work_dir/artifacts.txt"
(
  cd -- "$directory"
  find . -maxdepth 1 -type f ! -name '.*' ! -name SHA256SUMS -print |
    sed 's|^\./||' |
    LC_ALL=C sort
) >"$artifact_inventory"
count=0
while IFS= read -r name; do
  [ "$directory/$name" != "$output" ] || continue
  artifact_sha256=$(release_sha256 "$directory/$name")
  printf '%s  %s\n' "$artifact_sha256" "$name" >>"$candidate"
  count=$((count + 1))
done <"$artifact_inventory"

[ "$count" -gt 0 ] || release_die "no release artifacts found in $directory"
release_assert_new_or_identical "$candidate" "$output"
release_note "generated $output ($count artifacts)"
