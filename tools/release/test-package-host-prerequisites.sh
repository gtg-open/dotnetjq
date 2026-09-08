#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

checks=0
fail() {
  printf 'package host-prerequisite test: %s\n' "$*" >&2
  exit 1
}

sdk_fixture() (
  expected_rid=$1
  fixture_info=$2
  fixture_status=${3:-0}
  dotnet() {
    [ "$#" -eq 1 ] && [ "$1" = --info ] || return 97
    [ "${DOTNET_CLI_UI_LANGUAGE-}" = en-US ] || return 98
    printf '%s\n' "$fixture_info"
    return "$fixture_status"
  }
  release_require_native_dotnet_sdk "$expected_rid"
)

for rid in win-x64 win-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64; do
  fixture_info=$(printf '.NET SDK:\n Version: 10.0.400\nRuntime Environment:\n RID: %s\n' "$rid")
  sdk_fixture "$rid" "$fixture_info" || fail "native $rid SDK was rejected"
  checks=$((checks + 1))
done
sdk_fixture win-arm64 $'.NET SDK:\r\nRuntime Environment:\r\n RID: win-arm64\r\nHost:\r\n Architecture: arm64\r' ||
  fail 'Windows CRLF SDK information was rejected'
checks=$((checks + 1))

for fixture_info in \
  'RID: win-x64' \
  'RID: osx-arm64' \
  '.NET runtimes installed: no SDK' \
  'RID:' \
  $'RID: win-arm64\nRID: win-arm64'; do
  if sdk_fixture win-arm64 "$fixture_info" >/dev/null 2>&1; then
    fail "non-native, missing, or ambiguous SDK RID was accepted: $fixture_info"
  fi
  checks=$((checks + 1))
done
if sdk_fixture linux-musl-x64 'RID: linux-x64' >/dev/null 2>&1; then
  fail 'glibc SDK was accepted for a musl package'
fi
checks=$((checks + 1))
if sdk_fixture win-arm64 'RID: win-arm64' 1 >/dev/null 2>&1; then
  fail 'a failing dotnet --info command was accepted'
fi
checks=$((checks + 1))

# Exercise the production pointer inventory filter using each runner's awk,
# including macOS BSD awk. Unexpected DLLs/nested files must still be returned
# so the caller rejects them instead of silently ignoring a fallback payload.
for path in \
  tools/net10.0/any/DotnetToolSettings.xml \
  tools/net10.0/any/unexpected.dll \
  tools/net10.0/any/nested/unexpected.dll \
  tools/net11.0/any/another-file; do
  actual=$(printf '%s\n' "$path" | release_pointer_any_payload)
  [ "$actual" = "$path" ] || fail "pointer payload was omitted: $path"
  checks=$((checks + 1))
done
for path in \
  tools/net10.0/any/ \
  tools/net10.0/any/nested/ \
  tools/net10.0/any \
  tools/net10.0/win-arm64/dotnetjq.exe \
  tools//any/unexpected.dll \
  other/tools/net10.0/any/unexpected.dll \
  tools/net10.0/anything/unexpected.dll \
  dotnetjq.nuspec; do
  actual=$(printf '%s\n' "$path" | release_pointer_any_payload)
  [ -z "$actual" ] || fail "non-payload inventory entry was selected: $path"
  checks=$((checks + 1))
done

printf 'package host-prerequisite tests passed (%s/%s)\n' "$checks" "$checks"
