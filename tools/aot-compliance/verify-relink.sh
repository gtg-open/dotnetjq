#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: verify-relink.sh [--version VERSION] [--rid LINUX_RID] [--keep-temp]

On a matching Linux host, prove that the deterministic corresponding-source
archive can rebuild the NativeAOT CLI while the development checkout and the
external upstream jq checkout are hidden. Then alter only the LGPL-derived
GlibcCompat source, rebuild, and verify an observable jq-filter behavior change.
USAGE
}

fail() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "$script_dir/../.." && pwd -P)"
version="0.0.0-proof"
runtime_identifier=""
keep_temp=0

while (($# > 0)); do
    case "$1" in
        --version)
            (($# >= 2)) || fail "--version requires a value"
            version="$2"
            shift 2
            ;;
        --rid)
            (($# >= 2)) || fail "--rid requires a value"
            runtime_identifier="$2"
            shift 2
            ;;
        --keep-temp)
            keep_temp=1
            shift
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

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] ||
    fail "--version must be a normalized SemVer value: $version"
prerelease="${version#*-}"
if [[ "$prerelease" != "$version" ]]; then
    old_ifs="$IFS"
    IFS=.
    for identifier in $prerelease; do
        case "$identifier" in
            *[!0-9]*|0) ;;
            0*) IFS="$old_ifs"; fail "--version must be a normalized SemVer value: $version" ;;
        esac
    done
    IFS="$old_ifs"
fi

[[ "$(uname -s)" = Linux ]] || fail "the sandboxed relinking proof requires Linux"
command -v bwrap >/dev/null 2>&1 || fail "bubblewrap (bwrap) is required for the no-checkout proof"
command -v python3 >/dev/null 2>&1 || fail "python3 is required to make the controlled source edit"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"

if [[ -z "$runtime_identifier" ]]; then
    case "$(uname -m)" in
        x86_64|amd64)
            architecture=x64
            ;;
        aarch64|arm64)
            architecture=arm64
            ;;
        *)
            fail "unsupported Linux architecture: $(uname -m)"
            ;;
    esac

    if [[ -f /etc/alpine-release ]] || ldd --version 2>&1 | grep -qi musl; then
        runtime_identifier="linux-musl-$architecture"
    else
        runtime_identifier="linux-$architecture"
    fi
fi

case "$runtime_identifier" in
    linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64)
        ;;
    *)
        fail "the Linux proof accepts only Linux NativeAOT RIDs: $runtime_identifier"
        ;;
esac

if [[ -n "${DOTNETJQ_AOT_PROOF_TEMP:-}" ]]; then
    temp_base="$DOTNETJQ_AOT_PROOF_TEMP"
elif [[ -n "${XDG_CACHE_HOME:-}" ]]; then
    temp_base="$XDG_CACHE_HOME/dotnetjq-aot-compliance"
elif [[ -n "${HOME:-}" ]]; then
    temp_base="$HOME/.cache/dotnetjq-aot-compliance"
else
    fail "HOME, XDG_CACHE_HOME, or DOTNETJQ_AOT_PROOF_TEMP must identify a safe proof parent"
fi
[[ "$temp_base" = /* ]] || fail "DOTNETJQ_AOT_PROOF_TEMP must be an absolute path"
mkdir -p -- "$temp_base"
temp_base="$(cd -- "$temp_base" && pwd -P)"
proof_root="$(mktemp -d "$temp_base/dotnetjq-aot-relink.XXXXXXXX")"

cleanup() {
    if ((keep_temp == 1)); then
        printf 'PROOF_DIRECTORY=%s\n' "$proof_root"
        return
    fi

    if [[ -n "${proof_root:-}" && -d "$proof_root" &&
          "$proof_root" != "/" &&
          "$proof_root" == "$temp_base"/dotnetjq-aot-relink.* ]]; then
        rm -rf -- "$proof_root"
    else
        printf 'warning: refused unsafe temporary cleanup: %s\n' "${proof_root:-<unset>}" >&2
    fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p \
    "$proof_root/archive-one" \
    "$proof_root/archive-two" \
    "$proof_root/extracted"

nuget_packages="${NUGET_PACKAGES:-${HOME:-}/.nuget/packages}"
[[ -d "$nuget_packages" ]] ||
    fail "the sandboxed proof requires a populated NuGet package cache; restore the NativeAOT project once first"
nuget_packages="$(cd -- "$nuget_packages" && pwd -P)"

archive_creator="$repo_root/tools/aot-compliance/create-source-archive.sh"
"$archive_creator" --version "$version" --output-dir "$proof_root/archive-one" \
    > "$proof_root/archive-one-create.log"
"$archive_creator" --version "$version" --output-dir "$proof_root/archive-two" \
    > "$proof_root/archive-two-create.log"

archive_name="dotnetjq-aot-source-$version.tar.gz"
archive_one="$proof_root/archive-one/$archive_name"
archive_two="$proof_root/archive-two/$archive_name"

cmp --silent "$archive_one" "$archive_two" || fail "two source-archive runs were not byte-identical"
cmp --silent "$archive_one.sha256" "$archive_two.sha256" || fail "archive checksum files differ"
(
    cd -- "$proof_root/archive-one"
    sha256sum --check "$archive_name.sha256"
)

tar -xzf "$archive_one" -C "$proof_root/extracted"
archive_source="$proof_root/extracted/dotnetjq-aot-source-$version"
[[ -d "$archive_source" ]] || fail "archive did not contain the expected top-level directory"
(
    cd -- "$archive_source"
    sha256sum --check --quiet SOURCE_SHA256SUMS
)

embedded_upstream="$(find "$archive_source" -path '*/upstream/jq' -print -quit)"
if [[ -n "$embedded_upstream" ]]; then
    fail "source archive unexpectedly contains an upstream jq checkout"
fi

cp -a -- "$archive_source" "$proof_root/original-source"
cp -a -- "$archive_source" "$proof_root/modified-source"

component_relative="src/DotNetJq.GlibcCompat/GlibcCompatMath.cs"
original_component="$proof_root/original-source/$component_relative"
modified_component="$proof_root/modified-source/$component_relative"
original_component_sha="$(sha256sum "$original_component" | cut -d ' ' -f 1)"

python3 - "$modified_component" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
old = "    public static double Gamma(double value) => Lgamma(value);"
new = "    public static double Gamma(double value) => 424242.5d;"
text = path.read_text(encoding="utf-8")
if text.count(old) != 1:
    raise SystemExit(f"expected exactly one GlibcCompat Gamma seam in {path}")
path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")
PY

modified_component_sha="$(sha256sum "$modified_component" | cut -d ' ' -f 1)"
[[ "$original_component_sha" != "$modified_component_sha" ]] || fail "controlled component edit did not change its hash"

[[ "$repo_root" != / ]] || fail "refusing to mask the filesystem root"
masked_trees=(--tmpfs "$repo_root")
upstream_root="${DOTNETJQ_UPSTREAM:-$repo_root/upstream/jq}"
if [[ -d "$upstream_root" ]]; then
    upstream_root="$(cd -- "$upstream_root" && pwd -P)"
    [[ "$upstream_root" != / ]] || fail "refusing to mask the filesystem root"
    # Resolve external checkouts and symlinks without assuming a workstation layout.
    if [[ "$upstream_root" != "$repo_root" && "$upstream_root" != "$repo_root/"* ]]; then
        masked_trees+=(--tmpfs "$upstream_root")
    fi
fi

run_isolated() {
    env -u LD_PRELOAD bwrap \
        --die-with-parent \
        --new-session \
        --unshare-net \
        --ro-bind / / \
        --dev /dev \
        --proc /proc \
        "${masked_trees[@]}" \
        --tmpfs /tmp \
        --bind "$proof_root" "$proof_root" \
        --chdir "$proof_root" \
        --setenv DOTNETJQ_UPSTREAM /__dotnetjq_upstream_checkout_is_intentionally_unavailable__ \
        --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
        --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1 \
        --setenv DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE true \
        --setenv DOTNET_NOLOGO true \
        --setenv NUGET_PACKAGES "$nuget_packages" \
        --setenv TMPDIR /tmp \
        --unsetenv LD_PRELOAD \
        "$@"
}

rebuilt_executable=""
run_build() {
    local label="$1"
    local build_source="$2"
    local output_dir="$proof_root/$label-output"
    local build_log="$proof_root/$label-build.log"

    run_isolated \
        "$build_source/tools/aot-compliance/rebuild-aot.sh" \
        --version "$version" \
        "$build_source" \
        "$runtime_identifier" \
        "$output_dir" 2>&1 | tee "$build_log"

    rebuilt_executable="$(sed -n 's/^AOT_EXECUTABLE=//p' "$build_log" | tail -n 1)"
    [[ -n "$rebuilt_executable" && -f "$rebuilt_executable" ]] ||
        fail "the $label build did not report a NativeAOT executable"
    rebuilt_version="$(run_isolated "$rebuilt_executable" --version | tr -d '\r')"
    [[ "$rebuilt_version" = "dotnetjq-$version (jq-1.8.2 compatible)" ]] ||
        fail "the $label build reported the wrong version: $rebuilt_version"
}

run_build original "$proof_root/original-source"
original_executable="$rebuilt_executable"
original_result="$(run_isolated "$original_executable" -n '5 | gamma' | tr -d '\r')"
[[ "$original_result" != 424242.5 ]] || fail "unmodified source unexpectedly produced the proof sentinel"

run_build modified "$proof_root/modified-source"
modified_executable="$rebuilt_executable"
modified_result="$(run_isolated "$modified_executable" -n '5 | gamma' | tr -d '\r')"
[[ "$modified_result" = 424242.5 ]] ||
    fail "modified NativeAOT CLI returned '$modified_result' instead of the proof sentinel"

original_binary_sha="$(sha256sum "$original_executable" | cut -d ' ' -f 1)"
modified_binary_sha="$(sha256sum "$modified_executable" | cut -d ' ' -f 1)"
[[ "$original_binary_sha" != "$modified_binary_sha" ]] || fail "modified source produced an identical executable"
source_archive_sha="$(cut -d ' ' -f 1 < "$archive_one.sha256")"

printf 'AOT_RELINK_PROOF=PASS\n'
printf 'RID=%s\n' "$runtime_identifier"
printf 'SOURCE_ARCHIVE_SHA256=%s\n' "$source_archive_sha"
printf 'ORIGINAL_COMPONENT_SHA256=%s\n' "$original_component_sha"
printf 'MODIFIED_COMPONENT_SHA256=%s\n' "$modified_component_sha"
printf 'ORIGINAL_RESULT=%s\n' "$original_result"
printf 'MODIFIED_RESULT=%s\n' "$modified_result"
printf 'ORIGINAL_BINARY_SHA256=%s\n' "$original_binary_sha"
printf 'MODIFIED_BINARY_SHA256=%s\n' "$modified_binary_sha"
