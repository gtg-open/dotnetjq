#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

fail() {
  printf 'release host-RID test: %s\n' "$*" >&2
  exit 1
}

assert_equal() {
  local expected=$1
  local actual=$2
  local label=$3
  [ "$actual" = "$expected" ] ||
    fail "$label (expected $expected, got $actual)"
}

host_rid_fixture() {
  local fixture_kernel=$1
  local fixture_machine=$2
  local architecture_w6432=$3
  local processor_architecture=$4

  if [ "$architecture_w6432" = '<unset>' ]; then
    unset PROCESSOR_ARCHITEW6432
  else
    export PROCESSOR_ARCHITEW6432=$architecture_w6432
  fi
  if [ "$processor_architecture" = '<unset>' ]; then
    unset PROCESSOR_ARCHITECTURE
  else
    export PROCESSOR_ARCHITECTURE=$processor_architecture
  fi

  uname() {
    case ${1-} in
      -s) printf '%s\n' "$fixture_kernel" ;;
      -m) printf '%s\n' "$fixture_machine" ;;
      *) fail "fixture received unsupported uname argument: ${1-<empty>}" ;;
    esac
  }

  release_host_rid
}

# The Windows ARM64 hosted runner can expose x86_64 from its emulated Git Bash
# process. Windows' native-architecture variable must win over both that value
# and PROCESSOR_ARCHITECTURE.
assert_equal win-arm64 \
  "$(host_rid_fixture MINGW64_NT-10.0-26100 x86_64 ARM64 AMD64)" \
  'ARM64 Windows with x64-emulated uname'

assert_equal win-arm64 \
  "$(host_rid_fixture MSYS_NT-10.0-26100 x86_64 '<unset>' arm64)" \
  'native ARM64 Windows architecture'

assert_equal win-x64 \
  "$(host_rid_fixture CYGWIN_NT-10.0 x86_64 aMd64 x86)" \
  'case-normalized native AMD64 Windows architecture'

# Non-Windows hosts continue to use uname and ignore Windows-only variables.
assert_equal linux-x64 \
  "$(host_rid_fixture Linux x86_64 ARM64 ARM64)" \
  'Linux uname architecture'
assert_equal osx-arm64 \
  "$(host_rid_fixture Darwin aarch64 AMD64 AMD64)" \
  'macOS uname architecture'

if host_rid_fixture MINGW64_NT-10.0 x86_64 IA64 AMD64 >/dev/null 2>&1; then
  fail 'unsupported Windows native architecture was accepted'
fi
if host_rid_fixture MINGW64_NT-10.0 arm64 '<unset>' '<unset>' >/dev/null 2>&1; then
  fail 'Windows architecture silently fell back to process uname'
fi

printf 'release host-RID tests passed (7/7)\n'
