#!/usr/bin/env bash
set -euo pipefail

unset LD_PRELOAD

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
upstream_root=${DOTNETJQ_UPSTREAM:-"$repo_root/upstream/jq"}
runner="$repo_root/tools/DotNetJq.CompatibilityRunner/DotNetJq.CompatibilityRunner.csproj"

command -v python3 >/dev/null 2>&1 || {
  printf '%s\n' 'python3 is required to verify TRX test evidence' >&2
  exit 2
}
test_results_parent=${TMPDIR:-/tmp}
[[ "$test_results_parent" = /* && -d "$test_results_parent" ]] || {
  printf 'unsafe or missing temporary directory: %s\n' "$test_results_parent" >&2
  exit 2
}
test_results_parent=$(cd -- "$test_results_parent" && pwd -P)
[[ "$test_results_parent" != / ]] || {
  printf '%s\n' 'refusing to create compatibility results under the filesystem root' >&2
  exit 2
}
test_results=$(mktemp -d "$test_results_parent/dotnetjq-core-tests.XXXXXXXX")
cleanup_test_results() {
  if [[ -d "$test_results" &&
        "$test_results" == "$test_results_parent"/dotnetjq-core-tests.* ]]; then
    rm -rf -- "$test_results"
  else
    printf 'refusing unsafe compatibility-test cleanup: %s\n' "$test_results" >&2
    return 1
  fi
}
trap cleanup_test_results EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

dotnet build "$repo_root/DotNetJq.sln" --configuration Release \
  --no-restore -t:Rebuild -m:1 -nr:false
dotnet test "$repo_root/tests/DotNetJq.Tests/DotNetJq.Tests.csproj" \
  --configuration Release --no-build --no-restore \
  --logger 'console;verbosity=minimal' \
  --logger 'trx;LogFileName=core-compatibility.trx' \
  --results-directory "$test_results"
python3 "$repo_root/tools/release/verify-trx-no-skips.py" \
  "$test_results/core-compatibility.trx"

fixtures=(
  jq.test
  man.test
  onig.test
  manonig.test
  base64.test
  uri.test
  optional.test
)

for fixture in "${fixtures[@]}"; do
  dotnet run --configuration Release --project "$runner" --no-build -- \
    --fixture "$upstream_root/tests/$fixture"
done
