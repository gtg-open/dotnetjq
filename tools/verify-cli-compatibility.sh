#!/usr/bin/env bash
set -euo pipefail

# Execute jq's unchanged pinned shell drivers against the managed executable.
# The temporary git archive intentionally excludes native build products, so
# FILE*/LD_PRELOAD injection remains a native-harness concern rather than being
# mistaken for managed CLI behavior.
unset LD_PRELOAD

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
upstream_root="${DOTNETJQ_UPSTREAM:-$repository_root/upstream/jq}"
upstream_commit="34f7186b86743a083a589741b6cea95293524108"
shtest_sha="a991a539e32640df0a59c4f7273cffcd0be14f44d4d85ac683a37aafb9f0cbae"
utf8test_sha="c2ac29c59e6f3e4461b8c2b2ceac8c9438f1c32a34afba27a2d0808624fc6991"

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

for command_name in dotnet git realpath sha256sum tar; do
  command -v "$command_name" >/dev/null 2>&1 || \
    fail "CLI compatibility verification requires $command_name"
done

test -e "$upstream_root/.git" || fail "upstream jq checkout is unavailable: $upstream_root"
actual_commit="$(git -C "$upstream_root" rev-parse HEAD)"
test "$actual_commit" = "$upstream_commit" || \
  fail "upstream jq HEAD $actual_commit does not match pinned commit $upstream_commit"

actual_shtest_sha="$(sha256sum "$upstream_root/tests/shtest")"
actual_shtest_sha="${actual_shtest_sha%% *}"
test "$actual_shtest_sha" = "$shtest_sha" || fail "pinned tests/shtest hash mismatch"
actual_utf8test_sha="$(sha256sum "$upstream_root/tests/utf8test")"
actual_utf8test_sha="${actual_utf8test_sha%% *}"
test "$actual_utf8test_sha" = "$utf8test_sha" || fail "pinned tests/utf8test hash mismatch"

temporary_parent="$(realpath -- "${TMPDIR:-/tmp}")"
test -d "$temporary_parent" || fail "temporary parent is unavailable: $temporary_parent"
test "$temporary_parent" != / || fail "refusing unsafe temporary parent: $temporary_parent"
validation_root="$(mktemp -d -- "$temporary_parent/dotnetjq-cli-compat.XXXXXXXX")"
cleanup_validation_root() {
  local resolved_root
  resolved_root="$(realpath -m -- "$validation_root")"
  if [[ -d "$resolved_root" && "$resolved_root" == "$temporary_parent"/dotnetjq-cli-compat.* ]]; then
    rm -rf -- "$resolved_root"
  else
    printf 'refusing unsafe cleanup target: %s\n' "$resolved_root" >&2
    return 1
  fi
}
trap cleanup_validation_root EXIT

fixture_root="$validation_root/jq-$upstream_commit"
mkdir -p -- "$fixture_root" "$validation_root/bin"
git -C "$upstream_root" archive --format=tar "$upstream_commit" | tar -xf - -C "$fixture_root"

dotnet restore "$repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj" \
  -p:NuGetAudit=false --verbosity quiet
dotnet build "$repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj" \
  --configuration Release --no-restore --verbosity minimal

subject="$repository_root/src/DotNetJq.Cli/bin/Release/net10.0/DotNetJq.Cli"
test -x "$subject" || fail "managed CLI apphost was not produced: $subject"
subject="$(realpath -- "$subject")"

# tests/jq-f-test.sh deliberately resolves a command named jq from PATH. Keep
# that compatibility alias confined to this temporary fixture.
wrapper="$validation_root/bin/jq"
printf '%s\n' '#!/bin/sh' 'exec "$DOTNETJQ_CLI_SUBJECT" "$@"' > "$wrapper"
chmod 0755 "$wrapper"

export DOTNETJQ_CLI_SUBJECT="$subject"
export PATH="$validation_root/bin:$PATH"
export JQ="$subject"
unset ENABLE_VALGRIND TRACE_TESTS

run_driver() {
  local driver="$1"
  local log="$validation_root/${driver}.log"
  if ! (cd "$fixture_root" && "tests/$driver") >"$log" 2>&1; then
    printf 'unchanged jq driver failed: tests/%s\n' "$driver" >&2
    printf '%s\n' 'showing the final 400 trace lines:' >&2
    tail -n 400 "$log" >&2
    return 1
  fi
  printf 'unchanged jq driver passed: tests/%s\n' "$driver"
}

run_driver shtest
run_driver utf8test

printf 'DotNetJq CLI compatibility verification passed (jq %s)\n' "$upstream_commit"
