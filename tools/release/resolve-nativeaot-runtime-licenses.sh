#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: resolve-nativeaot-runtime-licenses.sh --rid RID --output PATH [options]' \
    '' \
    'Options:' \
    '  --project PATH  CLI project (default: src/DotNetJq.Cli/DotNetJq.Cli.csproj)'
}

rid=
output=
project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
while [ "$#" -gt 0 ]; do
  case $1 in
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --output) [ "$#" -ge 2 ] || release_die '--output requires a value'; output=$2; shift 2 ;;
    --project) [ "$#" -ge 2 ] || release_die '--project requires a value'; project=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$rid" ] || release_die '--rid is required'
release_target_field "$rid" 2 >/dev/null
[ -n "$output" ] || release_die '--output is required'
[ -f "$project" ] || release_die "CLI project does not exist: $project"
release_require_command dotnet
host_os=$(release_host_os)

output_parent=$(CDPATH= cd -- "$(dirname -- "$output")" && pwd)
output="$output_parent/$(basename -- "$output")"
case $output in
  "$output_parent"/*) ;;
  *) release_die "invalid output path: $output" ;;
esac

work_dir=$(release_make_temp_dir "$output_parent" nativeaot-runtime-license-resolve)
cleanup() {
  case $work_dir in
    "$output_parent"/.nativeaot-runtime-license-resolve.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
raw_manifest="$work_dir/msbuild-paths.txt"
msbuild_manifest=$raw_manifest
if [ "$host_os" = windows ] && command -v cygpath >/dev/null 2>&1; then
  msbuild_manifest=$(cygpath -aw -- "$raw_manifest")
fi

dotnet msbuild "$project" \
  -restore \
  -target:WriteResolvedNativeAotRuntimeLicenseManifest \
  -property:RuntimeIdentifier="$rid" \
  -property:PublishAot=true \
  -property:NativeAotRuntimeLicenseManifestPath="$msbuild_manifest" \
  -verbosity:quiet \
  -nologo

[ -f "$raw_manifest" ] || release_die "MSBuild did not write the NativeAOT runtime-license manifest: $raw_manifest"
candidate_manifest="$work_dir/resolved-paths.txt"
: >"$candidate_manifest"
while IFS= read -r source || [ -n "$source" ]; do
  source=${source%$'\r'}
  if [ "$host_os" = windows ] && command -v cygpath >/dev/null 2>&1; then
    source=$(cygpath -au -- "$source")
  fi
  printf '%s\n' "$source" >>"$candidate_manifest"
done <"$raw_manifest"

[ "$(wc -l <"$candidate_manifest" | tr -d ' ')" -eq 2 ] ||
  release_die "resolved NativeAOT runtime-license manifest must contain exactly two files: $raw_manifest"

expected_names="$release_repository_root/packaging/nativeaot-runtime-license-paths.txt"
resolved_names="$work_dir/resolved-names.txt"
configured_names="$work_dir/configured-names.txt"
sed 's#\\#/#g; s#.*/##' "$candidate_manifest" | LC_ALL=C sort >"$resolved_names"
sed '/^#/d; /^$/d' "$expected_names" | LC_ALL=C sort >"$configured_names"
cmp -s "$resolved_names" "$configured_names" ||
  release_die "resolved NativeAOT runtime-license names do not match $expected_names"

while IFS= read -r source; do
  [ -f "$source" ] || release_die "resolved NativeAOT runtime license does not exist: $source"
done <"$candidate_manifest"

mv -- "$candidate_manifest" "$output"
