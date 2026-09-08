#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-package-manager-metadata.sh --version VERSION --repository OWNER/NAME' \
    '       --publisher TEXT --winget-package-id ID --artifacts PATH [--tag TAG]' \
    '' \
    'Regenerates and byte-compares the exact Homebrew/WinGet metadata set,' \
    'checks the Homebrew Ruby syntax, and parses/validates the WinGet YAML.'
}

version=
repository=
publisher=
winget_package_id=
artifacts=
tag=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --repository) [ "$#" -ge 2 ] || release_die '--repository requires a value'; repository=$2; shift 2 ;;
    --publisher) [ "$#" -ge 2 ] || release_die '--publisher requires a value'; publisher=$2; shift 2 ;;
    --winget-package-id) [ "$#" -ge 2 ] || release_die '--winget-package-id requires a value'; winget_package_id=$2; shift 2 ;;
    --artifacts) [ "$#" -ge 2 ] || release_die '--artifacts requires a value'; artifacts=$2; shift 2 ;;
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_repository "$repository"
release_validate_package_id "$winget_package_id"
release_validate_single_line publisher "$publisher"
[ -d "$artifacts" ] || release_die "artifact directory does not exist: ${artifacts-}"
[ -n "$tag" ] || tag="v$version"
case $tag in *[!0-9A-Za-z._-]*) release_die "invalid release tag: $tag" ;; esac
for command in cmp find grep ruby sort; do
  release_require_command "$command"
done

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-package-metadata)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-package-metadata.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

expected_inventory="$work_dir/metadata.expected"
actual_inventory="$work_dir/metadata.actual"
printf '%s\n' \
  dotnetjq.rb \
  "$winget_package_id.yaml" \
  "$winget_package_id.installer.yaml" \
  "$winget_package_id.locale.en-US.yaml" |
  LC_ALL=C sort >"$expected_inventory"
(
  cd -- "$artifacts"
  find . -maxdepth 1 -type f \( -name '*.rb' -o -name '*.yaml' \) -print |
    sed 's|^\./||' |
    LC_ALL=C sort
) >"$actual_inventory"
cmp -s -- "$expected_inventory" "$actual_inventory" ||
  release_die 'package-manager metadata inventory must contain exactly one formula and three WinGet manifests'

metadata_symlink=$(find "$artifacts" -maxdepth 1 -type l -print -quit)
if [ -n "$metadata_symlink" ]; then
  release_die 'release artifact directory must not contain symbolic links'
fi

regenerated="$work_dir/regenerated"
mkdir -p -- "$regenerated"
"$release_script_dir/generate-homebrew-formula.sh" \
  --version "$version" --repository "$repository" --tag "$tag" \
  --artifacts "$artifacts" --output "$regenerated/dotnetjq.rb"
"$release_script_dir/generate-winget-manifests.sh" \
  --version "$version" --repository "$repository" --tag "$tag" \
  --publisher "$publisher" --package-id "$winget_package_id" \
  --artifacts "$artifacts" --output-directory "$regenerated"

while IFS= read -r name; do
  [ -s "$artifacts/$name" ] || release_die "package-manager metadata is missing or empty: $name"
  cmp -s -- "$regenerated/$name" "$artifacts/$name" ||
    release_die "package-manager metadata does not match its release inputs: $name"
  if grep -E '__[A-Z0-9_]+__' "$artifacts/$name" >/dev/null 2>&1; then
    release_die "package-manager metadata contains an unresolved template token: $name"
  elif [ "$?" -ne 1 ]; then
    release_die "could not scan package-manager metadata for unresolved template tokens: $name"
  fi
done <"$expected_inventory"

ruby -c "$artifacts/dotnetjq.rb" >/dev/null
ruby "$release_script_dir/verify-winget-manifests.rb" \
  "$artifacts" "$winget_package_id" "$version" "$repository" "$tag" "$publisher"

for required_formula_contract in \
  'custom tap; not homebrew/core' \
  'on_macos do' \
  'on_linux do' \
  'if Hardware::CPU.arm? && Hardware::CPU.is_64_bit?' \
  'elsif Hardware::CPU.intel? && Hardware::CPU.is_64_bit?' \
  'odie "dotnetjq supports only 64-bit ARM and Intel hosts"' \
  'glibc 2.39 baseline' \
  'depends_on "icu4c@78"' \
  'LD_LIBRARY_PATH: Formula["icu4c@78"].opt_lib' \
  'resource "corresponding_source" do' \
  'pkgshare.install "COPYING", "COPYING.LIB", "LICENSE.DotNetJq", "LICENSES.md"' \
  'pipe_output("#{bin}/dotnetjq' ; do
  grep -F -- "$required_formula_contract" "$artifacts/dotnetjq.rb" >/dev/null ||
    release_die "Homebrew formula is missing required contract: $required_formula_contract"
done

[ "$(grep -Ec '^    if Hardware::CPU\.arm\? && Hardware::CPU\.is_64_bit\?$' \
  "$artifacts/dotnetjq.rb")" -eq 2 ] ||
  release_die 'Homebrew formula must guard 64-bit ARM on both macOS and Linux'
[ "$(grep -Ec '^    elsif Hardware::CPU\.intel\? && Hardware::CPU\.is_64_bit\?$' \
  "$artifacts/dotnetjq.rb")" -eq 2 ] ||
  release_die 'Homebrew formula must guard 64-bit Intel on both macOS and Linux'
[ "$(grep -Fc 'odie "dotnetjq supports only 64-bit ARM and Intel hosts"' \
  "$artifacts/dotnetjq.rb")" -eq 2 ] ||
  release_die 'Homebrew formula must reject unsupported CPUs on both macOS and Linux'

release_note "verified exact Homebrew/WinGet metadata for $version"
