#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: create-source-archive.sh --version VERSION [options]' \
    '' \
    'Options:' \
    '  --ref GIT_REF               committed source ref (default: HEAD)' \
    '  --output-directory PATH     artifact directory (default: artifacts/release)'
}

version=
source_ref=HEAD
output_directory="$release_repository_root/artifacts/release"
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --ref) [ "$#" -ge 2 ] || release_die '--ref requires a value'; source_ref=$2; shift 2 ;;
    --output-directory) [ "$#" -ge 2 ] || release_die '--output-directory requires a value'; output_directory=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_require_command git
git -C "$release_repository_root" rev-parse --verify "$source_ref^{commit}" >/dev/null ||
  release_die "source ref is not a commit: $source_ref"
worktree_status=$(git -C "$release_repository_root" status --porcelain --untracked-files=all)
[ -z "$worktree_status" ] ||
  release_die 'source archive requires a clean worktree so no source is omitted'

mkdir -p -- "$output_directory"
archive_name="dotnetjq-$version-source.tar.gz"
work_dir=$(release_make_temp_dir "$output_directory" dotnetjq-source)
cleanup() {
  case $work_dir in
    "$output_directory"/.dotnetjq-source.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

candidate="$work_dir/$archive_name"
prefix="dotnetjq-$version-source/"
git -C "$release_repository_root" archive --format=tar.gz --prefix="$prefix" --output="$candidate" "$source_ref"
"$release_script_dir/verify-source-archive.sh" --archive "$candidate" --version "$version"
release_assert_new_or_identical "$candidate" "$output_directory/$archive_name"
release_note "created $output_directory/$archive_name"
