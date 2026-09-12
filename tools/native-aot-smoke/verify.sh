#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project="$script_dir/DotNetJq.NativeAotSmoke.csproj"
lexer_source="$script_dir/../../src/DotNetJq/Generated/Lexer/lexer.c.cs"
temporary_parent="$(realpath -- "${TMPDIR:-/tmp}")"

if grep -Eq 'System\.Reflection|GetMaxParseToken|\.GetField\(|\.GetValue\(' "$lexer_source"; then
    printf 'generated lexer still contains a NativeAOT-unsafe reflection lookup\n' >&2
    exit 1
elif [[ $? -ne 1 ]]; then
    printf 'could not scan generated lexer for NativeAOT-unsafe reflection lookups\n' >&2
    exit 1
fi
if ! grep -Fqx \
    '        private const Tokens maxParseToken = Tokens.maxParseToken;' \
    "$lexer_source"; then
    printf 'generated lexer does not statically root its typed maxParseToken sentinel\n' >&2
    exit 1
fi

if [[ ! -d "$temporary_parent" || "$temporary_parent" == "/" ]]; then
    printf 'refusing unsafe temporary parent: %s\n' "$temporary_parent" >&2
    exit 2
fi

publish_dir="$(mktemp -d -- "$temporary_parent/dotnetjq-native-aot.XXXXXXXX")"
cleanup_publish_dir() {
    local resolved_dir
    resolved_dir="$(realpath -m -- "$publish_dir")"
    if [[ -d "$resolved_dir" && "$resolved_dir" == "$temporary_parent"/dotnetjq-native-aot.* ]]; then
        rm -rf -- "$resolved_dir"
    else
        printf 'refusing unsafe cleanup target: %s\n' "$resolved_dir" >&2
        return 1
    fi
}
trap cleanup_publish_dir EXIT

dotnet publish "$project" \
    --configuration Release \
    --output "$publish_dir" \
    -p:PublishAot=true \
    -p:NuGetAudit=false \
    --verbosity minimal

executable="$publish_dir/DotNetJq.NativeAotSmoke"
if [[ ! -x "$executable" ]]; then
    printf 'NativeAOT publish did not produce an executable: %s\n' "$executable" >&2
    exit 1
fi

"$executable"

unexpected_generator_dll="$(find "$publish_dir" -type f \
    \( -iname '*gppg*.dll' -o -iname '*gplex*.dll' \) -print -quit)"
if [[ -n "$unexpected_generator_dll" ]]; then
    printf 'NativeAOT publish unexpectedly contains a parser/lexer generator DLL\n' >&2
    find "$publish_dir" -type f \
        \( -iname '*gppg*.dll' -o -iname '*gplex*.dll' \) -print >&2
    exit 1
fi

printf 'PUBLISHED_FILES\n'
find "$publish_dir" -maxdepth 1 -type f -print | LC_ALL=C sort |
while IFS= read -r published_file; do
    published_file_size="$(wc -c <"$published_file" | tr -d ' ')"
    printf '%s %s bytes\n' "${published_file##*/}" "$published_file_size"
done
printf 'EXECUTABLE_SHA256 '
sha256sum "$executable" | cut -d' ' -f1
printf 'GENERATOR_DLLS none\n'
