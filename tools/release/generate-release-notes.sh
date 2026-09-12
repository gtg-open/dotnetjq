#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

version=
repository=
tag=
output=

while [ "$#" -gt 0 ]; do
  case $1 in
    --version) version=${2-}; shift 2 ;;
    --repository) repository=${2-}; shift 2 ;;
    --tag) tag=${2-}; shift 2 ;;
    --output) output=${2-}; shift 2 ;;
    *) release_die "unknown generate-release-notes option: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_repository "$repository"
[ "$tag" = "v$version" ] ||
  release_die "release tag must be v<version>: expected v$version, found ${tag:-<empty>}"
[ -n "$output" ] || release_die 'release-notes output path is required'
[ ! -d "$output" ] && [ ! -L "$output" ] ||
  release_die "release-notes output must be a regular-file path: $output"

output_directory=${output%/*}
[ "$output_directory" != "$output" ] || output_directory=.
work_dir=$(release_make_temp_dir "$output_directory" release-notes)
cleanup_release_notes() {
  case $work_dir in
    "$output_directory"/.release-notes.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe release-notes cleanup: $work_dir" ;;
  esac
}
trap cleanup_release_notes EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

candidate="$work_dir/release-notes.md"
cat >"$candidate" <<EOF
# DotNetJq $version

This release is compatible with jq 1.8.2.

- [Installation and .NET, CLI, and PowerShell examples](https://github.com/$repository/blob/$tag/docs/installation.md)
- [Canonical jq 1.8 manual](https://jqlang.org/manual/v1.8/)
- [SHA-256 checksums](https://github.com/$repository/releases/download/$tag/SHA256SUMS)
- [Release and verification policy](https://github.com/$repository/blob/$tag/RELEASING.md)

The attached archives are self-contained NativeAOT commands for all supported
64-bit Windows, Linux/glibc, Linux/musl, and macOS targets. NuGet provides the
managed library and the RID-selecting \`dotnetjq\` .NET tool package.
EOF

release_assert_new_or_identical "$candidate" "$output"
release_note "generated deterministic release notes: $output"
