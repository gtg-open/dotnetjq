#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

version=0.0.0-reproducibility
authors='DotNetJq reproducibility test'
repository_url=https://github.com/example/dotnetjq
cli_project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
library_project="$release_repository_root/src/DotNetJq/DotNetJq.csproj"
# CI supplies the matrix RID explicitly: an emulated shell's architecture is
# not evidence that the package under test matches the intended release target.
if [ "$#" -eq 0 ]; then
  rid=$(release_host_rid)
elif [ "$#" -eq 2 ] && [ "$1" = --rid ]; then
  rid=$2
  release_target_field "$rid" 2 >/dev/null
else
  release_die 'usage: test-nuget-reproducibility.sh [--rid RID]'
fi

release_require_command cmp
release_require_command dotnet
release_require_command python3
python3 "$release_script_dir/test-nupkg-archive.py"
python3 "$release_script_dir/test-nupkg-comparison.py"
python3 "$release_script_dir/test-compiler-path-map.py"
python3 -B "$release_script_dir/test-windows-tool-shim.py"

work_parent=${TMPDIR:-/tmp}
work_dir=$(release_make_temp_dir "$work_parent" dotnetjq-nuget-reproducibility)
cleanup() {
  case $work_dir in
    "$work_parent"/.dotnetjq-nuget-reproducibility.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

pack_set() {
  local build_root=$1
  local output="$build_root/packages"
  # Normalize path spelling before mapping. A macOS TMPDIR trailing slash
  # previously produced a double slash that C# normalized but PathMap did not;
  # Windows needs native paths, not Git Bash's /c/... spelling.
  local path_map
  path_map=$(python3 "$release_script_dir/compiler-path-map.py" \
    "$release_repository_root" "$build_root")
  mkdir -p -- \
    "$output" \
    "$build_root/artifacts" \
    "$build_root/cli-home" \
    "$build_root/nuget-http-cache" \
    "$build_root/nuget-packages" \
    "$build_root/nuget-plugins-cache"
  (
    export DOTNET_CLI_HOME="$build_root/cli-home"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
    export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    export NUGET_HTTP_CACHE_PATH="$build_root/nuget-http-cache"
    export NUGET_PACKAGES="$build_root/nuget-packages"
    export NUGET_PLUGINS_CACHE_PATH="$build_root/nuget-plugins-cache"
    export MSYS2_ARG_CONV_EXCL="${MSYS2_ARG_CONV_EXCL:+$MSYS2_ARG_CONV_EXCL;}-p:PathMap="

    dotnet pack "$cli_project" \
      --artifacts-path "$build_root/artifacts" \
      --disable-build-servers \
      --force \
      --maxcpucount:1 \
      --configuration Release \
      -p:NuGetAudit=false \
      -p:PathMap="$path_map" \
      -p:Version="$version" \
      -p:PackageVersion="$version" \
      -p:Authors="$authors" \
      -p:RepositoryUrl="$repository_url" \
      -p:UseSharedCompilation=false \
      --output "$output" \
      --nologo
    dotnet pack "$library_project" \
      --artifacts-path "$build_root/artifacts" \
      --disable-build-servers \
      --force \
      --maxcpucount:1 \
      --configuration Release \
      -p:NuGetAudit=false \
      -p:PathMap="$path_map" \
      -p:Version="$version" \
      -p:PackageVersion="$version" \
      -p:Authors="$authors" \
      -p:RepositoryUrl="$repository_url" \
      -p:UseSharedCompilation=false \
      --output "$output" \
      --nologo
    dotnet pack "$cli_project" \
      --artifacts-path "$build_root/artifacts" \
      --disable-build-servers \
      --force \
      --maxcpucount:1 \
      --configuration Release \
      --runtime "$rid" \
      -p:ContinuousIntegrationBuild=true \
      -p:DebugSymbols=false \
      -p:DebugType=none \
      -p:NuGetAudit=false \
      -p:PathMap="$path_map" \
      -p:PublishAot=true \
      -p:SelfContained=true \
      -p:Version="$version" \
      -p:PackageVersion="$version" \
      -p:Authors="$authors" \
      -p:RepositoryUrl="$repository_url" \
      -p:UseSharedCompilation=false \
      --output "$output" \
      --nologo
  )
}

first_root="$work_dir/first"
second_root="$work_dir/second"
pack_set "$first_root"
pack_set "$second_root"
first="$first_root/packages"
second="$second_root/packages"

package_names="DotNetJq.Library.$version.nupkg
dotnetjq.$version.nupkg
dotnetjq.$rid.$version.nupkg"
failed=0
while IFS= read -r package_name; do
  [ -f "$first/$package_name" ] || release_die "first pack omitted $package_name"
  [ -f "$second/$package_name" ] || release_die "second pack omitted $package_name"
  python3 "$release_script_dir/verify-nupkg-archive.py" \
    "$first/$package_name" "$second/$package_name"
  if ! cmp -s -- "$first/$package_name" "$second/$package_name"; then
    failed=1
    release_note "repeat pack was not byte-identical: $package_name"
    evidence="$release_repository_root/artifacts/reproducibility/$rid"
    mkdir -p "$evidence/first" "$evidence/second"
    cp -- "$first/$package_name" "$evidence/first/$package_name"
    cp -- "$second/$package_name" "$evidence/second/$package_name"
    # Keep all unequal pairs, not only the first library failure. The report
    # explains differences; cmp above remains the unmodified release gate.
    python3 "$release_script_dir/compare-nupkg.py" \
      "$first/$package_name" "$second/$package_name" \
      >"$evidence/$package_name.diff.json" || true
    sed -n '1,240p' "$evidence/$package_name.diff.json"
  fi
done <<EOF
$package_names
EOF

[ "$failed" -eq 0 ] || release_die "cold package builds differ; evidence: $evidence"
release_note "repeat pack is byte-identical for library, pointer, and $rid RID packages"

# Exercise the cold-built packages before discarding them. This runs on every
# PR's native matrix, so platform-specific installer/setup failures are caught
# before a release tag reaches the final, archive-bound bundle installation.
bash "$release_script_dir/test-package-host-prerequisites.sh"
bash "$release_script_dir/verify-local-tool-package.sh" \
  --version "$version" \
  --authors "$authors" \
  --repository-url "$repository_url" \
  --rid "$rid" \
  --project "$cli_project" \
  --package-directory "$first"
