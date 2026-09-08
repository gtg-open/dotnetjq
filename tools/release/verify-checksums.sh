#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' 'Usage: verify-checksums.sh --directory PATH [--checksums FILE]'
}

directory=
checksums=
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --checksums) [ "$#" -ge 2 ] || release_die '--checksums requires a value'; checksums=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$directory" ] || release_die '--directory is required'
[ -d "$directory" ] || release_die "directory does not exist: $directory"
[ -n "$checksums" ] || checksums="$directory/SHA256SUMS"
[ -s "$checksums" ] || release_die "checksum file is missing or empty: $checksums"

listed_names=$(mktemp "${TMPDIR:-/tmp}/dotnetjq-checksum-names.XXXXXX")
duplicate_names="$listed_names.duplicates"
artifact_inventory="$listed_names.artifacts"
cleanup() {
  case $listed_names in
    "${TMPDIR:-/tmp}"/dotnetjq-checksum-names.*)
      rm -f -- "$listed_names" "$duplicate_names" "$artifact_inventory"
      ;;
    *) release_die "refusing unsafe temporary cleanup: $listed_names" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

while IFS= read -r line; do
  expected=${line%%  *}
  name=${line#*  }
  [ "$line" != "$name" ] || release_die "malformed checksum line: $line"
  [ "${#expected}" -eq 64 ] || release_die "invalid SHA-256 for $name"
  case $expected in *[!0-9A-Fa-f]*) release_die "invalid SHA-256 for $name" ;; esac
  case $name in ''|*/*|.*) release_die "unsafe checksum filename: $name" ;; esac
  [ -f "$directory/$name" ] || release_die "checksummed artifact is missing: $name"
  actual=$(release_sha256 "$directory/$name")
  [ "$(printf '%s' "$actual" | tr 'A-F' 'a-f')" = "$(printf '%s' "$expected" | tr 'A-F' 'a-f')" ] ||
    release_die "checksum mismatch: $name"
  printf '%s\n' "$name" >>"$listed_names"
done <"$checksums"

LC_ALL=C sort "$listed_names" | uniq -d >"$duplicate_names"
if [ -s "$duplicate_names" ]; then
  release_die 'checksum file contains duplicate filenames'
fi

find "$directory" -maxdepth 1 -type f ! -name '.*' ! -name SHA256SUMS \
  -exec sh -c 'for path do basename "$path"; done' sh {} + |
  LC_ALL=C sort >"$artifact_inventory"
while IFS= read -r name; do
  [ "$directory/$name" = "$checksums" ] && continue
  grep -Fx -- "$name" "$listed_names" >/dev/null || release_die "artifact is not checksummed: $name"
done <"$artifact_inventory"

verified_checksum_count=$(wc -l <"$listed_names" | tr -d ' ')
release_note "verified $verified_checksum_count checksums"
