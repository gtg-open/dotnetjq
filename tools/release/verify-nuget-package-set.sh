#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-nuget-package-set.sh --directory PATH --package-id ID --version VERSION' \
    '       --authors TEXT --repository-url URL [options]' \
    '' \
    'Options:' \
    '  --rid RID       validate only one RID package (the pointer must still map every RID)' \
    '  --project PATH  CLI project used to resolve each NativeAOT runtime pack' \
    '  --native-archives PATH  byte-bind each RID entry point to its verified archive' \
    '  --library-package-id ID  managed library package required for a complete set' \
    '' \
    'Without --rid, validates the pointer and every configured RID package.'
}

directory=
package_id=
version=
requested_rid=
native_archives=
project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
library_package_id=DotNetJq.Library
authors=
repository_url=
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; requested_rid=$2; shift 2 ;;
    --native-archives) [ "$#" -ge 2 ] || release_die '--native-archives requires a value'; native_archives=$2; shift 2 ;;
    --project) [ "$#" -ge 2 ] || release_die '--project requires a value'; project=$2; shift 2 ;;
    --library-package-id) [ "$#" -ge 2 ] || release_die '--library-package-id requires a value'; library_package_id=$2; shift 2 ;;
    --authors) [ "$#" -ge 2 ] || release_die '--authors requires a value'; authors=$2; shift 2 ;;
    --repository-url) [ "$#" -ge 2 ] || release_die '--repository-url requires a value'; repository_url=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -d "$directory" ] || release_die "NuGet directory does not exist: ${directory-}"
release_validate_package_id "$package_id"
release_validate_package_id "$library_package_id"
release_validate_version "$version"
release_validate_single_line authors "$authors"
release_validate_single_line repository-url "$repository_url"
case $repository_url in
  https://github.com/*)
    repository_name=${repository_url#https://github.com/}
    release_validate_repository "$repository_name"
    ;;
  *) release_die "repository-url must be an explicit https://github.com/owner/name URL: $repository_url" ;;
esac
[ -z "$requested_rid" ] || release_target_field "$requested_rid" 2 >/dev/null
[ -f "$project" ] || release_die "CLI project does not exist: $project"
[ -z "$native_archives" ] || [ -d "$native_archives" ] ||
  release_die "native archive directory does not exist: $native_archives"
release_require_command awk
release_require_command unzip
release_require_command python3
python3 "$release_script_dir/verify-packaging-text-inputs.py"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-nuget-verify)
identity_baseline="$work_dir/package-identity.baseline"
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-nuget-verify.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
configured_targets="$work_dir/release-targets.txt"
release_each_target >"$configured_targets"

optional_grep() {
  local status
  if grep "$@"; then
    return 0
  else
    status=$?
    [ "$status" -eq 1 ] && return 0
    return "$status"
  fi
}

package_xml() {
  local package=$1
  local pattern=$2
  local matches
  local package_name
  local package_entries
  local match_count
  package_name=$(basename -- "$package") || return
  package_entries="$work_dir/$package_name.xml-entries"
  unzip -Z1 "$package" >"$package_entries" ||
    release_die "could not enumerate package entries: $package_name"
  matches=$(optional_grep -E "$pattern" "$package_entries") ||
    release_die "could not scan package entries in $package_name"
  match_count=$(printf '%s\n' "$matches" | sed '/^$/d' | wc -l | tr -d ' ')
  [ "$match_count" -eq 1 ] ||
    release_die "expected one $pattern entry in $package_name"
  unzip -p "$package" "$matches" | tr -d '\r\n'
}

assert_xml_value() {
  local xml=$1
  local element=$2
  local value=$3
  case $xml in
    *"<$element>$value</$element>"*) ;;
    *) release_die "missing <$element>$value</$element> package metadata" ;;
  esac
}

xml_escape_text() {
  printf '%s' "$1" | sed \
    -e 's/&/\&amp;/g' \
    -e 's/</\&lt;/g' \
    -e 's/>/\&gt;/g'
}

count_literal_occurrences() {
  local needle=$1
  awk -v needle="$needle" '
    {
      line = $0
      while ((position = index(line, needle)) != 0) {
        count++
        line = substr(line, position + length(needle))
      }
    }
    END { print count + 0 }
  '
}

verify_package_identity() {
  local nuspec=$1
  local package_name=$2
  local expected_authors
  local repository_count
  local repository_metadata
  local actual_repository_url
  local identity_candidate
  expected_authors=$(xml_escape_text "$authors")
  case $nuspec in
    *"<authors>$expected_authors</authors>"*) ;;
    *) release_die "$package_name does not declare the exact explicit Authors value" ;;
  esac

  repository_count=$(count_literal_occurrences '<repository ' <<<"$nuspec")
  [ "$repository_count" -eq 1 ] ||
    release_die "$package_name must contain exactly one NuGet repository element"
  repository_metadata=$(printf '%s\n' "$nuspec" | sed -n \
    's|.*\(<repository type="git" url="[^"]*"\( commit="[0-9a-f][0-9a-f]*"\)\{0,1\} />\).*|\1|p')
  [ -n "$repository_metadata" ] ||
    release_die "$package_name repository metadata must have exact git type, URL, and optional hexadecimal commit shape"
  actual_repository_url=$(printf '%s\n' "$repository_metadata" | sed -n \
    's|^<repository type="git" url="\([^"]*\)".*|\1|p')
  [ "$actual_repository_url" = "$repository_url" ] ||
    release_die "$package_name RepositoryUrl differs from the explicit release identity"

  identity_candidate="$work_dir/$package_name.identity"
  printf '%s\n%s\n' "$expected_authors" "$repository_metadata" >"$identity_candidate"
  if [ ! -e "$identity_baseline" ]; then
    cp -- "$identity_candidate" "$identity_baseline"
  else
    cmp -s -- "$identity_baseline" "$identity_candidate" ||
      release_die "$package_name Authors/repository metadata differs from the other NuGet packages"
  fi
}

verify_tool_nuspec() {
  local nuspec=$1
  local package_name=$2
  case $nuspec in
    *'<license type="file">LICENSES.md</license>'*) ;;
    *) release_die "$package_name does not declare LICENSES.md as its file license" ;;
  esac
  case $nuspec in
    *'<readme>README.md</readme>'*) ;;
    *) release_die "$package_name does not declare README.md as its readme" ;;
  esac
  case $nuspec in
    *'<dependency '*) release_die "$package_name unexpectedly declares an external package dependency" ;;
  esac
}

write_common_package_inventory() {
  local package_id_value=$1
  local output=$2
  local required
  local source_file
  local license_inventory
  printf '%s\n' \
    '_rels/.rels' \
    "$package_id_value.nuspec" \
    '[Content_Types].xml' \
    'package/services/metadata/core-properties/nuget.psmdcp' \
    'README.md' \
    >"$output"
  while IFS= read -r required; do
    case $required in ''|'#'*) continue ;; esac
    if [ -d "$release_repository_root/$required" ]; then
      license_inventory="$work_dir/common-$(printf '%s' "$required" | tr '/ ' '__').inventory"
      find "$release_repository_root/$required" -type f | LC_ALL=C sort >"$license_inventory" ||
        release_die "could not enumerate repository license directory: $required"
      while IFS= read -r source_file; do
        printf '%s\n' "${source_file#"$release_repository_root/"}" >>"$output"
      done <"$license_inventory"
    else
      printf '%s\n' "$required" >>"$output"
    fi
  done <"$release_license_paths_file"
  LC_ALL=C sort -u -o "$output" "$output"
}

verify_exact_package_inventory() {
  local package=$1
  local expected=$2
  local package_name
  local raw_entries
  local actual
  package_name=$(basename -- "$package")
  python3 "$release_script_dir/verify-nupkg-archive.py" "$package"
  raw_entries="$work_dir/$package_name.all-entries"
  actual="$work_dir/$package_name.all-files"
  unzip -Z1 "$package" >"$raw_entries" ||
    release_die "could not enumerate package entries: $package_name"
  while IFS= read -r entry; do
    case $entry in
      */)
        release_die "$package_name contains an unexpected explicit directory entry: $entry"
        ;;
      ''|/*|*\\*|../*|*/../*|*/..|./*|*/./*|*/.|*//*)
        release_die "$package_name contains an unsafe or non-canonical entry: $entry"
        ;;
    esac
  done <"$raw_entries"
  local duplicate_entries="$work_dir/$package_name.duplicate-entries"
  LC_ALL=C sort "$raw_entries" | uniq -d >"$duplicate_entries"
  if [ -s "$duplicate_entries" ]; then
    release_die "$package_name contains duplicate entries"
  fi
  LC_ALL=C sort -u "$raw_entries" >"$actual"
  LC_ALL=C sort -u -o "$expected" "$expected"
  cmp -s -- "$expected" "$actual" ||
    release_die "$package_name file inventory contains a missing or unexpected payload"
}

verify_package_license_payload() {
  local package=$1
  local package_name
  local source_inventory
  local extracted
  package_name=$(basename -- "$package")
  while IFS= read -r required; do
    case $required in ''|'#'*) continue ;; esac
    if [ -d "$release_repository_root/$required" ]; then
      source_inventory="$work_dir/$package_name.$(printf '%s' "$required" | tr '/ ' '__').license-files"
      find "$release_repository_root/$required" -type f | LC_ALL=C sort >"$source_inventory" ||
        release_die "could not enumerate repository license directory: $required"
      while IFS= read -r source_file; do
        relative=${source_file#"$release_repository_root/"}
        unzip -Z1 "$package" | grep -Fx -- "$relative" >/dev/null ||
          release_die "$package_name is missing $relative"
        extracted="$work_dir/$package_name.license-extracted"
        unzip -p "$package" "$relative" >"$extracted" ||
          release_die "$package_name could not extract $relative"
        cmp -s -- "$source_file" "$extracted" ||
          release_die "$package_name has a modified $relative"
      done <"$source_inventory"
    else
      unzip -Z1 "$package" | grep -Fx -- "$required" >/dev/null ||
        release_die "$package_name is missing $required"
      extracted="$work_dir/$package_name.license-extracted"
      unzip -p "$package" "$required" >"$extracted" ||
        release_die "$package_name could not extract $required"
      cmp -s -- "$release_repository_root/$required" "$extracted" ||
        release_die "$package_name has a modified $required"
    fi
  done <"$release_license_paths_file"
}

verify_package_corresponding_source() {
  local package=$1
  local package_name
  package_name=$(basename -- "$package")
  local package_entries="$work_dir/$package_name.entries"
  local expected_entries="$work_dir/$package_name.aot-source.expected"
  local actual_entries="$work_dir/$package_name.aot-source.actual"
  local relative
  local package_path
  local repository_source_files="$work_dir/$package_name.repository-source-files"
  local extracted_source="$work_dir/$package_name.source-extracted"
  unzip -Z1 "$package" >"$package_entries"
  : >"$expected_entries"
  : >"$repository_source_files"

  while IFS= read -r required; do
    case $required in ''|'#'*) continue ;; esac
    release_find_source_files "$required" | LC_ALL=C sort >>"$repository_source_files" ||
      release_die "could not enumerate corresponding-source path: $required"
  done <"$release_aot_source_paths_file"
  LC_ALL=C sort -u -o "$repository_source_files" "$repository_source_files"
  while IFS= read -r source_file; do
    relative=${source_file#"$release_repository_root/"}
    package_path="aot-source/$relative"
    printf '%s\n' "$package_path" >>"$expected_entries"
    grep -Fx -- "$package_path" "$package_entries" >/dev/null ||
      release_die "$package_name is missing corresponding source: $package_path"
    unzip -p "$package" "$package_path" >"$extracted_source" ||
      release_die "$package_name could not extract corresponding source: $package_path"
    cmp -s -- "$source_file" "$extracted_source" ||
      release_die "$package_name has modified corresponding source: $package_path"
  done <"$repository_source_files"

  sed -n '/^aot-source\/.*[^\/]$/p' "$package_entries" | LC_ALL=C sort -u >"$actual_entries"
  LC_ALL=C sort -u -o "$expected_entries" "$expected_entries"
  cmp -s -- "$expected_entries" "$actual_entries" ||
    release_die "$package_name aot-source inventory does not exactly match packaging/aot-source-paths.txt"

  for key_path in \
    aot-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.cs \
    aot-source/src/DotNetJq/Grammar/lexer.l \
    aot-source/src/DotNetJq/Grammar/parser.y \
    aot-source/src/DotNetJq/Generated/Lexer/lexer.c.cs \
    aot-source/src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs \
    aot-source/src/DotNetJq/Resources/builtin.jq \
    aot-source/porting/AOT_RELINKING.md \
    aot-source/tools/aot-compliance/create-source-archive.sh \
    aot-source/tools/aot-compliance/rebuild-aot.sh \
    aot-source/tools/aot-compliance/verify-relink.sh; do
    grep -Fx -- "$key_path" "$expected_entries" >/dev/null ||
      release_die "corresponding-source policy does not cover key rebuild input: $key_path"
  done
}

verify_library_package() {
  local package="$directory/$library_package_id.$version.nupkg"
  local package_name
  local nuspec
  local entries
  local component_source="$release_repository_root/src/DotNetJq.GlibcCompat"
  local expected_lib_entries
  local actual_lib_entries
  local expected_source_entries
  local actual_source_entries
  local component_source_files
  local relative_source
  local source_file
  local package_source_path
  local extracted_library_entry="$work_dir/library-package-entry"
  local component_documentation
  local documentation_fragment
  local documentation_member_count
  package_name=$(basename -- "$package")
  [ -f "$package" ] || release_die "managed library package is missing: $package_name"

  nuspec=$(package_xml "$package" '\.nuspec$')
  assert_xml_value "$nuspec" id "$library_package_id"
  assert_xml_value "$nuspec" version "$version"
  verify_package_identity "$nuspec" "$package_name"
  case $nuspec in
    *'<license type="file">LICENSES.md</license>'*) ;;
    *) release_die "$package_name does not declare LICENSES.md as its file license" ;;
  esac
  case $nuspec in
    *'<readme>README.md</readme>'*) ;;
    *) release_die "$package_name does not declare README.md as its readme" ;;
  esac
  case $nuspec in
    *'<dependency '*) release_die "$package_name unexpectedly declares an external package dependency" ;;
  esac
  case $nuspec in
    *'<packageType name="DotnetTool"'*|*'<packageType name="DotnetToolRidPackage"'*)
      release_die "$package_name must not declare a dotnet-tool package type"
      ;;
  esac

  verify_package_license_payload "$package"
  unzip -Z1 "$package" | grep -Fx -- 'README.md' >/dev/null ||
    release_die "$package_name is missing README.md"
  unzip -p "$package" README.md >"$extracted_library_entry" ||
    release_die "$package_name could not extract README.md"
  cmp -s -- "$release_repository_root/README.md" "$extracted_library_entry" ||
    release_die "$package_name has a modified README.md"
  entries="$work_dir/$package_name.entries"
  unzip -Z1 "$package" >"$entries"

  expected_lib_entries="$work_dir/$package_name.lib.expected"
  actual_lib_entries="$work_dir/$package_name.lib.actual"
  printf '%s\n' \
    'lib/net10.0/DotNetJq.GlibcCompat.dll' \
    'lib/net10.0/DotNetJq.GlibcCompat.xml' \
    'lib/net10.0/DotNetJq.dll' \
    'lib/net10.0/DotNetJq.xml' \
    | LC_ALL=C sort >"$expected_lib_entries"
  sed -n '/^lib\/net10\.0\/.*[^\/]$/p' "$entries" | LC_ALL=C sort -u >"$actual_lib_entries"
  cmp -s -- "$expected_lib_entries" "$actual_lib_entries" ||
    release_die "$package_name lib/net10.0 inventory must contain only both managed assemblies and both XML documentation files"

  component_documentation=$(unzip -p "$package" 'lib/net10.0/DotNetJq.GlibcCompat.xml') ||
    release_die "$package_name cannot extract DotNetJq.GlibcCompat.xml"
  documentation_member_count=$(count_literal_occurrences '<member name=' \
    <<<"$component_documentation")
  [ "$documentation_member_count" -eq 5 ] ||
    release_die "$package_name DotNetJq.GlibcCompat.xml must document exactly the public type and four ABI methods"
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
    printf '%s\n' "$component_documentation" | grep -Fq -- "$documentation_fragment" ||
      release_die "$package_name DotNetJq.GlibcCompat.xml is missing its exact ABI semantics: $documentation_fragment"
  done

  expected_source_entries="$work_dir/$package_name.lgpl-source.expected"
  actual_source_entries="$work_dir/$package_name.lgpl-source.actual"
  component_source_files="$work_dir/$package_name.lgpl-source.repository-files"
  release_find_source_files 'src/DotNetJq.GlibcCompat' | LC_ALL=C sort >"$component_source_files"
  printf '%s\n' \
    'lgpl-source/Directory.Build.props' \
    'lgpl-source/global.json' \
    >"$expected_source_entries"
  while IFS= read -r source_file; do
    relative_source=${source_file#"$component_source/"}
    printf '%s\n' "lgpl-source/src/DotNetJq.GlibcCompat/$relative_source" >>"$expected_source_entries"
  done <"$component_source_files"
  LC_ALL=C sort -u -o "$expected_source_entries" "$expected_source_entries"
  sed -n '/^lgpl-source\/.*[^\/]$/p' "$entries" | LC_ALL=C sort -u >"$actual_source_entries"
  cmp -s -- "$expected_source_entries" "$actual_source_entries" ||
    release_die "$package_name lgpl-source inventory does not exactly match the repository LGPL source boundary"

  while IFS= read -r source_file; do
    relative_source=${source_file#"$component_source/"}
    package_source_path="lgpl-source/src/DotNetJq.GlibcCompat/$relative_source"
    unzip -p "$package" "$package_source_path" >"$extracted_library_entry" ||
      release_die "$package_name could not extract LGPL corresponding source: $package_source_path"
    cmp -s -- "$source_file" "$extracted_library_entry" ||
      release_die "$package_name has modified LGPL corresponding source: $package_source_path"
  done <"$component_source_files"
  for relative_source in Directory.Build.props global.json; do
    unzip -p "$package" "lgpl-source/$relative_source" >"$extracted_library_entry" ||
      release_die "$package_name could not extract LGPL build input: lgpl-source/$relative_source"
    cmp -s -- "$release_repository_root/$relative_source" "$extracted_library_entry" ||
      release_die "$package_name has modified LGPL build input: lgpl-source/$relative_source"
  done

  if grep -E '^(runtimes|tools)/|\.(deps\.json|runtimeconfig\.json|so|dylib|a|lib|o|obj|exe)$' "$entries" >/dev/null 2>&1; then
    release_die "$package_name contains a runtime-specific or native payload"
  elif [ "$?" -ne 1 ]; then
    release_die "$package_name entries could not be scanned for runtime-specific payloads"
  fi

  # Managed DLLs are expected only at the two exact library paths already
  # compared above. Reject a DLL injected anywhere else (for example under a
  # native/ or content/ directory), which the lib/net10.0 inventory alone
  # would not detect.
  unexpected_dlls=$(awk '
    tolower($0) ~ /\.dll$/ &&
    $0 !~ /^lib\/net10\.0\/(DotNetJq|DotNetJq\.GlibcCompat)\.dll$/ { print }
  ' "$entries")
  [ -z "$unexpected_dlls" ] ||
    release_die "$package_name contains an unexpected DLL payload: $unexpected_dlls"

  exact_inventory="$work_dir/$package_name.exact.expected"
  write_common_package_inventory "$library_package_id" "$exact_inventory"
  cat "$expected_lib_entries" "$expected_source_entries" >>"$exact_inventory"
  verify_exact_package_inventory "$package" "$exact_inventory"
}

if [ -z "$requested_rid" ]; then
  expected_packages="$work_dir/expected-packages.txt"
  actual_packages="$work_dir/actual-packages.txt"
  printf '%s\n' \
    "$library_package_id.$version.nupkg" \
    "$package_id.$version.nupkg" \
    >"$expected_packages"
  while IFS= read -r rid; do
    printf '%s\n' "$package_id.$rid.$version.nupkg" >>"$expected_packages"
  done <"$configured_targets"
  LC_ALL=C sort -u -o "$expected_packages" "$expected_packages"
  (
    cd -- "$directory"
    find . -maxdepth 1 -type f -name '*.nupkg' -print |
      sed 's|^\./||' |
      LC_ALL=C sort -u
  ) >"$actual_packages"
  cmp -s -- "$expected_packages" "$actual_packages" ||
    release_die 'NuGet package inventory does not exactly match the managed library, pointer, and eight RID packages'
  verify_library_package
fi

pointer="$directory/$package_id.$version.nupkg"
[ -f "$pointer" ] || release_die "pointer package is missing: $(basename -- "$pointer")"
pointer_entries="$work_dir/pointer-package-entries.txt"
unzip -Z1 "$pointer" >"$pointer_entries" ||
  release_die 'could not enumerate pointer package entries'
pointer_nuspec=$(package_xml "$pointer" '\.nuspec$')
assert_xml_value "$pointer_nuspec" id "$package_id"
assert_xml_value "$pointer_nuspec" version "$version"
verify_package_identity "$pointer_nuspec" "$(basename -- "$pointer")"
verify_tool_nuspec "$pointer_nuspec" "$(basename -- "$pointer")"
case $pointer_nuspec in
  *'<packageType name="DotnetTool"'*) ;;
  *) release_die 'pointer package type is not DotnetTool' ;;
esac

pointer_settings=$(package_xml "$pointer" '^tools/[^/]+/any/DotnetToolSettings\.xml$')
pointer_settings_path=$(grep -E '^tools/[^/]+/any/DotnetToolSettings\.xml$' "$pointer_entries")
case $pointer_settings in
  *'<Command Name="dotnetjq"'*) ;;
  *) release_die 'pointer package does not expose the dotnetjq command' ;;
esac
pointer_any_payload=$(release_pointer_any_payload <"$pointer_entries")
[ "$pointer_any_payload" = "$pointer_settings_path" ] ||
  release_die 'pointer package contains an unexpected tools/<tfm>/any payload'
case $pointer_settings in
  *'RuntimeIdentifier="any"'*) release_die 'pointer package unexpectedly contains an any fallback' ;;
esac

expected_rid_count=$(wc -l <"$configured_targets" | tr -d ' ')
actual_rid_count=$(printf '%s' "$pointer_settings" | grep -o 'RuntimeIdentifierPackage ' | wc -l | tr -d ' ')
[ "$actual_rid_count" -eq "$expected_rid_count" ] ||
  release_die "pointer maps $actual_rid_count RIDs; expected $expected_rid_count"

verify_package_license_payload "$pointer"
verify_package_corresponding_source "$pointer"
pointer_exact_inventory="$work_dir/$(basename -- "$pointer").exact.expected"
write_common_package_inventory "$package_id" "$pointer_exact_inventory"
cat "$work_dir/$(basename -- "$pointer").aot-source.expected" >>"$pointer_exact_inventory"
printf '%s\n' "$pointer_settings_path" >>"$pointer_exact_inventory"
verify_exact_package_inventory "$pointer" "$pointer_exact_inventory"
runtime_license_paths_file="$release_repository_root/packaging/nativeaot-runtime-license-paths.txt"
while IFS= read -r runtime_license; do
  case $runtime_license in ''|'#'*) continue ;; esac
  if grep -Fx -- "$runtime_license" "$pointer_entries" >/dev/null 2>&1; then
    release_die "pointer package must not carry RID-specific NativeAOT runtime license: $runtime_license"
  elif [ "$?" -ne 1 ]; then
    release_die 'could not scan pointer package entries for NativeAOT runtime licenses'
  fi
done <"$runtime_license_paths_file"

rid_verification_targets="$work_dir/rids-to-verify.txt"
if [ -n "$requested_rid" ]; then
  printf '%s\n' "$requested_rid" >"$rid_verification_targets"
else
  cp -- "$configured_targets" "$rid_verification_targets"
fi
verified_rid_count=0
while IFS= read -r rid; do
  rid_id="$package_id.$rid"
  rid_package="$directory/$rid_id.$version.nupkg"
  [ -f "$rid_package" ] || release_die "RID package is missing: $(basename -- "$rid_package")"
  rid_package_entries="$work_dir/$rid.package-entries.txt"
  unzip -Z1 "$rid_package" >"$rid_package_entries" ||
    release_die "could not enumerate $rid_id package entries"
  case $pointer_settings in
    *"<RuntimeIdentifierPackage RuntimeIdentifier=\"$rid\" Id=\"$rid_id\""*) ;;
    *) release_die "pointer package does not map $rid to $rid_id" ;;
  esac

  rid_nuspec=$(package_xml "$rid_package" '\.nuspec$')
  assert_xml_value "$rid_nuspec" id "$rid_id"
  assert_xml_value "$rid_nuspec" version "$version"
  verify_package_identity "$rid_nuspec" "$(basename -- "$rid_package")"
  verify_tool_nuspec "$rid_nuspec" "$(basename -- "$rid_package")"
  case $rid_nuspec in
    *'<packageType name="DotnetToolRidPackage"'*) ;;
    *) release_die "$rid_id package type is not DotnetToolRidPackage" ;;
  esac

  rid_settings_path="tools/any/$rid/DotnetToolSettings.xml"
  grep -Fx -- "$rid_settings_path" "$rid_package_entries" >/dev/null ||
    release_die "$rid_id is missing $rid_settings_path"
  rid_settings=$(unzip -p "$rid_package" "$rid_settings_path" | tr -d '\r\n')
  case $rid_settings in
    *'<Command Name="dotnetjq"'*'Runner="executable"'*) ;;
    *) release_die "$rid_id does not define dotnetjq as a native executable" ;;
  esac
  entry_point=$(printf '%s' "$rid_settings" | sed -n 's/.*EntryPoint="\([^"]*\)".*/\1/p')
  [ -n "$entry_point" ] || release_die "$rid_id has no native entry point"
  grep -Fx -- "tools/any/$rid/$entry_point" "$rid_package_entries" >/dev/null ||
    release_die "$rid_id is missing its native entry point: $entry_point"

  rid_tool_entries="$work_dir/$rid.tools.actual"
  expected_rid_tool_entries="$work_dir/$rid.tools.expected"
  sed -n "/^tools\/any\/$rid\/.*[^\/]$/p" "$rid_package_entries" |
    LC_ALL=C sort -u >"$rid_tool_entries"
  printf '%s\n' \
    "tools/any/$rid/$entry_point" \
    "$rid_settings_path" \
    "tools/any/$rid/LICENSE.TXT" \
    "tools/any/$rid/THIRD-PARTY-NOTICES.TXT" \
    | LC_ALL=C sort -u >"$expected_rid_tool_entries"
  cmp -s -- "$expected_rid_tool_entries" "$rid_tool_entries" ||
    release_die "$rid_id tools/any/$rid inventory contains a sidecar or unexpected payload"

  extracted_entry="$work_dir/$rid"
  unzip -p "$rid_package" "tools/any/$rid/$entry_point" >"$extracted_entry"
  release_verify_binary_format "$extracted_entry" "$rid"
  if grep -E '^tools/any/[^/]+/.*(\.dll|\.deps\.json|\.runtimeconfig\.json)$' \
      "$rid_package_entries" >/dev/null 2>&1; then
    release_die "$rid_id contains a managed runtime payload"
  elif [ "$?" -ne 1 ]; then
    release_die "could not scan $rid_id package entries for managed runtime payloads"
  fi
  verify_package_license_payload "$rid_package"
  verify_package_corresponding_source "$rid_package"
  resolved_runtime_licenses="$work_dir/$rid.nativeaot-runtime-licenses"
  "$release_script_dir/resolve-nativeaot-runtime-licenses.sh" \
    --project "$project" --rid "$rid" --output "$resolved_runtime_licenses"
  while IFS= read -r runtime_license_source; do
    runtime_license_name=$(printf '%s\n' "$runtime_license_source" | sed 's#\\#/#g; s#.*/##')
    runtime_license_package_path="tools/any/$rid/$runtime_license_name"
    grep -Fx -- "$runtime_license_package_path" "$rid_package_entries" >/dev/null ||
      release_die "$rid_id is missing resolved NativeAOT runtime license: $runtime_license_package_path"
    extracted_runtime_license="$work_dir/$rid.runtime-license"
    unzip -p "$rid_package" "$runtime_license_package_path" >"$extracted_runtime_license" ||
      release_die "$rid_id could not extract NativeAOT runtime license: $runtime_license_name"
    cmp -s -- "$runtime_license_source" "$extracted_runtime_license" ||
      release_die "$rid_id NativeAOT runtime license differs from the resolved pack: $runtime_license_name"
  done <"$resolved_runtime_licenses"

  rid_exact_inventory="$work_dir/$(basename -- "$rid_package").exact.expected"
  write_common_package_inventory "$rid_id" "$rid_exact_inventory"
  cat "$work_dir/$(basename -- "$rid_package").aot-source.expected" \
      "$expected_rid_tool_entries" >>"$rid_exact_inventory"
  verify_exact_package_inventory "$rid_package" "$rid_exact_inventory"

  if [ -n "$native_archives" ]; then
    archive="$native_archives/$(release_archive_name "$version" "$rid")"
    "$release_script_dir/verify-native-archive.sh" \
      --archive "$archive" --rid "$rid" --version "$version" --project "$project"
    archive_entry="$work_dir/$rid.archive-entry"
    case $(release_target_field "$rid" 2) in
      zip) unzip -p "$archive" dotnetjq.exe >"$archive_entry" ;;
      tar.gz) tar -xOf "$archive" ./dotnetjq >"$archive_entry" ;;
    esac
    cmp -s -- "$extracted_entry" "$archive_entry" ||
      release_die "$rid_id entry point is not byte-identical to its verified NativeAOT archive"
  fi
  verified_rid_count=$((verified_rid_count + 1))
done <"$rid_verification_targets"

if [ -z "$requested_rid" ]; then
  release_note "verified $library_package_id plus pointer and $verified_rid_count RID package(s) for $package_id $version; pointer maps all $expected_rid_count"
else
  release_note "verified pointer plus $verified_rid_count RID package(s) for $package_id $version; pointer maps all $expected_rid_count"
fi
