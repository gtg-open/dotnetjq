#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: rebuild-aot.sh --version VERSION SOURCE_ROOT RID OUTPUT_DIRECTORY

Build the DotNetJq CLI as a self-contained NativeAOT executable from an
extracted corresponding-source archive. RID must be one of:
  win-x64 win-arm64 linux-x64 linux-arm64
  linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64
USAGE
}

fail() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

version=""
while (($# > 0)); do
    case "$1" in
        --version)
            (($# >= 2)) || fail "--version requires a value"
            version="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --*)
            fail "unknown option: $1"
            ;;
        *)
            break
            ;;
    esac
done

[[ -n "$version" ]] || fail "--version is required"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] ||
    fail "version must be a normalized SemVer value: $version"
prerelease="${version#*-}"
if [[ "$prerelease" != "$version" ]]; then
    old_ifs="$IFS"
    IFS=.
    for identifier in $prerelease; do
        case "$identifier" in
            *[!0-9]*|0) ;;
            0*) IFS="$old_ifs"; fail "version must be a normalized SemVer value: $version" ;;
        esac
    done
    IFS="$old_ifs"
fi

(($# == 3)) || {
    usage >&2
    exit 2
}

source_root="$1"
runtime_identifier="$2"
output_dir="$3"

case "$runtime_identifier" in
    win-x64|win-arm64|linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-x64|osx-arm64)
        ;;
    *)
        fail "unsupported NativeAOT RID: $runtime_identifier"
        ;;
esac

[[ -d "$source_root" ]] || fail "source root does not exist: $source_root"
source_root="$(cd -- "$source_root" && pwd -P)"
project="$source_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
[[ -f "$project" ]] || fail "CLI project is missing: $project"

if [[ -e "$source_root/upstream/jq" || -L "$source_root/upstream/jq" ]]; then
    fail "the corresponding-source rebuild must not contain an upstream jq checkout"
fi

host_kernel="$(uname -s)"
case "$host_kernel" in
    Linux)
        [[ "$runtime_identifier" = linux-* ]] ||
            fail "$runtime_identifier requires a matching Windows or macOS build host"
        ;;
    Darwin)
        [[ "$runtime_identifier" = osx-* ]] ||
            fail "$runtime_identifier requires a matching Windows or Linux build host"
        ;;
    MINGW*|MSYS*|CYGWIN*)
        [[ "$runtime_identifier" = win-* ]] ||
            fail "$runtime_identifier requires a matching Linux or macOS build host"
        ;;
    *)
        fail "unsupported build host: $host_kernel"
        ;;
esac

if [[ -e "$output_dir" && ! -d "$output_dir" ]]; then
    fail "output path exists and is not a directory: $output_dir"
fi
mkdir -p -- "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd -P)"
existing_output="$(find "$output_dir" ! -path "$output_dir" -print -quit)"
if [[ -n "$existing_output" ]]; then
    fail "output directory must be empty: $output_dir"
fi

command -v dotnet >/dev/null 2>&1 || fail ".NET SDK is required"

build_environment=(
    env
    -u LD_PRELOAD
    DOTNETJQ_UPSTREAM=/__dotnetjq_upstream_checkout_is_intentionally_unavailable__
    DOTNET_CLI_TELEMETRY_OPTOUT=1
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
    DOTNET_NOLOGO=true
)

if [[ "$host_kernel" = Linux && -z "${CC:-}" ]]; then
    if command -v clang >/dev/null 2>&1; then
        build_environment+=(CC=clang)
    elif command -v gcc >/dev/null 2>&1; then
        build_environment+=(CC=gcc)
    else
        fail "NativeAOT requires clang or gcc on Linux"
    fi
fi

"${build_environment[@]}" dotnet publish "$project" \
    --disable-build-servers \
    --maxcpucount:1 \
    --configuration Release \
    --runtime "$runtime_identifier" \
    --self-contained true \
    -p:PublishAot=true \
    -p:DebugSymbols=false \
    -p:DebugType=none \
    -p:ContinuousIntegrationBuild=true \
    -p:NuGetAudit=false \
    -p:Version="$version" \
    -p:PackageVersion="$version" \
    -p:UseSharedCompilation=false \
    --output "$output_dir"

executable=""
candidate_names=(
    dotnetjq
    DotNetJq.Cli
    jq
    dotnetjq.exe
    DotNetJq.Cli.exe
    jq.exe
)

for candidate_name in "${candidate_names[@]}"; do
    if [[ -f "$output_dir/$candidate_name" ]]; then
        executable="$output_dir/$candidate_name"
        break
    fi
done

if [[ -z "$executable" ]]; then
    executable_candidate=""
    executable_candidate_count=0
    for candidate_path in "$output_dir"/*; do
        [[ -f "$candidate_path" ]] || continue
        case "$candidate_path" in
            *.dbg|*.dll|*.json|*.pdb|*.xml) continue ;;
        esac
        executable_candidate="$candidate_path"
        executable_candidate_count=$((executable_candidate_count + 1))
    done
    if ((executable_candidate_count == 1)); then
        executable="$executable_candidate"
    fi
fi

[[ -n "$executable" ]] || fail "could not identify the NativeAOT executable in $output_dir"
printf 'AOT_VERSION=%s\n' "$version"
printf 'AOT_EXECUTABLE=%s\n' "$executable"
