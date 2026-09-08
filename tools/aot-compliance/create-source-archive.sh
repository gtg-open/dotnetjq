#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: create-source-archive.sh --version VERSION --output-dir DIRECTORY [--source-root DIRECTORY]

Create a deterministic corresponding-source archive for DotNetJq NativeAOT
artifacts. The output directory may exist, but the archive and checksum files
must not already exist.
USAGE
}

fail() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
default_source_root="$(cd -- "$script_dir/../.." && pwd -P)"
source_root="$default_source_root"
version=""
output_dir=""

while (($# > 0)); do
    case "$1" in
        --version)
            (($# >= 2)) || fail "--version requires a value"
            version="$2"
            shift 2
            ;;
        --output-dir)
            (($# >= 2)) || fail "--output-dir requires a value"
            output_dir="$2"
            shift 2
            ;;
        --source-root)
            (($# >= 2)) || fail "--source-root requires a value"
            source_root="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "unknown argument: $1"
            ;;
    esac
done

[[ -n "$version" ]] || fail "--version is required"
[[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z._+-]*$ ]] ||
    fail "version may contain only letters, digits, dot, underscore, plus, and hyphen"
[[ -n "$output_dir" ]] || fail "--output-dir is required"
[[ -d "$source_root" ]] || fail "source root does not exist: $source_root"

source_root="$(cd -- "$source_root" && pwd -P)"
mkdir -p -- "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd -P)"

command -v gzip >/dev/null 2>&1 || fail "gzip is required"
if command -v sha256sum >/dev/null 2>&1; then
    hash_file() { sha256sum -- "$1" | awk '{ print $1 }'; }
elif command -v shasum >/dev/null 2>&1; then
    hash_file() { shasum -a 256 -- "$1" | awk '{ print $1 }'; }
else
    fail "sha256sum or shasum is required"
fi

if command -v tar >/dev/null 2>&1 && tar --version 2>/dev/null | grep -q 'GNU tar'; then
    gnu_tar=tar
elif command -v gtar >/dev/null 2>&1 && gtar --version 2>/dev/null | grep -q 'GNU tar'; then
    gnu_tar=gtar
else
    fail "GNU tar is required for deterministic archives (install tar or gtar)"
fi

bundle_name="dotnetjq-aot-source-$version"
archive_name="$bundle_name.tar.gz"
archive_path="$output_dir/$archive_name"
checksum_path="$archive_path.sha256"

[[ ! -e "$archive_path" ]] || fail "refusing to overwrite: $archive_path"
[[ ! -e "$checksum_path" ]] || fail "refusing to overwrite: $checksum_path"

temp_base="${TMPDIR:-/tmp}"
[[ "$temp_base" = /* ]] || fail "TMPDIR must be an absolute path"
[[ -d "$temp_base" ]] || fail "temporary directory does not exist: $temp_base"
temp_base="$(cd -- "$temp_base" && pwd -P)"
stage_parent="$(mktemp -d "$temp_base/dotnetjq-aot-source.XXXXXXXX")"
stage_root="$stage_parent/$bundle_name"

cleanup() {
    if [[ -n "${stage_parent:-}" && -d "$stage_parent" &&
          "$stage_parent" != "/" &&
          "$stage_parent" == "$temp_base"/dotnetjq-aot-source.* ]]; then
        rm -rf -- "$stage_parent"
    else
        printf 'warning: refused unsafe temporary cleanup: %s\n' "${stage_parent:-<unset>}" >&2
    fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p -- "$stage_root"

copy_relative() {
    local relative="$1"
    local source_file="$source_root/$relative"
    local destination_file="$stage_root/$relative"

    [[ "$relative" != /* && "$relative" != *'..'* ]] || fail "unsafe relative path: $relative"
    [[ -f "$source_file" ]] || fail "required source file is missing: $relative"
    mkdir -p -- "$(dirname -- "$destination_file")"
    cp -- "$source_file" "$destination_file"
}

required_root_files=(
    COPYING
    COPYING.LIB
    COPYING.jq
    Directory.Build.props
    LICENSE.DotNetJq
    LICENSES.md
    README.md
    THIRD_PARTY_NOTICES.md
    dotnet-tools.json
    global.json
    porting/AOT_RELINKING.md
    porting/PORTING_MANIFEST.json
)

for relative in "${required_root_files[@]}"; do
    copy_relative "$relative"
done

source_directories=(
    src/DotNetJq.Cli
    src/DotNetJq
    src/DotNetJq.GlibcCompat
    THIRD_PARTY_LICENSES
    tools/aot-compliance
)

for relative in "${source_directories[@]}"; do
    [[ -d "$source_root/$relative" ]] || fail "required source directory is missing: $relative"
done

source_inventory="$stage_parent/source-files.inventory"
(
    cd -- "$source_root"
    find "${source_directories[@]}" -type f \
        ! -path '*/bin/*' \
        ! -path '*/obj/*' \
        -print0 | LC_ALL=C sort -z > "$source_inventory"
) || fail "could not enumerate the complete corresponding-source input"

while IFS= read -r -d '' relative; do
    copy_relative "$relative"
done < "$source_inventory"

cat > "$stage_root/SOURCE_BUNDLE_INFO.txt" <<INFO
Format: DotNetJq NativeAOT corresponding source v1
Release-Version: $version
Target-Framework: net10.0
Upstream-jq-Tag: jq-1.8.2
Upstream-jq-Commit: 34f7186b86743a083a589741b6cea95293524108
INFO

find "$stage_root" -type d -exec chmod 0755 {} +
find "$stage_root" -type f -exec chmod 0644 {} +
find "$stage_root" -type f -name '*.sh' -exec chmod 0755 {} +

bundle_inventory="$stage_parent/bundle-files.inventory"
(
    cd -- "$stage_root"
    find . -type f ! -name SOURCE_SHA256SUMS -print0 |
        LC_ALL=C sort -z > "$bundle_inventory"
) || fail "could not enumerate the staged corresponding-source bundle"
(
    cd -- "$stage_root"
    while IFS= read -r -d '' source_file; do
        source_hash="$(hash_file "$source_file")"
        printf '%s  %s\n' "$source_hash" "$source_file"
    done < "$bundle_inventory"
) > "$stage_root/SOURCE_SHA256SUMS"
chmod 0644 "$stage_root/SOURCE_SHA256SUMS"

(
    cd -- "$stage_parent"
    "$gnu_tar" \
        --sort=name \
        --mtime='@0' \
        --owner=0 \
        --group=0 \
        --numeric-owner \
        --format=gnu \
        -cf - \
        "$bundle_name" |
        gzip -n > "$archive_path"
)

(
    cd -- "$output_dir"
    archive_hash="$(hash_file "$archive_name")"
    printf '%s  %s\n' "$archive_hash" "$archive_name" > "$archive_name.sha256"
)

printf 'SOURCE_ARCHIVE=%s\n' "$archive_path"
source_archive_sha256="$(cut -d ' ' -f 1 < "$checksum_path")"
printf 'SOURCE_ARCHIVE_SHA256=%s\n' "$source_archive_sha256"
