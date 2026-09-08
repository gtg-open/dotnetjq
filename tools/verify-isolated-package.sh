#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
package_project="$repository_root/src/DotNetJq/DotNetJq.csproj"
component_source="$repository_root/src/DotNetJq.GlibcCompat"
consumer_template="$script_dir/package-isolation"
package_version="0.0.0-isolation"
supplied_package=""
authors=""
repository_url=""

usage() {
  printf '%s\n' \
    'Usage: verify-isolated-package.sh --authors TEXT --repository-url URL' \
    '       [--package FILE --version VERSION]' \
    '' \
    'Without --package, packs the local project as 0.0.0-isolation.' \
    'With --package, validates and executes that exact release artifact.'
}

while (($# > 0)); do
  case "$1" in
    --package)
      (($# >= 2)) || { printf '%s\n' '--package requires a value' >&2; exit 2; }
      supplied_package="$2"
      shift 2
      ;;
    --version)
      (($# >= 2)) || { printf '%s\n' '--version requires a value' >&2; exit 2; }
      package_version="$2"
      shift 2
      ;;
    --authors)
      (($# >= 2)) || { printf '%s\n' '--authors requires a value' >&2; exit 2; }
      authors="$2"
      shift 2
      ;;
    --repository-url)
      (($# >= 2)) || { printf '%s\n' '--repository-url requires a value' >&2; exit 2; }
      repository_url="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'unknown argument: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$authors" || "$authors" == *$'\n'* || "$authors" == *$'\r'* || "$authors" == *$'\t'* ]]; then
  printf 'authors must be an explicit single-line value\n' >&2
  exit 2
fi
if [[ ! "$repository_url" =~ ^https://github\.com/[0-9A-Za-z._-]+/[0-9A-Za-z._-]+$ ]]; then
  printf 'repository-url must be an explicit https://github.com/owner/name URL: %s\n' \
    "${repository_url:-<empty>}" >&2
  exit 2
fi

[[ "$package_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] || {
  printf 'invalid normalized package version: %s\n' "$package_version" >&2
  exit 2
}
prerelease="${package_version#*-}"
if [[ "$prerelease" != "$package_version" ]]; then
  old_ifs=$IFS
  IFS=.
  for identifier in $prerelease; do
    case "$identifier" in
      *[!0-9]*|0) ;;
      0*)
        IFS=$old_ifs
        printf 'invalid normalized package version: %s\n' "$package_version" >&2
        exit 2
        ;;
    esac
  done
  IFS=$old_ifs
fi

for required_command in \
  awk bwrap cmp diff dotnet find readlink realpath rg sha256sum sort strings unzip zipinfo; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'package isolation verification requires %s\n' "$required_command" >&2
    exit 2
  fi
done

if [[ -n "$supplied_package" ]]; then
  [[ -f "$supplied_package" ]] || {
    printf 'supplied package does not exist: %s\n' "$supplied_package" >&2
    exit 2
  }
  supplied_package="$(realpath -- "$supplied_package")"
fi

dotnet_executable="$(readlink -f -- "$(command -v dotnet)")"
dotnet_root="$(dirname -- "$dotnet_executable")"
masked_source_tree="$(dirname -- "$repository_root")"
if [[ ! -x "$dotnet_executable" || ! -d "$dotnet_root" ]]; then
  printf 'could not resolve the dotnet host used for package isolation\n' >&2
  exit 2
fi
if [[ ! -d "$masked_source_tree" || "$masked_source_tree" == "/" ]]; then
  printf 'refusing unsafe source-tree mask: %s\n' "$masked_source_tree" >&2
  exit 2
fi

temporary_parent="$(realpath -- "${TMPDIR:-/tmp}")"
if [[ ! -d "$temporary_parent" || "$temporary_parent" == "/" ]]; then
  printf 'refusing unsafe temporary parent: %s\n' "$temporary_parent" >&2
  exit 2
fi

validation_root="$(mktemp -d -- "$temporary_parent/dotnetjq-package-isolation.XXXXXXXX")"
cleanup_validation_root() {
  local resolved_root
  resolved_root="$(realpath -m -- "$validation_root")"
  if [[ -d "$resolved_root" && "$resolved_root" == "$temporary_parent"/dotnetjq-package-isolation.* ]]; then
    rm -rf -- "$resolved_root"
  else
    printf 'refusing unsafe cleanup target: %s\n' "$resolved_root" >&2
    return 1
  fi
}
trap cleanup_validation_root EXIT

package_feed="$validation_root/feed"
consumer_root="$validation_root/consumer"
nuget_cache="$validation_root/nuget-cache"
sandbox_home="$validation_root/sandbox-home"
sandbox_tmp="$validation_root/sandbox-tmp"
source_rebuild_root="$validation_root/source-rebuild"
mkdir -p -- "$package_feed" "$consumer_root" "$nuget_cache" "$sandbox_home" "$sandbox_tmp" "$source_rebuild_root"
cp -R -- "$consumer_template/." "$consumer_root/"

package_path="$package_feed/DotNetJq.Library.$package_version.nupkg"
if [[ -n "$supplied_package" ]]; then
  [[ "$(basename -- "$supplied_package")" = "$(basename -- "$package_path")" ]] || {
    printf 'supplied package filename must be %s\n' "$(basename -- "$package_path")" >&2
    exit 1
  }
  cp -- "$supplied_package" "$package_path"
else
  dotnet restore "$package_project" -p:NuGetAudit=false --verbosity quiet
  dotnet pack "$package_project" \
    --configuration Release \
    --no-restore \
    --output "$package_feed" \
    -p:Version="$package_version" \
    -p:PackageVersion="$package_version" \
    -p:Authors="$authors" \
    -p:RepositoryUrl="$repository_url" \
    --verbosity minimal
fi

if [[ ! -f "$package_path" ]]; then
  printf 'expected package was not produced: %s\n' "$package_path" >&2
  exit 1
fi
package_entries="$validation_root/package-entries.txt"
zipinfo -1 "$package_path" >"$package_entries"
extract_package_entry() {
  local entry="$1"
  local destination="$2"
  if ! unzip -p "$package_path" "$entry" >"$destination"; then
    printf 'could not extract package entry: %s\n' "$entry" >&2
    exit 1
  fi
}
for required_entry in \
  COPYING \
  COPYING.LIB \
  LICENSE.DotNetJq \
  LICENSES.md \
  README.md \
  THIRD_PARTY_NOTICES.md \
  THIRD_PARTY_LICENSES/GPPG-License.md \
  THIRD_PARTY_LICENSES/Oniguruma-License.txt \
  lib/net10.0/DotNetJq.dll \
  lib/net10.0/DotNetJq.GlibcCompat.dll \
  lib/net10.0/DotNetJq.GlibcCompat.xml \
  lib/net10.0/DotNetJq.xml \
  lgpl-source/Directory.Build.props \
  lgpl-source/global.json \
  lgpl-source/src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj \
  lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.cs \
  lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.Pow.cs \
  lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.X87.cs \
  lgpl-source/src/DotNetJq.GlibcCompat/COPYING.LIB \
  lgpl-source/src/DotNetJq.GlibcCompat/NuGet.Offline.Config \
  lgpl-source/src/DotNetJq.GlibcCompat/README.md; do
  if ! grep -Fxq "$required_entry" "$package_entries"; then
    printf 'package is missing required metadata file: %s\n' "$required_entry" >&2
    cat "$package_entries" >&2
    exit 1
  fi
done

packaged_component_documentation="$(
  unzip -p "$package_path" 'lib/net10.0/DotNetJq.GlibcCompat.xml'
)"
component_documentation_member_count="$(
  awk '
    {
      line = $0
      while ((position = index(line, "<member name=")) != 0) {
        count++
        line = substr(line, position + length("<member name="))
      }
    }
    END { print count + 0 }
  ' <<<"$packaged_component_documentation"
)"
if [[ "$component_documentation_member_count" -ne 5 ]]; then
  printf 'DotNetJq.GlibcCompat.xml must document exactly the public type and four ABI methods\n' >&2
  exit 1
fi
for documentation_fragment in \
  '<name>DotNetJq.GlibcCompat</name>' \
  'T:DotNetJq.GlibcCompat.GlibcCompatMath' \
  'M:DotNetJq.GlibcCompat.GlibcCompatMath.Gamma(System.Double)' \
  'M:DotNetJq.GlibcCompat.GlibcCompatMath.Lgamma(System.Double)' \
  'M:DotNetJq.GlibcCompat.GlibcCompatMath.LgammaR(System.Double)' \
  'M:DotNetJq.GlibcCompat.GlibcCompatMath.Tgamma(System.Double)' \
  'This operation is intentionally an alias' \
  'At positive and negative' \
  'infinity produce'; do
  if ! grep -Fq -- "$documentation_fragment" <<<"$packaged_component_documentation"; then
    printf 'DotNetJq.GlibcCompat.xml is missing its exact ABI semantics: %s\n' \
      "$documentation_fragment" >&2
    exit 1
  fi
done

package_hash="$(sha256sum "$package_path")"
package_hash="${package_hash%% *}"
packaged_main_hash="$(unzip -p "$package_path" 'lib/net10.0/DotNetJq.dll' | sha256sum)"
packaged_main_hash="${packaged_main_hash%% *}"
packaged_component_hash="$(unzip -p "$package_path" 'lib/net10.0/DotNetJq.GlibcCompat.dll' | sha256sum)"
packaged_component_hash="${packaged_component_hash%% *}"

packaged_entry_scratch="$validation_root/packaged-entry"
extract_package_entry COPYING "$packaged_entry_scratch"
if ! cmp -s "$packaged_entry_scratch" "$repository_root/COPYING.jq"; then
  printf 'packaged COPYING is not byte-identical to pinned COPYING.jq\n' >&2
  exit 1
fi

for metadata_entry in \
  LICENSE.DotNetJq \
  LICENSES.md \
  README.md \
  THIRD_PARTY_NOTICES.md \
  THIRD_PARTY_LICENSES/GPPG-License.md \
  THIRD_PARTY_LICENSES/Oniguruma-License.txt; do
  extract_package_entry "$metadata_entry" "$packaged_entry_scratch"
  if ! cmp -s "$packaged_entry_scratch" "$repository_root/$metadata_entry"; then
    printf 'packaged metadata differs from repository: %s\n' "$metadata_entry" >&2
    exit 1
  fi
done

expected_gppg_license_hash="354bb658c3465bf907ad308f9aa51eea9511ffff7db723f78a519b17758aabd4"
actual_gppg_license_hash="$(sha256sum "$repository_root/THIRD_PARTY_LICENSES/GPPG-License.md")"
actual_gppg_license_hash="${actual_gppg_license_hash%% *}"
if [[ "$actual_gppg_license_hash" != "$expected_gppg_license_hash" ]]; then
  printf 'GPPG-License.md is not the exact Springcomp.GPPG 1.2.5 license file\n' >&2
  exit 1
fi

expected_oniguruma_license_hash="70ba5469ea0bab6e18a32d7009068f996503168d27be57747e08da34337ff26f"
actual_oniguruma_license_hash="$(sha256sum "$repository_root/THIRD_PARTY_LICENSES/Oniguruma-License.txt")"
actual_oniguruma_license_hash="${actual_oniguruma_license_hash%% *}"
if [[ "$actual_oniguruma_license_hash" != "$expected_oniguruma_license_hash" ]]; then
  printf 'Oniguruma-License.txt is not the exact pinned Oniguruma license file\n' >&2
  exit 1
fi

expected_glibc_license_hash="dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551"
actual_glibc_license_hash="$(sha256sum "$repository_root/COPYING.LIB")"
actual_glibc_license_hash="${actual_glibc_license_hash%% *}"
if [[ "$actual_glibc_license_hash" != "$expected_glibc_license_hash" ]]; then
  printf 'COPYING.LIB is not the exact GNU C Library 2.39 license file\n' >&2
  exit 1
fi

for license_entry in COPYING.LIB lgpl-source/src/DotNetJq.GlibcCompat/COPYING.LIB; do
  extract_package_entry "$license_entry" "$packaged_entry_scratch"
  if ! cmp -s "$packaged_entry_scratch" "$repository_root/COPYING.LIB"; then
    printf 'packaged %s is not byte-identical to GNU C Library 2.39 COPYING.LIB\n' \
      "$license_entry" >&2
    exit 1
  fi
done

if ! cmp -s \
  "$repository_root/COPYING.LIB" \
  "$repository_root/src/DotNetJq.GlibcCompat/COPYING.LIB"; then
  printf 'repository GNU C Library license copies differ\n' >&2
  exit 1
fi

if ! grep -Fq 'No single license applies to every file in this package.' \
  "$repository_root/LICENSES.md" ||
  ! grep -Fq 'lib/net10.0/DotNetJq.GlibcCompat.dll' \
  "$repository_root/LICENSES.md" ||
  ! grep -Fq 'LGPL-2.1-or-later' "$repository_root/LICENSES.md" ||
  ! grep -Fq 'THIRD_PARTY_LICENSES/GPPG-License.md' \
  "$repository_root/LICENSES.md" ||
  ! grep -Fq 'THIRD_PARTY_LICENSES/Oniguruma-License.txt' \
  "$repository_root/LICENSES.md"; then
  printf 'LICENSES.md does not state the mixed-license component boundary\n' >&2
  exit 1
fi

component_source_inventory="$validation_root/component-source.inventory"
package_source_inventory="$validation_root/package-source.inventory"
package_source_prefix="lgpl-source/src/DotNetJq.GlibcCompat"

# Run each inventory producer as a checked pipeline. A process substitution
# would hide a failing find/zipinfo command from `set -e` and could validate a
# partial source set. Comparing the sorted inventories in both directions also
# rejects stale or injected package entries, not just missing repository files.
(
  cd -- "$component_source"
  LC_ALL=C find . -type f \
    ! -path './bin/*' \
    ! -path './obj/*' \
    -print |
    sed 's|^\./||' |
    LC_ALL=C sort
) >"$component_source_inventory"
awk -v prefix="$package_source_prefix/" '
    index($0, prefix) == 1 && substr($0, length($0), 1) != "/" {
      print substr($0, length(prefix) + 1)
    }
  ' "$package_entries" |
  LC_ALL=C sort > "$package_source_inventory"

if ! cmp -s "$component_source_inventory" "$package_source_inventory"; then
  printf 'package corresponding-source inventory differs from repository\n' >&2
  diff -u "$component_source_inventory" "$package_source_inventory" >&2 || true
  exit 1
fi

packaged_source_scratch="$validation_root/packaged-component-source"
while IFS= read -r relative_source; do
  source_file="$component_source/$relative_source"
  package_entry="lgpl-source/src/DotNetJq.GlibcCompat/$relative_source"
  if ! unzip -p "$package_path" "$package_entry" > "$packaged_source_scratch"; then
    printf 'could not extract packaged corresponding source: %s\n' \
      "$relative_source" >&2
    exit 1
  fi
  if ! cmp -s "$packaged_source_scratch" "$source_file"; then
    printf 'packaged corresponding source differs from repository source: %s\n' \
      "$relative_source" >&2
    exit 1
  fi
done < "$component_source_inventory"

for build_input in Directory.Build.props global.json; do
  extract_package_entry "lgpl-source/$build_input" "$packaged_entry_scratch"
  if ! cmp -s "$packaged_entry_scratch" "$repository_root/$build_input"; then
    printf 'packaged LGPL build input differs from repository: %s\n' "$build_input" >&2
    exit 1
  fi
done

package_metadata="$(unzip -p "$package_path" '*.nuspec')"
expected_authors="$(
  printf '%s' "$authors" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
)"
if ! grep -Fq "<id>DotNetJq.Library</id>" <<<"$package_metadata" ||
   ! grep -Fq "<version>$package_version</version>" <<<"$package_metadata"; then
  printf 'package metadata does not match DotNetJq.Library %s\n' "$package_version" >&2
  exit 1
fi
if ! grep -Fq "<authors>$expected_authors</authors>" <<<"$package_metadata"; then
  printf 'package metadata does not contain the exact explicit Authors value\n' >&2
  exit 1
fi
repository_metadata_count="$(
  awk '
    {
      line = $0
      while ((position = index(line, "<repository ")) != 0) {
        count++
        line = substr(line, position + length("<repository "))
      }
    }
    END { print count + 0 }
  ' <<<"$package_metadata"
)"
if [[ "$repository_metadata_count" -ne 1 ]] ||
   ! grep -Fq "<repository type=\"git\" url=\"$repository_url\"" <<<"$package_metadata"; then
  printf 'package metadata does not contain the exact explicit git RepositoryUrl\n' >&2
  exit 1
fi
if ! grep -Fq '<license type="file">LICENSES.md</license>' <<<"$package_metadata"; then
  printf 'package metadata does not declare mixed-license LICENSES.md\n' >&2
  exit 1
fi

if [[ "$package_metadata" == *'<dependency '* ]]; then
  printf 'single-package distribution unexpectedly declares an external package dependency\n' >&2
  printf '%s\n' "$package_metadata" >&2
  exit 1
fi

unexpected_generator_dll_entries="$(
  awk 'tolower($0) ~ /(^|\/)[^\/]*(gppg|gplex)[^\/]*\.dll$/ { print }' \
    "$package_entries"
)"
if [[ -n "$unexpected_generator_dll_entries" ]]; then
  printf 'package unexpectedly contains a GPPG/GPLEX assembly\n' >&2
  printf '%s\n' "$unexpected_generator_dll_entries" >&2
  exit 1
fi

if ! grep -Fq '<readme>README.md</readme>' <<<"$package_metadata"; then
  printf 'package metadata does not declare README.md as its readme\n' >&2
  exit 1
fi

unexpected_jq_entries="$(
  awk '
    (tolower($0) ~ /(^|\/)(jq|jq\.exe)$/ || tolower($0) ~ /\.jq$/) &&
    $0 != "COPYING" { print }
  ' "$package_entries"
)"
if [[ -n "$unexpected_jq_entries" ]]; then
  printf 'package unexpectedly contains an external jq file\n' >&2
  printf '%s\n' "$unexpected_jq_entries" >&2
  exit 1
fi

unexpected_native_entries="$(
  awk '
    tolower($0) ~ /(^|\/)runtimes\// ||
    tolower($0) ~ /\.(so|dylib)$/ ||
    tolower($0) ~ /(^|\/)[^\/]+\.exe$/ { print }
  ' "$package_entries"
)"
if [[ -n "$unexpected_native_entries" ]]; then
  printf 'package unexpectedly contains a native/runtime-specific or process artifact\n' >&2
  printf '%s\n' "$unexpected_native_entries" >&2
  exit 1
fi

unexpected_source_build_artifacts="$(
  awk '$0 ~ /^lgpl-source\/src\/DotNetJq\.GlibcCompat\/(bin|obj)\// { print }' \
    "$package_entries"
)"
if [[ -n "$unexpected_source_build_artifacts" ]]; then
  printf 'corresponding-source bundle unexpectedly contains build artifacts\n' >&2
  printf '%s\n' "$unexpected_source_build_artifacts" >&2
  exit 1
fi

main_assembly_strings="$(
  unzip -p "$package_path" 'lib/net10.0/DotNetJq.dll' | strings
)"
component_assembly_strings="$(
  unzip -p "$package_path" 'lib/net10.0/DotNetJq.GlibcCompat.dll' | strings
)"
source_parent="$(dirname -- "$repository_root")"
for managed_assembly in DotNetJq.dll DotNetJq.GlibcCompat.dll; do
  if [[ "$managed_assembly" == "DotNetJq.dll" ]]; then
    assembly_strings="$main_assembly_strings"
  else
    assembly_strings="$component_assembly_strings"
  fi
  for forbidden_source_path in "$repository_root/" "$source_parent/jq/"; do
    if [[ "$assembly_strings" == *"$forbidden_source_path"* ]]; then
      printf '%s unexpectedly embeds source path %s\n' \
        "$managed_assembly" "$forbidden_source_path" >&2
      exit 1
    fi
  done
done

if grep -Eq \
  'GlibcPowPositive|GlibcExp2Primary|GammaProduct|LgammaNegative|GlibcExpTable' \
  <<<"$main_assembly_strings"; then
  printf 'DotNetJq.dll unexpectedly embeds a GlibcCompat algorithm symbol\n' >&2
  exit 1
elif [[ $? -ne 1 ]]; then
  printf 'could not scan DotNetJq.dll symbols for GlibcCompat algorithms\n' >&2
  exit 1
fi

if ! grep -Fq 'GlibcCompatMath' <<<"$component_assembly_strings"; then
  printf 'component assembly does not contain the expected GlibcCompat implementation\n' >&2
  exit 1
fi

dotnet restore "$consumer_root/Consumer.csproj" \
  --source "$package_feed" \
  --packages "$nuget_cache" \
  -p:DotNetJqPackageVersion="$package_version" \
  -p:NuGetAudit=false \
  --verbosity quiet
dotnet build "$consumer_root/Consumer.csproj" \
  --configuration Release \
  --no-restore \
  -p:DotNetJqPackageVersion="$package_version" \
  --verbosity minimal

consumer_dll="$consumer_root/bin/Release/net10.0/Consumer.dll"
if [[ ! -f "$consumer_dll" ]]; then
  printf 'consumer assembly was not produced: %s\n' "$consumer_dll" >&2
  exit 1
fi

consumer_output="$consumer_root/bin/Release/net10.0"
consumer_deps="$consumer_output/Consumer.deps.json"
if [[ ! -f "$consumer_deps" ]]; then
  printf 'consumer dependency manifest was not produced: %s\n' "$consumer_deps" >&2
  exit 1
fi
if rg -i 'springcomp\.(gppg|gplex)' "$consumer_deps"; then
  printf 'consumer dependency graph unexpectedly includes GPPG/GPLEX\n' >&2
  exit 1
elif [[ $? -ne 1 ]]; then
  printf 'could not scan the consumer dependency graph\n' >&2
  exit 1
fi
unexpected_consumer_generator="$(find "$consumer_output" -maxdepth 1 -type f \
    \( -iname '*gppg*.dll' -o -iname '*gplex*.dll' \) -print -quit)"
if [[ -n "$unexpected_consumer_generator" ]]; then
  printf 'consumer output unexpectedly contains a GPPG/GPLEX assembly\n' >&2
  find "$consumer_output" -maxdepth 1 -type f \
    \( -iname '*gppg*.dll' -o -iname '*gplex*.dll' \) -print >&2
  exit 1
fi
consumer_main="$consumer_output/DotNetJq.dll"
consumer_component="$consumer_output/DotNetJq.GlibcCompat.dll"
for package_assembly in DotNetJq.dll DotNetJq.GlibcCompat.dll; do
  extract_package_entry "lib/net10.0/$package_assembly" "$packaged_entry_scratch"
  if ! cmp -s "$packaged_entry_scratch" "$consumer_output/$package_assembly"; then
    printf 'consumer did not receive packaged assembly unchanged: %s\n' \
      "$package_assembly" >&2
    exit 1
  fi
done
consumer_hash_before="$(sha256sum "$consumer_dll")"
consumer_hash_before="${consumer_hash_before%% *}"
main_hash_before="$(sha256sum "$consumer_main")"
main_hash_before="${main_hash_before%% *}"

snapshot_consumer_output_except_component() {
  local destination="$1"
  (
    cd -- "$consumer_output"
    LC_ALL=C find . -type f \
      ! -path './DotNetJq.GlibcCompat.dll' \
      -print |
      sed 's|^\./||' |
      LC_ALL=C sort |
      while IFS= read -r relative_file; do
        file_hash="$(sha256sum -- "$relative_file")"
        printf '%s  %s\n' "${file_hash%% *}" "$relative_file"
      done
  ) > "$destination"
}

consumer_output_inventory_before="$validation_root/consumer-output.before.sha256"
consumer_output_inventory_after="$validation_root/consumer-output.after.sha256"
snapshot_consumer_output_except_component "$consumer_output_inventory_before"

# The runtime process sees an empty source-parent tree, no usable system jq, no
# network, and only the already-built consumer/package artifacts under the
# validated temporary directory as writable state.
run_isolated_consumer() {
  local expected_component_version="${1:-}"
  set --
  if [[ -n "$expected_component_version" ]]; then
    set -- \
      --setenv DOTNETJQ_EXPECT_GLIBC_INFORMATIONAL_VERSION "$expected_component_version"
  fi

  bwrap \
    --die-with-parent \
    --unshare-net \
    --ro-bind / / \
    --dev-bind /dev /dev \
    --proc /proc \
    --tmpfs /tmp \
    --tmpfs "$masked_source_tree" \
    --bind "$validation_root" "$validation_root" \
    --ro-bind /dev/null /usr/bin/jq \
    --chdir "$consumer_root" \
    --clearenv \
    --setenv HOME "$sandbox_home" \
    --setenv TMPDIR "$sandbox_tmp" \
    --setenv DOTNET_CLI_HOME "$sandbox_home" \
    --setenv DOTNET_ROOT "$dotnet_root" \
    --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1 \
    --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
    --setenv DOTNETJQ_UPSTREAM /definitely/missing/dotnetjq-upstream \
    --setenv DOTNETJQ_ISOLATION_SENTINEL visible \
    --setenv PATH "$dotnet_root:/usr/bin:/bin" \
    "$@" \
    -- /bin/sh -eu -c '
      test ! -e "$1"
      test ! -s /usr/bin/jq
      exec "$2" "$3"
    ' package-isolation "$repository_root" "$dotnet_executable" "$consumer_dll"
}

# First validate the component binary actually shipped by the package.
run_isolated_consumer

# Rebuild the complete corresponding source from the package without network
# access, mark that rebuild, replace only the LGPL DLL, and validate that the
# unchanged consumer and DotNetJq.dll load and execute the modified build.
unzip -q "$package_path" 'lgpl-source/*' -d "$source_rebuild_root"
extracted_source_root="$source_rebuild_root/lgpl-source"
component_project_rel="src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj"
replacement_version="lgpl-source-rebuild"
bwrap \
  --die-with-parent \
  --unshare-net \
  --ro-bind / / \
  --dev-bind /dev /dev \
  --proc /proc \
  --tmpfs /tmp \
  --tmpfs "$masked_source_tree" \
  --bind "$validation_root" "$validation_root" \
  --chdir "$extracted_source_root" \
  --clearenv \
  --setenv HOME "$sandbox_home" \
  --setenv TMPDIR "$sandbox_tmp" \
  --setenv DOTNET_CLI_HOME "$sandbox_home" \
  --setenv DOTNET_ROOT "$dotnet_root" \
  --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1 \
  --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
  --setenv PATH "$dotnet_root:/usr/bin:/bin" \
  -- /bin/sh -eu -c '
    test ! -e "$1"
    "$2" restore "$3" \
      --configfile src/DotNetJq.GlibcCompat/NuGet.Offline.Config \
      -p:NuGetAudit=false \
      -p:IsAotCompatible=false \
      -p:IsTrimmable=false \
      -p:EnableAotAnalyzer=false \
      -p:EnableTrimAnalyzer=false \
      --verbosity quiet
    "$2" build "$3" \
      --configuration Release \
      --no-restore \
      -p:IsAotCompatible=false \
      -p:IsTrimmable=false \
      -p:EnableAotAnalyzer=false \
      -p:EnableTrimAnalyzer=false \
      -p:InformationalVersion="$4" \
      --verbosity minimal
  ' lgpl-source-build "$repository_root" "$dotnet_executable" \
    "$component_project_rel" "$replacement_version"

rebuilt_component="$extracted_source_root/src/DotNetJq.GlibcCompat/bin/Release/net10.0/DotNetJq.GlibcCompat.dll"
if [[ ! -f "$rebuilt_component" ]]; then
  printf 'packaged corresponding source did not produce the component assembly\n' >&2
  exit 1
fi
rebuilt_component_hash="$(sha256sum "$rebuilt_component")"
rebuilt_component_hash="${rebuilt_component_hash%% *}"

cp -- "$rebuilt_component" \
  "$consumer_component"
if ! cmp -s "$rebuilt_component" "$consumer_component"; then
  printf 'rebuilt LGPL component replacement was not copied exactly\n' >&2
  exit 1
fi
consumer_hash_after="$(sha256sum "$consumer_dll")"
consumer_hash_after="${consumer_hash_after%% *}"
main_hash_after="$(sha256sum "$consumer_main")"
main_hash_after="${main_hash_after%% *}"
snapshot_consumer_output_except_component "$consumer_output_inventory_after"
if [[ "$consumer_hash_after" != "$consumer_hash_before" ||
      "$main_hash_after" != "$main_hash_before" ]]; then
  printf 'component replacement unexpectedly changed consumer or DotNetJq.dll\n' >&2
  exit 1
fi
if ! cmp -s "$consumer_output_inventory_before" "$consumer_output_inventory_after"; then
  printf 'component replacement changed consumer output outside the replaceable DLL\n' >&2
  diff -u "$consumer_output_inventory_before" "$consumer_output_inventory_after" >&2 || true
  exit 1
fi
consumer_output_inventory_hash="$(sha256sum "$consumer_output_inventory_after")"
consumer_output_inventory_hash="${consumer_output_inventory_hash%% *}"
run_isolated_consumer "$replacement_version"

printf 'package SHA-256: %s\n' "$package_hash"
printf 'packaged DotNetJq.dll SHA-256: %s\n' "$packaged_main_hash"
printf 'packaged DotNetJq.GlibcCompat.dll SHA-256: %s\n' "$packaged_component_hash"
printf 'rebuilt DotNetJq.GlibcCompat.dll SHA-256: %s\n' "$rebuilt_component_hash"
printf 'unchanged consumer SHA-256: %s\n' "$consumer_hash_after"
printf 'unchanged DotNetJq.dll SHA-256: %s\n' "$main_hash_after"
printf 'unchanged consumer-output inventory SHA-256: %s\n' "$consumer_output_inventory_hash"
printf 'isolated package verification passed: %s\n' "$package_path"
