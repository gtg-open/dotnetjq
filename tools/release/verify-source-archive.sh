#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

archive=
version=
while [ "$#" -gt 0 ]; do
  case $1 in
    --archive) [ "$#" -ge 2 ] || release_die '--archive requires a value'; archive=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    -h|--help) printf '%s\n' 'Usage: verify-source-archive.sh --archive PATH --version VERSION'; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -f "$archive" ] || release_die "source archive does not exist: ${archive-}"
release_validate_version "$version"
[ "$(basename -- "$archive")" = "dotnetjq-$version-source.tar.gz" ] ||
  release_die "unexpected source archive name: $(basename -- "$archive")"
release_require_command tar

prefix="dotnetjq-$version-source"
entries=$(tar -tzf "$archive")
[ -n "$entries" ] || release_die 'source archive is empty'
while IFS= read -r entry; do
  case $entry in
    "$prefix"|"$prefix/"*) ;;
    *) release_die "source archive entry escapes its prefix: $entry" ;;
  esac
  case $entry in *'/../'*|*'/..'|*\\*) release_die "unsafe source archive entry: $entry" ;; esac
done <<<"$entries"

required_paths='global.json
Directory.Build.props
COPYING
COPYING.LIB
LICENSE.DotNetJq
LICENSES.md
THIRD_PARTY_NOTICES.md
THIRD_PARTY_LICENSES/GPPG-License.md
src/DotNetJq.Cli/DotNetJq.Cli.csproj
src/DotNetJq/DotNetJq.csproj
src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj
src/DotNetJq.GlibcCompat/GlibcCompatMath.cs
src/DotNetJq.GlibcCompat/COPYING.LIB
tools/release/build-native-archive.sh
packaging/release-targets.tsv'
while IFS= read -r required; do
  printf '%s\n' "$entries" | grep -Fx -- "$prefix/$required" >/dev/null ||
    release_die "source archive is missing required rebuild input: $required"
done <<<"$required_paths"

for source_tree in src/DotNetJq.Cli src/DotNetJq src/DotNetJq.GlibcCompat; do
  printf '%s\n' "$entries" | grep -F "$prefix/$source_tree/" | grep -E '\.cs$' >/dev/null ||
    release_die "source archive contains no C# sources for $source_tree"
done

release_note "verified $(basename -- "$archive")"
