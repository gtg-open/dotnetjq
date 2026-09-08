#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../.." && pwd)
grammar_path="$repo_root/src/DotNetJq/Grammar/lexer.l"
tool_manifest="$repo_root/dotnet-tools.json"
default_output="src/DotNetJq/Generated/Lexer/lexer.c.cs"
expected_package="springcomp.gplex"
expected_version="1.2.5"
expected_command="dotnet-gplex"
pipeline_revision="lexer-output-v8"

mode=generate
output_arg=$default_output

usage() {
    printf '%s\n' \
        "Usage: tools/parser-gen/generate-gplex-lexer.sh [--check] [--output PATH]" \
        "" \
        "Generate the managed jq scanner from src/DotNetJq/Grammar/lexer.l." \
        "The default production output is $default_output." \
        "" \
        "  --check        Compare normalized generator output with PATH." \
        "  --output PATH  Override the staging/check output path." \
        "  --help         Show this help."
}

while (($# > 0)); do
    case "$1" in
        --check)
            mode=check
            shift
            ;;
        --output)
            if (($# < 2)); then
                printf '%s\n' "error: --output requires a path" >&2
                exit 2
            fi
            output_arg=$2
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            printf '%s\n' "error: unknown argument '$1'" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ ! -f "$grammar_path" ]]; then
    printf '%s\n' "error: managed lexer grammar not found: $grammar_path" >&2
    exit 1
fi

if [[ ! -f "$tool_manifest" ]]; then
    printf '%s\n' "error: pinned tool manifest not found: $tool_manifest" >&2
    exit 1
fi

if [[ "$output_arg" = /* ]]; then
    output_path=$output_arg
else
    output_path="$repo_root/$output_arg"
fi

python3 - "$tool_manifest" "$expected_package" "$expected_version" "$expected_command" <<'PY'
import json
import sys

manifest_path, expected_package, expected_version, expected_command = sys.argv[1:]
with open(manifest_path, "rb") as stream:
    manifest = json.load(stream)
tool = manifest.get("tools", {}).get(expected_package)
if tool is None:
    raise SystemExit(f"error: tool manifest does not contain {expected_package}")
if tool.get("version") != expected_version or expected_command not in tool.get("commands", []):
    raise SystemExit(
        "error: lexer generator must be "
        f"{expected_package} {expected_version} exposing {expected_command}"
    )
PY

source_sha=$(sha256sum -- "$grammar_path" | awk '{print $1}')
generation_key=$(
    printf '%s\n%s\n%s\n%s\n%s' \
        "$pipeline_revision" \
        "$source_sha" \
        "$expected_package" \
        "$expected_version" \
        "$expected_command" |
        sha256sum | awk '{print $1}'
)
source_header="// jq-port-managed-source-sha256: $source_sha"
generator_header="// jq-port-generator: $expected_package/$expected_version ($expected_command)"
generation_key_header="// jq-port-generation-key-sha256: $generation_key"

if [[ "$mode" == generate && -f "$output_path" ]] &&
    head -n 8 -- "$output_path" | grep -Fqx -- "$generation_key_header"; then
    printf '%s\n' "lexer output is current; reused $output_path"
    exit 0
fi

work_parent="$repo_root/obj/parser-gen"
mkdir -p -- "$work_parent"
work_dir=$(mktemp -d "$work_parent/gplex-work.XXXXXX")
cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

# Keep both input and output names stable. Besides avoiding GPLEX's Unix
# absolute-path/switch ambiguity, this prevents host paths entering the header.
cp -- "$grammar_path" "$work_dir/lexer.l"
if ! generator_log=$(
    cd -- "$work_dir"
    dotnet tool run "$expected_command" -- /out:lexer.raw.cs /verbose lexer.l 2>&1
); then
    printf '%s\n' "$generator_log" >&2
    printf '%s\n' "error: $expected_command failed" >&2
    exit 1
fi
if grep -Eiq '(^|[[:space:]])warning[[:space:]]+[0-9]+' <<<"$generator_log"; then
    printf '%s\n' "$generator_log" >&2
    printf '%s\n' "error: $expected_command emitted a warning" >&2
    exit 1
fi

raw_output="$work_dir/lexer.raw.cs"
frame_normalized_body="$work_dir/lexer.frame-normalized-body.cs"
normalized_body="$work_dir/lexer.normalized-body.cs"
normalized_output="$work_dir/lexer.normalized.cs"
if [[ ! -f "$raw_output" ]]; then
    printf '%s\n' "error: dotnet-gplex did not create $raw_output" >&2
    exit 1
fi

if ! grep -Fqx '//  GPLEX Version:  1.2.5.0' "$raw_output"; then
    printf '%s\n' "error: generated output is not from pinned Springcomp.GPLEX 1.2.5" >&2
    exit 1
fi
if ! grep -Fqx '    public class BufferException : Exception' "$raw_output" ||
    ! grep -Fqx '    public abstract class ScanBuff' "$raw_output"; then
    printf '%s\n' "error: pinned GPLEX buffer declarations changed unexpectedly" >&2
    exit 1
fi

# Springcomp.GPLEX writes host/time metadata even when the automaton and C# are
# identical. Normalize those volatile header lines and CRLF line endings. Its
# legacy frame predates nullable-reference analysis and exposes buffer helpers
# publicly, so disable nullable analysis and internalize exactly those two
# generated helper types to preserve the production assembly's public surface.
LC_ALL=C sed -E \
    -e 's/\r$//' \
    -e 's/[[:blank:]]+$//' \
    -e 's|^//  Machine:.*$|//  Machine: <normalized>|' \
    -e 's|^//  DateTime:.*$|//  DateTime: <normalized>|' \
    -e 's|^//  GPLEX input file <lexer\.l - .*>$|//  GPLEX input file <lexer.l>|' \
    -e 's/^    public class BufferException : Exception$/    internal class BufferException : Exception/' \
    -e 's/^    public abstract class ScanBuff$/    internal abstract class ScanBuff/' \
    -e '/^#define PERSIST$/a #nullable disable' \
    "$raw_output" > "$frame_normalized_body"

# GPLEX 1.2.5 discovers the standalone token sentinel with reflection. That
# lookup is unnecessary because the emitted Tokens enum is known at compile
# time, and it gives the NativeAOT trimmer no statically rooted field access to
# preserve. Rewrite the pinned frame shape deterministically to a typed enum
# constant. Fail closed if a future GPLEX frame changes either source block.
python3 - "$frame_normalized_body" "$normalized_body" <<'PY'
from pathlib import Path
import sys

source_path = Path(sys.argv[1])
output_path = Path(sys.argv[2])
text = source_path.read_text(encoding="utf-8")

reflection_using = "using System.Reflection;\n"
reflection_block = """        private static int GetMaxParseToken() {
          var f = typeof(Tokens).GetField("maxParseToken");
            return (f == null ? int.MaxValue : (int)f.GetValue(null));
        }

        static int parserMax = GetMaxParseToken();
"""
typed_block = """        // GPLEX 1.2.5 normally reflects over Tokens here. Keep the generated
        // sentinel as a statically rooted enum constant so NativeAOT trimming
        // cannot remove or hide it.
        private const Tokens maxParseToken = Tokens.maxParseToken;
        private const int parserMax = (int)maxParseToken;
"""
reflection_comment = """            // parserMax is set by reflecting on the Tokens
            // enumeration.  If maxParseToken is defined
            // that is used, otherwise int.MaxValue is used.
"""
typed_comment = """            // Scanner-only token values at or above the typed sentinel must
            // not reach parser consumers.
"""

expected_blocks = {
    "System.Reflection import": (reflection_using, ""),
    "reflection lookup": (reflection_block, typed_block),
    "reflection explanation": (reflection_comment, typed_comment),
}
for label, (old, new) in expected_blocks.items():
    count = text.count(old)
    if count != 1:
        raise SystemExit(
            f"error: pinned GPLEX {label} changed unexpectedly (found {count}, expected 1)"
        )
    text = text.replace(old, new)

for forbidden in ("System.Reflection", "GetMaxParseToken", ".GetField(", ".GetValue("):
    if forbidden in text:
        raise SystemExit(f"error: generated lexer still contains reflection marker {forbidden!r}")

output_path.write_text(text, encoding="utf-8", newline="\n")
PY

{
    printf '%s\n' \
        "$source_header" \
        "$generator_header" \
        "$generation_key_header" \
        "// <auto-generated/>" \
        "// DOTNETJQ PORT MAP" \
        "// Upstream repository: https://github.com/jqlang/jq" \
        "// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)" \
        "// Upstream file: src/lexer.c" \
        "// Source of truth: src/DotNetJq/Grammar/lexer.l" \
        "// Strategy: GENERATED" \
        "// Generator: tools/parser-gen/generate-gplex-lexer.sh (springcomp.gplex/1.2.5)" \
        "// Target file: src/DotNetJq/Generated/Lexer/lexer.c.cs"
    cat -- "$normalized_body"
} > "$normalized_output"

if [[ "$mode" == check ]]; then
    if [[ ! -f "$output_path" ]]; then
        printf '%s\n' "error: generated lexer output is missing: $output_path" >&2
        exit 1
    fi

    for expected_header in "$source_header" "$generator_header" "$generation_key_header"; do
        if ! head -n 8 -- "$output_path" | grep -Fqx -- "$expected_header"; then
            printf '%s\n' "error: generated lexer header is stale: $output_path" >&2
            printf '%s\n' "expected: $expected_header" >&2
            exit 1
        fi
    done

    if ! cmp -s -- "$output_path" "$normalized_output"; then
        printf '%s\n' "error: generated lexer output is stale: $output_path" >&2
        diff -u -- "$output_path" "$normalized_output" | sed -n '1,200p' || true
        exit 1
    fi

    printf '%s\n' "checked $output_path"
    exit 0
fi

mkdir -p -- "$(dirname -- "$output_path")"
temporary_output="$output_path.tmp.$$"
cp -- "$normalized_output" "$temporary_output"
mv -f -- "$temporary_output" "$output_path"
printf '%s\n' "generated $output_path"
