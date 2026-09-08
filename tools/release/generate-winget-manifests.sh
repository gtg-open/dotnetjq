#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: generate-winget-manifests.sh --version VERSION --repository OWNER/NAME' \
    '       --publisher TEXT --package-id ID --artifacts PATH --output-directory PATH' \
    '       [--tag TAG]'
}

version=
repository=
publisher=
package_id=
artifacts=
output_directory=
tag=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --repository) [ "$#" -ge 2 ] || release_die '--repository requires a value'; repository=$2; shift 2 ;;
    --publisher) [ "$#" -ge 2 ] || release_die '--publisher requires a value'; publisher=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --artifacts) [ "$#" -ge 2 ] || release_die '--artifacts requires a value'; artifacts=$2; shift 2 ;;
    --output-directory) [ "$#" -ge 2 ] || release_die '--output-directory requires a value'; output_directory=$2; shift 2 ;;
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_repository "$repository"
release_validate_package_id "$package_id"
release_validate_single_line publisher "$publisher"
[ -d "$artifacts" ] || release_die "artifact directory does not exist: ${artifacts-}"
[ -n "$output_directory" ] || release_die '--output-directory is required'
[ -n "$tag" ] || tag="v$version"
case $tag in *[!0-9A-Za-z._-]*) release_die "invalid release tag: $tag" ;; esac

for rid in win-x64 win-arm64; do
  archive="$artifacts/$(release_archive_name "$version" "$rid")"
  "$release_script_dir/verify-native-archive.sh" --archive "$archive" --rid "$rid" --version "$version"
done

win_x64_name=$(release_archive_name "$version" win-x64)
win_arm64_name=$(release_archive_name "$version" win-arm64)
base_url="https://github.com/$repository/releases/download/$tag"
repository_url="https://github.com/$repository"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-winget)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-winget.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

printf '%s\n' "$publisher" | LC_ALL=C grep -Eq "^[A-Za-z0-9._ ,&()/+'-]+$" ||
  release_die 'publisher contains a character outside the pinned WingetCreate plain-scalar contract'
case $publisher in ' '*|*' ') release_die 'publisher must not start or end with whitespace' ;; esac
win_x64_sha256=$(release_sha256 "$artifacts/$win_x64_name" | tr 'a-f' 'A-F')
win_arm64_sha256=$(release_sha256 "$artifacts/$win_arm64_name" | tr 'a-f' 'A-F')
raw_directory="$work_dir/raw"
mkdir "$raw_directory"
release_render_template "$release_repository_root/packaging/winget/version.yaml.template" "$raw_directory/$package_id.yaml" \
  __PACKAGE_ID__ "$package_id" __VERSION__ "$version"
release_render_template "$release_repository_root/packaging/winget/installer.yaml.template" "$raw_directory/$package_id.installer.yaml" \
  __PACKAGE_ID__ "$package_id" __VERSION__ "$version" \
  __WIN_X64_URL__ "$base_url/$win_x64_name" \
  __WIN_X64_SHA256__ "$win_x64_sha256" \
  __WIN_ARM64_URL__ "$base_url/$win_arm64_name" \
  __WIN_ARM64_SHA256__ "$win_arm64_sha256"
release_render_template "$release_repository_root/packaging/winget/locale.en-US.yaml.template" "$raw_directory/$package_id.locale.en-US.yaml" \
  __PACKAGE_ID__ "$package_id" __VERSION__ "$version" __PUBLISHER__ "$publisher" \
  __REPOSITORY_URL__ "$repository_url" __LICENSE_URL__ "$repository_url/blob/$tag/LICENSES.md"

# WingetCreate does not commit the caller's input bytes. Its submit command
# deserializes and reserializes every manifest. Keep our comparison bundle in
# the exact pinned post-serialization representation so PR/published reruns are
# genuinely byte-idempotent.
for raw_manifest in "$raw_directory"/*.yaml; do
  python3 "$release_script_dir/canonicalize-winget-manifest.py" \
    --input "$raw_manifest" --output "$work_dir/$(basename -- "$raw_manifest")"
done

mkdir -p -- "$output_directory"
for generated in "$work_dir"/*.yaml; do
  generated_name=$(basename -- "$generated")
  release_assert_new_or_identical "$generated" "$output_directory/$generated_name"
done
release_note "generated WinGet manifest bundle in $output_directory"
