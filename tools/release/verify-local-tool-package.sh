#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-local-tool-package.sh --version VERSION --authors TEXT' \
    '       --repository-url URL [options]' \
    '' \
    'Options:' \
    '  --package-id ID  CLI NuGet package ID (default: dotnetjq)' \
    '  --project PATH   CLI project (default: src/DotNetJq.Cli/DotNetJq.Cli.csproj)' \
    '  --rid RID        override detected host RID (must match the host OS)' \
    '  --package-directory PATH  install already-built pointer/RID packages from PATH' \
    '  --native-archives PATH    byte-bind the selected RID package to its release archive'
}

version=
package_id=dotnetjq
project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
rid=
package_directory=
native_archives=
authors=
repository_url=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --project) [ "$#" -ge 2 ] || release_die '--project requires a value'; project=$2; shift 2 ;;
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --package-directory) [ "$#" -ge 2 ] || release_die '--package-directory requires a value'; package_directory=$2; shift 2 ;;
    --native-archives) [ "$#" -ge 2 ] || release_die '--native-archives requires a value'; native_archives=$2; shift 2 ;;
    --authors) [ "$#" -ge 2 ] || release_die '--authors requires a value'; authors=$2; shift 2 ;;
    --repository-url) [ "$#" -ge 2 ] || release_die '--repository-url requires a value'; repository_url=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_package_id "$package_id"
release_validate_single_line authors "$authors"
release_validate_single_line repository-url "$repository_url"
[ -f "$project" ] || release_die "CLI project does not exist: $project"
[ -n "$rid" ] || rid=$(release_host_rid)
expected_host=$(release_target_field "$rid" 3)
actual_host=$(release_host_os)
[ "$expected_host" = "$actual_host" ] ||
  release_die "RID $rid does not match the current host operating system"
[ -z "$package_directory" ] || [ -d "$package_directory" ] ||
  release_die "package directory does not exist: $package_directory"
[ -z "$native_archives" ] || [ -d "$native_archives" ] ||
  release_die "native archive directory does not exist: $native_archives"
release_require_command awk
release_require_command dotnet

work_parent=${TMPDIR:-/tmp}
work_dir=$(release_make_temp_dir "$work_parent" dotnetjq-local-tool)
cleanup() {
  case $work_dir in
    "$work_parent"/.dotnetjq-local-tool.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

tool_path="$work_dir/tool"
nuget_cache="$work_dir/nuget-cache"
dotnet_home="$work_dir/dotnet-home"
if [ -n "$package_directory" ]; then
  packages=$(CDPATH= cd -- "$package_directory" && pwd)
else
  packages="$work_dir/packages"
fi
mkdir -p -- "$packages" "$tool_path" "$nuget_cache" "$dotnet_home"

if [ -z "$package_directory" ]; then
  pack_environment=(env -u LD_PRELOAD)
  case $rid in linux-*) pack_environment+=(CC=gcc) ;; esac
  "${pack_environment[@]}" dotnet pack "$project" \
    --configuration Release \
    -p:Version="$version" \
    -p:PackageVersion="$version" \
    -p:Authors="$authors" \
    -p:RepositoryUrl="$repository_url" \
    --output "$packages" \
    --nologo
  "${pack_environment[@]}" dotnet pack "$project" \
    --disable-build-servers \
    --maxcpucount:1 \
    --configuration Release \
    --runtime "$rid" \
    -p:ContinuousIntegrationBuild=true \
    -p:DebugSymbols=false \
    -p:DebugType=none \
    -p:NuGetAudit=false \
    -p:PublishAot=true \
    -p:SelfContained=true \
    -p:Version="$version" \
    -p:PackageVersion="$version" \
    -p:Authors="$authors" \
    -p:RepositoryUrl="$repository_url" \
    -p:UseSharedCompilation=false \
    --output "$packages" \
    --nologo
fi

package_verifier=(
  "$release_script_dir/verify-nuget-package-set.sh"
  --directory "$packages"
  --package-id "$package_id"
  --version "$version"
  --rid "$rid"
  --project "$project"
  --authors "$authors"
  --repository-url "$repository_url"
)
if [ -n "$native_archives" ]; then
  package_verifier+=(--native-archives "$native_archives")
fi
"${package_verifier[@]}"

nuget_config="$work_dir/NuGet.Config"
printf '%s\n' \
  '<?xml version="1.0" encoding="utf-8"?>' \
  '<configuration>' \
  '  <packageSources>' \
  '    <clear />' \
  '  </packageSources>' \
  '</configuration>' >"$nuget_config"

install_cache=$nuget_cache
install_home=$dotnet_home
if [ "$actual_host" = windows ] && command -v cygpath >/dev/null 2>&1; then
  install_cache=$(cygpath -aw -- "$nuget_cache")
  install_home=$(cygpath -aw -- "$dotnet_home")
fi
install_local_tool() {
  NUGET_PACKAGES="$install_cache" DOTNET_CLI_HOME="$install_home" \
  dotnet tool install "$package_id" \
    --tool-path "$tool_path" \
    --version "$version" \
    "$@" \
    --configfile "$nuget_config" \
    --add-source "$packages" \
    --no-http-cache
}
case $rid in
  win-*)
    # The Windows ARM64 runner can execute an x64 SDK under emulation. Force
    # the native matrix architecture instead of allowing the SDK process RID
    # to select win-x64. On Unix, --arch in the pinned SDK still goes through
    # its legacy four-RID validation path, so preserving native host selection
    # is both exact and required for the supported Linux RIDs.
    install_arch=$(release_target_field "$rid" 4)
    install_local_tool --arch "$install_arch"
    ;;
  *) install_local_tool ;;
esac

assert_installed_package_matches() {
  local id=$1
  local expected_package=$2
  local expected_name
  local normalized_id
  local installed_package_count
  local installed_package
  expected_name=$(basename -- "$expected_package")
  normalized_id=$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')
  local tool_store="$tool_path/.store"
  local installed_inventory="$work_dir/$normalized_id.installed-packages"
  [ -d "$tool_store" ] ||
    release_die 'local tool install did not create its private package store'
  find "$tool_store" -type f -iname "$expected_name" | LC_ALL=C sort \
    >"$installed_inventory"
  installed_package_count=$(awk 'END { print NR }' "$installed_inventory")
  [ "$installed_package_count" -eq 1 ] ||
    release_die "installed tool store must contain exactly one nupkg for $id"
  installed_package=$(sed -n '1p' "$installed_inventory")
  cmp -s -- "$expected_package" "$installed_package" ||
    release_die "installed package for $id is not byte-identical to the local feed"
}

assert_installed_package_matches \
  "$package_id" "$packages/$package_id.$version.nupkg"
assert_installed_package_matches \
  "$package_id.$rid" "$packages/$package_id.$rid.$version.nupkg"

normalized_package_id=$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')
normalized_rid=$(printf '%s' "$rid" | tr '[:upper:]' '[:lower:]')
normalized_version=$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')
expected_pointer_name="$normalized_package_id.$normalized_version.nupkg"
expected_rid_name="$normalized_package_id.$normalized_rid.$normalized_version.nupkg"
installed_package_paths_file="$work_dir/installed-package-paths.txt"
installed_package_names_file="$work_dir/installed-package-names.txt"
expected_installed_package_names_file="$work_dir/expected-installed-package-names.txt"
find "$tool_path/.store" -type f -iname '*.nupkg' -print >"$installed_package_paths_file"
: >"$installed_package_names_file"
while IFS= read -r installed_package_path; do
  printf '%s\n' "${installed_package_path##*/}" |
    tr '[:upper:]' '[:lower:]' >>"$installed_package_names_file"
done <"$installed_package_paths_file"
LC_ALL=C sort -o "$installed_package_names_file" "$installed_package_names_file"
printf '%s\n' \
  "$expected_pointer_name" \
  "$normalized_package_id.nupkg" \
  "$expected_rid_name" \
  "$normalized_package_id.$normalized_rid.nupkg" |
  LC_ALL=C sort >"$expected_installed_package_names_file"
if ! cmp -s -- "$expected_installed_package_names_file" "$installed_package_names_file"; then
  installed_package_names=$(tr '\n' ' ' <"$installed_package_names_file")
  release_die "selector resolved a package other than $package_id and $package_id.$rid; found: ${installed_package_names:-<none>}"
fi

case $rid in
  win-*)
    release_require_command pwsh
    release_require_command powershell.exe
    executable="$tool_path/dotnetjq.exe"
    [ -f "$executable" ] || release_die 'local tool install did not create dotnetjq.exe'
    release_verify_binary_format "$executable" "$rid"
    windows_executable=$executable
    windows_test_script="$release_repository_root/tests/powershell/Verify-DotNetJq.ps1"
    if command -v cygpath >/dev/null 2>&1; then
      windows_executable=$(cygpath -aw -- "$executable")
      windows_test_script=$(cygpath -aw -- "$windows_test_script")
    fi
    pwsh -NoLogo -NoProfile -NonInteractive -File \
      "$windows_test_script" -CliPath "$windows_executable" \
      -ExpectedShell PowerShell7 -ExpectedVersion "$version"
    powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass \
      -File "$windows_test_script" -CliPath "$windows_executable" \
      -ExpectedShell WindowsPowerShell51 -ExpectedVersion "$version"
    ;;
  *)
    executable="$tool_path/dotnetjq"
    [ -x "$executable" ] || release_die 'local tool install did not create an executable dotnetjq'
    release_verify_binary_format "$executable" "$rid"
    sh "$release_repository_root/tests/cli-shell/verify.sh" "$executable" "$version"
    ;;
esac

NUGET_PACKAGES="$install_cache" DOTNET_CLI_HOME="$install_home" \
dotnet tool uninstall "$package_id" --tool-path "$tool_path"
[ ! -e "$executable" ] && [ ! -L "$executable" ] ||
  release_die 'dotnet tool uninstall left the dotnetjq launcher installed'
tool_store="$tool_path/.store"
if [ -e "$tool_store" ] || [ -L "$tool_store" ]; then
  [ -d "$tool_store" ] && [ ! -L "$tool_store" ] ||
    release_die 'dotnet tool uninstall left a non-directory private store'
  # .NET 10 may retain an empty .store/.stage directory after a successful
  # uninstall. Empty directory scaffolding is not installed package state;
  # every file, symlink, or other payload still is.
  residual_store_payload=$(find "$tool_store" -mindepth 1 ! -type d -print -quit)
  [ -z "$residual_store_payload" ] ||
    release_die "dotnet tool uninstall left package payload in its private store: $residual_store_payload"
fi

release_note "local $package_id $version tool install/run/uninstall lifecycle passed for $rid"
