#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-release-bundle.sh --version VERSION --artifacts PATH' \
    '       --package-id ID --proof FILE --repository OWNER/NAME' \
    '       --authors TEXT --publisher TEXT --winget-package-id ID [options]' \
    '' \
    'Options:' \
    '  --library-package-id ID  managed library package ID (default: DotNetJq.Library)' \
    '  --proof-rid LINUX_RID    RID recorded by the relink proof (default: linux-x64)' \
    '  --tag TAG                release tag used by generated URLs (default: vVERSION)' \
    '' \
    'This validates local artifacts only. It never publishes them.'
}

version=
artifacts=
package_id=
library_package_id=DotNetJq.Library
proof=
proof_rid=linux-x64
repository=
authors=
publisher=
winget_package_id=
tag=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --artifacts) [ "$#" -ge 2 ] || release_die '--artifacts requires a value'; artifacts=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --library-package-id) [ "$#" -ge 2 ] || release_die '--library-package-id requires a value'; library_package_id=$2; shift 2 ;;
    --proof) [ "$#" -ge 2 ] || release_die '--proof requires a value'; proof=$2; shift 2 ;;
    --proof-rid) [ "$#" -ge 2 ] || release_die '--proof-rid requires a value'; proof_rid=$2; shift 2 ;;
    --repository) [ "$#" -ge 2 ] || release_die '--repository requires a value'; repository=$2; shift 2 ;;
    --authors) [ "$#" -ge 2 ] || release_die '--authors requires a value'; authors=$2; shift 2 ;;
    --publisher) [ "$#" -ge 2 ] || release_die '--publisher requires a value'; publisher=$2; shift 2 ;;
    --winget-package-id) [ "$#" -ge 2 ] || release_die '--winget-package-id requires a value'; winget_package_id=$2; shift 2 ;;
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_package_id "$package_id"
release_validate_package_id "$library_package_id"
release_validate_repository "$repository"
release_validate_single_line authors "$authors"
release_validate_single_line publisher "$publisher"
release_validate_package_id "$winget_package_id"
[ -n "$tag" ] || tag="v$version"
case $tag in *[!0-9A-Za-z._-]*) release_die "invalid release tag: $tag" ;; esac
[ -d "$artifacts" ] || release_die "artifact directory does not exist: ${artifacts-}"
release_require_command awk
release_require_command realpath

inventory_work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-release-bundle)
cleanup_inventory() {
  case $inventory_work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-release-bundle.*) rm -rf -- "$inventory_work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $inventory_work_dir" ;;
  esac
}
trap cleanup_inventory EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
expected_archives="$inventory_work_dir/native-archives.expected"
actual_archives="$inventory_work_dir/native-archives.actual"
expected_bundle="$inventory_work_dir/bundle.expected"
actual_bundle="$inventory_work_dir/bundle.actual"
configured_targets="$inventory_work_dir/release-targets.txt"
release_each_target >"$configured_targets"
target_count=$(awk 'END { print NR }' "$configured_targets")
expected_package_count=$((target_count + 2))
actual_package_count=$(find "$artifacts" -maxdepth 1 -type f -name '*.nupkg' | wc -l | tr -d ' ')
[ "$actual_package_count" -eq "$expected_package_count" ] ||
  release_die "release bundle must contain exactly $expected_package_count nupkgs; found $actual_package_count"

: >"$expected_archives"
: >"$expected_bundle"
printf '%s\n' \
  SHA256SUMS \
  aot-relink-proof.txt \
  dotnetjq.rb \
  "dotnetjq-aot-source-$version.tar.gz" \
  "dotnetjq-aot-source-$version.tar.gz.sha256" \
  nuget-publish-order.tsv \
  "$library_package_id.$version.nupkg" \
  "$package_id.$version.nupkg" \
  "$winget_package_id.yaml" \
  "$winget_package_id.installer.yaml" \
  "$winget_package_id.locale.en-US.yaml" \
  >>"$expected_bundle"
while IFS= read -r rid; do
  archive_name=$(release_archive_name "$version" "$rid")
  printf '%s\n' "$archive_name" >>"$expected_archives"
  printf '%s\n' "$archive_name" "$package_id.$rid.$version.nupkg" >>"$expected_bundle"
done <"$configured_targets"
LC_ALL=C sort -u -o "$expected_archives" "$expected_archives"
LC_ALL=C sort -u -o "$expected_bundle" "$expected_bundle"
(
  cd -- "$artifacts"
  find . -maxdepth 1 -type f \( -name '*.zip' -o -name '*.tar.gz' \) \
    ! -name "dotnetjq-aot-source-$version.tar.gz" -print |
    sed 's|^\./||' |
    LC_ALL=C sort -u
) >"$actual_archives"
cmp -s -- "$expected_archives" "$actual_archives" ||
  release_die 'release bundle native archive inventory does not exactly match the eight supported RIDs'
(
  cd -- "$artifacts"
  find . -mindepth 1 -maxdepth 1 -type f -print |
    sed 's|^\./||' |
    LC_ALL=C sort -u
) >"$actual_bundle"
cmp -s -- "$expected_bundle" "$actual_bundle" ||
  release_die 'release bundle top-level file inventory does not exactly match the 27 required artifacts'
unexpected_bundle_entry=$(find "$artifacts" -mindepth 1 -maxdepth 1 ! -type f -print -quit)
if [ -n "$unexpected_bundle_entry" ]; then
  release_die 'release bundle must contain only top-level regular files'
fi
bundle_proof="$artifacts/aot-relink-proof.txt"
[ -f "$proof" ] || release_die "AOT relink proof does not exist: ${proof-}"
[ "$(realpath -- "$proof")" = "$(realpath -- "$bundle_proof")" ] ||
  release_die '--proof must identify the exact aot-relink-proof.txt carried by the release bundle'
proof=$bundle_proof

while IFS= read -r rid; do
  "$release_script_dir/verify-native-archive.sh" \
    --archive "$artifacts/$(release_archive_name "$version" "$rid")" \
    --rid "$rid" --version "$version"
done <"$configured_targets"

"$release_script_dir/verify-aot-source-archive.sh" \
  --archive "$artifacts/dotnetjq-aot-source-$version.tar.gz" --version "$version"
"$release_script_dir/verify-nuget-package-set.sh" \
  --directory "$artifacts" --package-id "$package_id" \
  --library-package-id "$library_package_id" --version "$version" \
  --authors "$authors" --repository-url "https://github.com/$repository" \
  --native-archives "$artifacts"
"$release_script_dir/verify-checksums.sh" --directory "$artifacts"
"$release_script_dir/assert-publication-ready.sh" \
  --version "$version" --artifacts "$artifacts" --proof "$proof" --rid "$proof_rid" \
  --package-id "$package_id" --library-package-id "$library_package_id" \
  --repository "$repository" --publisher "$publisher" \
  --winget-package-id "$winget_package_id" --tag "$tag"

release_note 'complete local release bundle and AOT relinking evidence verified'
