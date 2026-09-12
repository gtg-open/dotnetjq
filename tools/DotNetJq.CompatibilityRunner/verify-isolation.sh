#!/usr/bin/env bash
set -euo pipefail

unset LD_PRELOAD

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture_dir="$(mktemp -d)"
trap 'rm -rf -- "$fixture_dir"' EXIT
fixture="$fixture_dir/isolation.test"

printf '.\nnull\nnull\n\n.\nfalse\nfalse\n\n.\ntrue\ntrue\n' > "$fixture"

set +e
report="$({
  DOTNETJQ_COMPATIBILITY_RUNNER_SELFTEST_CRASH_INDEX=1 \
    dotnet run --configuration Release --project "$script_dir" \
      --no-restore -- "$fixture"
} 2>&1)"
status=$?
set -e

printf '%s\n' "$report"

if [[ $status -ne 1 ]]; then
  printf 'isolation verification failed: expected exit 1, got %s\n' "$status" >&2
  exit 1
fi

if ! grep -Fq 'FAIL case=2 line=5 phase=process-crash' <<< "$report"; then
  printf 'isolation verification failed: missing process-crash report\n' >&2
  exit 1
fi

if ! grep -Fq 'TOTAL fixture_cases=3 selected=3 passed=2 failed=1' <<< "$report"; then
  printf 'isolation verification failed: remaining cases were not reported\n' >&2
  exit 1
fi

printf 'isolation verification passed\n'
