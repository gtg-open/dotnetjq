#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-aot-source-archive.sh --archive PATH --version VERSION [--checksum FILE]'
}

archive=
version=
checksum=
while [ "$#" -gt 0 ]; do
  case $1 in
    --archive) [ "$#" -ge 2 ] || release_die '--archive requires a value'; archive=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --checksum) [ "$#" -ge 2 ] || release_die '--checksum requires a value'; checksum=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
expected_name="dotnetjq-aot-source-$version.tar.gz"
[ -f "$archive" ] || release_die "AOT corresponding-source archive does not exist: ${archive-}"
[ "$(basename -- "$archive")" = "$expected_name" ] ||
  release_die "AOT corresponding-source archive must be named $expected_name"
[ -n "$checksum" ] || checksum="$archive.sha256"
[ -f "$checksum" ] || release_die "AOT corresponding-source checksum does not exist: $checksum"
release_require_command python3

archive_sha=$(release_sha256 "$archive")
checksum_hash=$(awk 'NF == 2 { print $1 }' "$checksum")
checksum_name=$(awk 'NF == 2 { print $2 }' "$checksum")
[ "$(awk 'NF { count++ } END { print count+0 }' "$checksum")" -eq 1 ] ||
  release_die 'AOT corresponding-source checksum must contain exactly one record'
[ "$checksum_hash" = "$archive_sha" ] || release_die 'AOT corresponding-source checksum does not match the archive'
case $checksum_name in "*$expected_name"|"$expected_name") ;; *) release_die 'AOT checksum names a different archive' ;; esac

python3 "$release_script_dir/verify-archive-metadata.py" aot-source "$archive" --version "$version"

release_require_command tar
work_parent=${TMPDIR:-/tmp}
work_dir=$(release_make_temp_dir "$work_parent" dotnetjq-aot-source-verify)
cleanup() {
  case $work_dir in
    "$work_parent"/.dotnetjq-aot-source-verify.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

entries="$work_dir/entries.txt"
tar -tzf "$archive" >"$entries"
[ -s "$entries" ] || release_die 'AOT corresponding-source archive is empty'
if ! tar -tvzf "$archive" | awk '{ type=substr($1,1,1); if (type != "-" && type != "d") bad=1 } END { exit bad }'; then
  release_die 'AOT corresponding-source archive contains a link or special file'
fi

prefix="dotnetjq-aot-source-$version"
canonical_entries="$work_dir/canonical-entries.txt"
: >"$canonical_entries"
while IFS= read -r entry; do
  case $entry in
    "$prefix"|"$prefix/"*) ;;
    *) release_die "AOT source entry escapes its versioned prefix: $entry" ;;
  esac
  case $entry in
    /*|*\\*|*/../*|*/..|*/./*|*/.|*//*) release_die "unsafe AOT source entry: $entry" ;;
  esac
  canonical=${entry%/}
  printf '%s\n' "$canonical" >>"$canonical_entries"
done <"$entries"
duplicate_entries="$work_dir/duplicate-entries.txt"
LC_ALL=C sort "$canonical_entries" | uniq -d >"$duplicate_entries"
if [ -s "$duplicate_entries" ]; then
  release_die 'AOT source archive contains duplicate or colliding entries'
fi

extract_root="$work_dir/extracted"
mkdir -p -- "$extract_root"
tar -xzf "$archive" -C "$extract_root"
source_root="$extract_root/$prefix"
[ -d "$source_root" ] || release_die 'AOT source archive did not extract its versioned root'

expected_files="$work_dir/expected-files.txt"
: >"$expected_files"
repository_files="$work_dir/repository-files.txt"
: >"$repository_files"
while IFS= read -r required; do
  case $required in ''|'#'*) continue ;; esac
  release_find_source_files "$required" | LC_ALL=C sort >>"$repository_files" ||
    release_die "could not enumerate corresponding-source path: $required"
done <"$release_aot_source_paths_file"
LC_ALL=C sort -u -o "$repository_files" "$repository_files"
while IFS= read -r source_file; do
  relative=${source_file#"$release_repository_root/"}
  printf '%s\n' "$relative" >>"$expected_files"
  [ -f "$source_root/$relative" ] || release_die "AOT source archive is missing $relative"
  cmp -s -- "$source_file" "$source_root/$relative" ||
    release_die "AOT source archive has modified release input: $relative"
done <"$repository_files"
printf '%s\n' SOURCE_BUNDLE_INFO.txt SOURCE_SHA256SUMS >>"$expected_files"
LC_ALL=C sort -u -o "$expected_files" "$expected_files"

actual_files="$work_dir/actual-files.txt"
find "$source_root" -type f -exec sh -c '
  prefix=$1
  shift
  for path do printf "%s\n" "${path#"$prefix/"}"; done
' sh "$source_root" {} + | LC_ALL=C sort -u >"$actual_files" ||
  release_die 'could not enumerate extracted corresponding-source files'
cmp -s -- "$expected_files" "$actual_files" ||
  release_die 'AOT corresponding-source file inventory does not match packaging/aot-source-paths.txt'

expected_directories="$work_dir/expected-directories.txt"
actual_directories="$work_dir/actual-directories.txt"
release_write_parent_directory_inventory "$expected_files" "$expected_directories"
find "$source_root" -mindepth 1 -type d -exec sh -c '
  prefix=$1
  shift
  for path do printf "%s\n" "${path#"$prefix/"}"; done
' sh "$source_root" {} + | LC_ALL=C sort -u >"$actual_directories" ||
  release_die 'could not enumerate extracted corresponding-source directories'
cmp -s -- "$expected_directories" "$actual_directories" ||
  release_die 'AOT corresponding-source archive contains a missing or unexpected directory'

bundle_info="$source_root/SOURCE_BUNDLE_INFO.txt"
grep -Fx 'Format: DotNetJq NativeAOT corresponding source v1' "$bundle_info" >/dev/null ||
  release_die 'AOT source bundle has an unsupported format'
grep -Fx "Release-Version: $version" "$bundle_info" >/dev/null ||
  release_die 'AOT source bundle records a different release version'
grep -Fx 'Target-Framework: net10.0' "$bundle_info" >/dev/null ||
  release_die 'AOT source bundle does not record net10.0'
grep -Fx 'Upstream-jq-Commit: 34f7186b86743a083a589741b6cea95293524108' "$bundle_info" >/dev/null ||
  release_die 'AOT source bundle does not record the pinned jq commit'

inventory="$source_root/SOURCE_SHA256SUMS"
listed_files="$work_dir/listed-files.txt"
: >"$listed_files"
while read -r recorded_hash recorded_path extra; do
  [ -z "${extra-}" ] || release_die 'malformed internal source checksum record'
  [[ $recorded_hash =~ ^[0-9a-f]{64}$ ]] || release_die 'malformed internal source checksum hash'
  recorded_path=${recorded_path#\*}
  recorded_path=${recorded_path#./}
  case $recorded_path in ''|/*|*\\*|../*|*/../*|*/..) release_die "unsafe internal source checksum path: $recorded_path" ;; esac
  [ -f "$source_root/$recorded_path" ] || release_die "internal source checksum names a missing file: $recorded_path"
  [ "$(release_sha256 "$source_root/$recorded_path")" = "$recorded_hash" ] ||
    release_die "internal source checksum mismatch: $recorded_path"
  printf '%s\n' "$recorded_path" >>"$listed_files"
done <"$inventory"
duplicate_checksum_entries="$work_dir/duplicate-checksum-entries.txt"
LC_ALL=C sort "$listed_files" | uniq -d >"$duplicate_checksum_entries"
if [ -s "$duplicate_checksum_entries" ]; then
  release_die 'internal source checksum inventory contains duplicates'
fi
sed '/^SOURCE_SHA256SUMS$/d' "$actual_files" >"$work_dir/checksummed-files.txt"
LC_ALL=C sort -u -o "$listed_files" "$listed_files"
cmp -s -- "$work_dir/checksummed-files.txt" "$listed_files" ||
  release_die 'internal source checksum inventory is incomplete'

release_note "verified $expected_name"
