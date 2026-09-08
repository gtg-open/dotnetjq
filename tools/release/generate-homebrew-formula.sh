#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: generate-homebrew-formula.sh --version VERSION --repository OWNER/NAME' \
    '       --artifacts PATH --output FILE [--tag TAG]'
}

version=
repository=
artifacts=
output=
tag=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --repository) [ "$#" -ge 2 ] || release_die '--repository requires a value'; repository=$2; shift 2 ;;
    --artifacts) [ "$#" -ge 2 ] || release_die '--artifacts requires a value'; artifacts=$2; shift 2 ;;
    --output) [ "$#" -ge 2 ] || release_die '--output requires a value'; output=$2; shift 2 ;;
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_repository "$repository"
[ -d "$artifacts" ] || release_die "artifact directory does not exist: ${artifacts-}"
[ -n "$output" ] || release_die '--output is required'
[ "$(basename -- "$output")" = dotnetjq.rb ] || release_die 'Homebrew formula output must be named dotnetjq.rb'
[ -n "$tag" ] || tag="v$version"
case $tag in *[!0-9A-Za-z._-]*) release_die "invalid release tag: $tag" ;; esac

for rid in osx-x64 osx-arm64 linux-x64 linux-arm64; do
  archive="$artifacts/$(release_archive_name "$version" "$rid")"
  "$release_script_dir/verify-native-archive.sh" --archive "$archive" --rid "$rid" --version "$version"
done
aot_source_name="dotnetjq-aot-source-$version.tar.gz"
aot_source_archive="$artifacts/$aot_source_name"
"$release_script_dir/verify-aot-source-archive.sh" \
  --archive "$aot_source_archive" --version "$version"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-homebrew)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-homebrew.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

base_url="https://github.com/$repository/releases/download/$tag"
template="$release_repository_root/packaging/homebrew/dotnetjq.rb.template"
candidate="$work_dir/dotnetjq.rb"
aot_source_sha256=$(release_sha256 "$aot_source_archive")
template_args=(
  __REPOSITORY_URL__ "https://github.com/$repository"
  __VERSION__ "$version"
  __AOT_SOURCE_URL__ "$base_url/$aot_source_name"
  __AOT_SOURCE_SHA256__ "$aot_source_sha256"
)
for rid in osx-arm64 osx-x64 linux-arm64 linux-x64; do
  name=$(release_archive_name "$version" "$rid")
  token=$(printf '%s' "$rid" | tr 'a-z-' 'A-Z_')
  archive_sha256=$(release_sha256 "$artifacts/$name")
  template_args+=("__${token}_URL__" "$base_url/$name")
  template_args+=("__${token}_SHA256__" "$archive_sha256")
done
release_render_template "$template" "$candidate" "${template_args[@]}"

mkdir -p -- "$(dirname -- "$output")"
release_assert_new_or_identical "$candidate" "$output"
release_note "generated Homebrew formula: $output"
