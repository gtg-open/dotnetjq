#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: build-native-archive.sh --rid RID --version VERSION [options]' \
    '' \
    'Options:' \
    '  --project PATH               CLI project (default: src/DotNetJq.Cli/DotNetJq.Cli.csproj)' \
    '  --output-directory PATH      artifact directory (default: artifacts/release)' \
    '  --configuration NAME         build configuration (default: Release)' \
    '  --published-executable NAME  publish output name (default: auto-detect)' \
    '  --execute-validation         execute --version after archive validation' \
    '  --performance-provenance PATH  emit the linux-x64 performance producer proof'
}

rid=
version=
project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
output_directory="$release_repository_root/artifacts/release"
configuration=Release
published_executable=
execute_validation=0
performance_provenance=

while [ "$#" -gt 0 ]; do
  case $1 in
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --project) [ "$#" -ge 2 ] || release_die '--project requires a value'; project=$2; shift 2 ;;
    --output-directory) [ "$#" -ge 2 ] || release_die '--output-directory requires a value'; output_directory=$2; shift 2 ;;
    --configuration) [ "$#" -ge 2 ] || release_die '--configuration requires a value'; configuration=$2; shift 2 ;;
    --published-executable) [ "$#" -ge 2 ] || release_die '--published-executable requires a value'; published_executable=$2; shift 2 ;;
    --execute-validation) execute_validation=1; shift ;;
    --performance-provenance) [ "$#" -ge 2 ] || release_die '--performance-provenance requires a value'; performance_provenance=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$rid" ] || release_die '--rid is required'
release_validate_version "$version"
[ -f "$project" ] || release_die "CLI project does not exist: $project"
if [ -n "$performance_provenance" ]; then
  [ "$rid" = linux-x64 ] && [ "$configuration" = Release ] &&
    [ -z "$published_executable" ] && [ "$execute_validation" -eq 1 ] ||
    release_die '--performance-provenance requires canonical linux-x64 Release build options and --execute-validation'
  [ "$project" = "$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj" ] ||
    release_die '--performance-provenance requires the canonical CLI project'
fi
expected_host=$(release_target_field "$rid" 3)
actual_host=$(release_host_os)
[ "$actual_host" = "$expected_host" ] ||
  release_die "RID $rid must be built on $expected_host, not $actual_host"

release_require_command dotnet
release_require_command python3
python3 "$release_script_dir/verify-packaging-text-inputs.py"
mkdir -p -- "$output_directory"
archive_name=$(release_archive_name "$version" "$rid")
if [ -n "$performance_provenance" ]; then
  resolved_output_directory=$(python3 -c \
    'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' \
    "$output_directory")
  resolved_performance_provenance=$(python3 -c \
    'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' \
    "$performance_provenance")
  expected_performance_provenance="$resolved_output_directory/$archive_name.provenance.json"
  [ "$resolved_performance_provenance" = "$expected_performance_provenance" ] ||
    release_die "performance provenance must be the canonical archive sidecar: $expected_performance_provenance"
  performance_provenance=$expected_performance_provenance
fi
work_dir=$(release_make_temp_dir "$output_directory" "dotnetjq-$rid-build")
cleanup() {
  case $work_dir in
    "$output_directory"/.dotnetjq-*-build.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

publish_directory="$work_dir/publish"
stage_directory="$work_dir/stage"
mkdir -p -- "$publish_directory" "$stage_directory"

if [ -n "$performance_provenance" ]; then
  performance_snapshot="$work_dir/performance-prebuild.json"
  python3 "$release_repository_root/tools/performance/build_attestation.py" \
    --root "$release_repository_root" snapshot --output "$performance_snapshot"
fi

dotnet publish "$project" \
  --disable-build-servers \
  --maxcpucount:1 \
  --configuration "$configuration" \
  --runtime "$rid" \
  --self-contained true \
  --output "$publish_directory" \
  --artifacts-path "$work_dir/build" \
  -p:ContinuousIntegrationBuild=true \
  -p:NuGetAudit=false \
  -p:PublishAot=true \
  -p:DebugSymbols=false \
  -p:DebugType=none \
  -p:Version="$version" \
  -p:PackageVersion="$version" \
  -p:UseSharedCompilation=false \
  --nologo

if [ -n "$performance_provenance" ]; then
  performance_native_aot_snapshot="$work_dir/performance-native-aot.json"
  python3 "$release_repository_root/tools/performance/build_attestation.py" \
    --root "$release_repository_root" snapshot-native-aot \
    --assets "$work_dir/build/obj/DotNetJq.Cli/project.assets.json" \
    --output "$performance_native_aot_snapshot"
fi

if [ -n "$published_executable" ]; then
  source_executable="$publish_directory/$published_executable"
else
  case $rid in
    win-*) suffix=.exe ;;
    *) suffix= ;;
  esac
  source_executable=
  for candidate in "$publish_directory/dotnetjq$suffix" "$publish_directory/DotNetJq.Cli$suffix"; do
    if [ -f "$candidate" ]; then
      source_executable=$candidate
      break
    fi
  done
fi
[ -n "$source_executable" ] && [ -f "$source_executable" ] ||
  release_die 'could not identify the NativeAOT executable in publish output'

managed_publish_payload=$(find "$publish_directory" -maxdepth 1 -type f \
  \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) \
  -print -quit)
if [ -n "$managed_publish_payload" ]; then
  release_die 'publish output is not a standalone NativeAOT application'
fi

case $rid in
  win-*) archive_executable=dotnetjq.exe ;;
  *) archive_executable=dotnetjq ;;
esac
cp -- "$source_executable" "$stage_directory/$archive_executable"
chmod 0755 "$stage_directory/$archive_executable"

while IFS= read -r required; do
  case $required in ''|'#'*) continue ;; esac
  [ -e "$release_repository_root/$required" ] || release_die "required release path is missing: $required"
  if [ -d "$release_repository_root/$required" ]; then
    cp -R -- "$release_repository_root/$required" "$stage_directory/$required"
  else
    cp -- "$release_repository_root/$required" "$stage_directory/$required"
  fi
done <"$release_license_paths_file"

runtime_license_paths_file="$release_repository_root/packaging/nativeaot-runtime-license-paths.txt"
while IFS= read -r runtime_license; do
  case $runtime_license in ''|'#'*) continue ;; esac
  [ -f "$publish_directory/$runtime_license" ] ||
    release_die "NativeAOT publish output is missing resolved runtime license: $runtime_license"
  cp -- "$publish_directory/$runtime_license" "$stage_directory/$runtime_license"
done <"$runtime_license_paths_file"

# Normalize modes as well as timestamps so the source checkout's mode bits do
# not affect archive bytes. The executable remains runnable on Unix.
find "$stage_directory" -type d -exec chmod 0755 {} +
find "$stage_directory" -type f -exec chmod 0644 {} +
chmod 0755 "$stage_directory/$archive_executable"
# Fixed timestamps and sorted member order avoid needless archive churn.
find "$stage_directory" -exec touch -t 198001010000 {} +

candidate_archive="$work_dir/$archive_name"
format=$(release_target_field "$rid" 2)
case $format in
  zip)
    dotnet run \
      --configuration Release \
      --project "$release_script_dir/DotNetJq.DeterministicZip/DotNetJq.DeterministicZip.csproj" \
      -- "$stage_directory" "$candidate_archive"
    ;;
  tar.gz)
    release_require_command gzip
    gnu_tar=
    if command -v tar >/dev/null 2>&1 && tar --version 2>/dev/null | grep 'GNU tar' >/dev/null 2>&1; then
      gnu_tar=tar
    elif command -v gtar >/dev/null 2>&1 && gtar --version 2>/dev/null | grep 'GNU tar' >/dev/null 2>&1; then
      gnu_tar=gtar
    else
      release_die 'GNU tar is required for deterministic tar.gz archives (install tar or gtar)'
    fi
    "$gnu_tar" --sort=name --mtime='1980-01-01 00:00:00Z' --owner=0 --group=0 --numeric-owner \
      --format=gnu -cf - -C "$stage_directory" . | gzip -n >"$candidate_archive"
    ;;
esac

validation_args=(--archive "$candidate_archive" --rid "$rid" --version "$version" --project "$project")
if [ "$execute_validation" -eq 1 ]; then
  validation_args+=(--execute)
fi
"$release_script_dir/verify-native-archive.sh" "${validation_args[@]}"

release_assert_new_or_identical "$candidate_archive" "$output_directory/$archive_name"
if [ -n "$performance_provenance" ]; then
  candidate_performance_provenance="$work_dir/$archive_name.provenance.json"
  python3 "$release_repository_root/tools/performance/build_attestation.py" \
    --root "$release_repository_root" create-release-provenance \
    --snapshot "$performance_snapshot" \
    --archive "$output_directory/$archive_name" \
    --executable "$stage_directory/$archive_executable" \
    --native-aot-snapshot "$performance_native_aot_snapshot" \
    --version "$version" --output "$candidate_performance_provenance"
  release_assert_new_or_identical \
    "$candidate_performance_provenance" "$performance_provenance"
fi
release_note "created $output_directory/$archive_name"
