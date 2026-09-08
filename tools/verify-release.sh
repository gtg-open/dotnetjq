#!/usr/bin/env bash
set -euo pipefail

# A host-injected preload is unrelated to this managed release and can write
# diagnostics into generator stderr, which is deliberately required to stay
# empty. Release verification runs in the ordinary managed process boundary.
unset LD_PRELOAD

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"
python3 "$repository_root/tools/release/verify-packaging-text-inputs.py"
bash "$repository_root/tools/release/test-release-workflow.sh"
bash "$repository_root/tools/release/test-nuget-reproducibility.sh"
python3 -B "$repository_root/tools/release/test-nuget-publication.py"
bash "$repository_root/tools/release/test-assert-release-tag.sh"
bash "$repository_root/tools/release/test-generate-release-notes.sh"
python3 -B "$repository_root/tools/release/test-github-release-probe.py"
python3 -B "$repository_root/tools/release/test-winget-pull-request-probe.py"
python3 -B "$repository_root/tools/release/test-winget-manifest-canonicalization.py"
python3 -B "$repository_root/tools/release/test-homebrew-formula-classifier.py"
upstream_root=${DOTNETJQ_UPSTREAM:-"$repository_root/upstream/jq"}
jq_commit=34f7186b86743a083a589741b6cea95293524108
oniguruma_commit=4ef89209a239c1aea328cf13c05a2807e5c146d1
oracle_sha256=b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f
modules_tree=457673594ed5c117efba91acbd87931fb9bf309e
torture_tree=b7ee9eb5804f1cade2d0321af1566d807c165791

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

canonical_path() {
  if command -v realpath >/dev/null 2>&1; then
    realpath "$1"
  elif readlink -f "$1" >/dev/null 2>&1; then
    readlink -f "$1"
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$1"
  else
    fail "realpath, GNU readlink, or python3 is required to resolve paths"
  fi
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    fail "sha256sum or shasum is required"
  fi
}

# Use an explicitly configured oracle when supplied. For local development,
# discover the official release binary next to the pinned source checkout or
# on PATH. The hash check below rejects a different build that merely reports
# the same version string.
oracle_path=${DOTNETJQ_ORACLE:-${DOTNETJQ_JQ182:-}}
if [[ -z "$oracle_path" ]]; then
  for candidate in \
    "$repository_root/artifacts/test-assets/jq-1.8.2/oracle/jq" \
    "$(dirname "$upstream_root")/jq-oracle-1.8.2/jq" \
    "$repository_root/artifacts/oracle/jq"; do
    if [[ -x "$candidate" ]]; then
      oracle_path=$candidate
      break
    fi
  done
fi
if [[ -z "$oracle_path" ]]; then
  oracle_path=$(command -v jq || true)
fi
[[ -n "$oracle_path" && -x "$oracle_path" ]] || \
  fail "set DOTNETJQ_ORACLE to the official jq 1.8.2 executable"
oracle_path=$(canonical_path "$oracle_path")
actual_oracle_sha256=$(sha256_file "$oracle_path")
test "$actual_oracle_sha256" = "$oracle_sha256" || \
  fail "jq oracle SHA-256 $actual_oracle_sha256 does not match $oracle_sha256"
actual_oracle_version=$("$oracle_path" --version)
test "$actual_oracle_version" = jq-1.8.2 || \
  fail "jq oracle version $actual_oracle_version does not match jq-1.8.2"
export DOTNETJQ_ORACLE="$oracle_path"
export DOTNETJQ_JQ182="$oracle_path"
export DOTNETJQ_REQUIRE_FULL_COMPATIBILITY=1

release_test_results=$(mktemp -d "${TMPDIR:-/tmp}/dotnetjq-release-tests.XXXXXXXX")
cleanup_release_test_results() {
  case $release_test_results in
    "${TMPDIR:-/tmp}"/dotnetjq-release-tests.*) rm -rf -- "$release_test_results" ;;
    *) fail "refusing unsafe release-test cleanup: $release_test_results" ;;
  esac
}
trap cleanup_release_test_results EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

test -e "$upstream_root/.git" || fail "upstream jq checkout is not a Git worktree: $upstream_root"
test -e "$upstream_root/vendor/oniguruma/.git" || \
  fail "pinned Oniguruma submodule is not initialized: $upstream_root/vendor/oniguruma"

release_upstream=$(canonical_path "$upstream_root")
# The manifest generator uses this same explicit checkout and records only its
# portable logical location; all pinned source/hash checks remain below.
export DOTNETJQ_UPSTREAM="$release_upstream"

actual_jq_commit=$(git -C "$upstream_root" rev-parse HEAD)
test "$actual_jq_commit" = "$jq_commit" || \
  fail "upstream jq HEAD $actual_jq_commit does not match pinned commit $jq_commit"

upstream_status=$(git -C "$upstream_root" status \
  --porcelain=v1 --untracked-files=all --ignore-submodules=none)
test -z "$upstream_status" || \
  fail "upstream jq checkout must be exactly clean for release verification"

actual_oniguruma_gitlink=$(git -C "$upstream_root" ls-tree HEAD -- vendor/oniguruma | awk '{print $3}')
test "$actual_oniguruma_gitlink" = "$oniguruma_commit" || \
  fail "jq vendor/oniguruma gitlink $actual_oniguruma_gitlink does not match $oniguruma_commit"

actual_oniguruma_commit=$(git -C "$upstream_root/vendor/oniguruma" rev-parse HEAD)
test "$actual_oniguruma_commit" = "$oniguruma_commit" || \
  fail "Oniguruma checkout HEAD $actual_oniguruma_commit does not match $oniguruma_commit"

oniguruma_status=$(git -C "$upstream_root/vendor/oniguruma" status \
  --porcelain=v1 --untracked-files=all)
test -z "$oniguruma_status" || \
  fail "Oniguruma checkout must be exactly clean for release verification"

oniguruma_license="$repository_root/THIRD_PARTY_LICENSES/Oniguruma-License.txt"
test -f "$oniguruma_license" || \
  fail "packaged Oniguruma license is missing: $oniguruma_license"
cmp -s "$upstream_root/vendor/oniguruma/COPYING" "$oniguruma_license" || \
  fail "packaged Oniguruma license is not byte-identical to pinned vendor/oniguruma/COPYING"

actual_modules_tree=$(git -C "$upstream_root" rev-parse HEAD:tests/modules)
test "$actual_modules_tree" = "$modules_tree" || \
  fail "module fixture tree $actual_modules_tree does not match $modules_tree"
actual_module_count=$(git -C "$upstream_root" ls-tree -r --name-only HEAD tests/modules | wc -l)
test "$actual_module_count" -eq 19 || fail "module fixture tree must contain exactly 19 files"

actual_torture_tree=$(git -C "$upstream_root" rev-parse HEAD:tests/torture)
test "$actual_torture_tree" = "$torture_tree" || \
  fail "torture fixture tree $actual_torture_tree does not match $torture_tree"
actual_torture_count=$(git -C "$upstream_root" ls-tree -r --name-only HEAD tests/torture | wc -l)
test "$actual_torture_count" -eq 1 || fail "torture fixture tree must contain exactly one file"

dotnet restore "$repository_root/DotNetJq.sln" -p:NuGetAudit=false
dotnet tool restore
"$repository_root/tools/parser-gen/generate-gplex-lexer.sh" --check
dotnet run --configuration Release \
  --project "$repository_root/tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj" -- \
  self-test --gppg dotnet-gppg
dotnet run --configuration Release \
  --project "$repository_root/tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj" -- \
  validate \
  --parser "$repository_root/src/DotNetJq/Grammar/parser.y" \
  --lexer "$repository_root/src/DotNetJq/Grammar/lexer.l"
dotnet run --configuration Release \
  --project "$repository_root/tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj" -- \
  check-generated-parser \
  --parser "$repository_root/src/DotNetJq/Grammar/parser.y" \
  --output "$repository_root/src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs"
python3 "$repository_root/tools/parser-gen/prototypes/parser/validate.py" --audit-upstream
python3 "$repository_root/tools/parser-gen/generate.py" \
  --check --upstream "$upstream_root"
python3 "$repository_root/tools/generate_manifest.py" --check --release
bash "$repository_root/tools/release/test-host-rid.sh"
perl "$repository_root/tools/DotNetJq.DifferentialProbe/verify-oniguruma-egcb-data.pl" \
  --check \
  "$upstream_root/vendor/oniguruma/src/unicode_egcb_data.c" \
  "$repository_root/src/DotNetJq/Compatibility/Regex/OnigurumaExtendedGraphemeData.cs"
perl "$repository_root/tools/DotNetJq.DifferentialProbe/generate-oniguruma-simple-case-fold-data.pl" \
  --check \
  "$upstream_root/vendor/oniguruma/src/unicode_fold_data.c" \
  "$repository_root/src/DotNetJq/Compatibility/Regex/OnigurumaSimpleCaseFoldData.cs"
python3 "$repository_root/tools/generate_fdlibm_elementary_corpus.py" \
  --check --oracle "$oracle_path"
perl "$repository_root/tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl" \
  "$upstream_root/vendor/oniguruma/src/unicode_property_data.c" \
  "$upstream_root/vendor/oniguruma/src/unicode_property_data_posix.c" \
  "$repository_root/src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs" \
  "$repository_root/src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs" \
  "$repository_root/src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs"
perl "$repository_root/tools/DotNetJq.DifferentialProbe/generate-regex-unicode-property-corpus.pl" \
  --check "$repository_root/tools/DotNetJq.DifferentialProbe/RegexUnicodePropertyCorpus.cs" \
  "$upstream_root/vendor/oniguruma/src/unicode_property_data.c" \
  "$upstream_root/vendor/oniguruma/src/unicode_property_data_posix.c"

forbidden_boundary_pattern='System\.Diagnostics\.Process|\bnew[[:space:]]+Process\b|\bProcess\.Start\b|\bProcessStartInfo\b|jq-oracle|/(home|Users)/[^/[:space:]]+/'
if rg -n "$forbidden_boundary_pattern" \
  "$repository_root/src/DotNetJq" \
  "$repository_root/src/DotNetJq.GlibcCompat" \
  "$repository_root/src/DotNetJq.Cli"; then
  printf 'production source contains a forbidden process/oracle/source-tree reference\n' >&2
  exit 1
elif [[ $? -ne 1 ]]; then
  fail "could not scan the production source boundary"
fi

# The CLI has narrow platform stdio/TTY imports. Native interop remains forbidden
# in the reusable managed compatibility assemblies themselves.
forbidden_library_interop_pattern='DllImport|LibraryImport|NativeLibrary'
if rg -n "$forbidden_library_interop_pattern" \
  "$repository_root/src/DotNetJq" \
  "$repository_root/src/DotNetJq.GlibcCompat"; then
  printf 'managed library source contains a forbidden native-interop reference\n' >&2
  exit 1
elif [[ $? -ne 1 ]]; then
  fail "could not scan the managed library native-interop boundary"
fi

"$repository_root/tools/verify-compatibility.sh"
DOTNETJQ_CLI="$repository_root/src/DotNetJq.Cli/bin/Release/net10.0/DotNetJq.Cli.dll" \
dotnet test "$repository_root/tests/DotNetJq.Cli.Tests/DotNetJq.Cli.Tests.csproj" \
  --configuration Release --no-build --no-restore \
  --logger 'console;verbosity=minimal' \
  --logger 'trx;LogFileName=cli-release.trx' \
  --results-directory "$release_test_results"
python3 "$repository_root/tools/release/verify-trx-no-skips.py" \
  "$release_test_results/cli-release.trx"
"$repository_root/tools/verify-cli-compatibility.sh"
"$repository_root/tools/DotNetJq.CompatibilityRunner/verify-isolation.sh"

DOTNETJQ_PERFORMANCE_UPSTREAM="$upstream_root" \
  "$repository_root/tools/performance/run.sh" \
  --correctness-only \
  --native-executable "$oracle_path" \
  --upstream "$upstream_root"

"$repository_root/tools/native-aot-smoke/verify.sh"
"$repository_root/tools/aot-compliance/verify-relink.sh" \
  --version 1.0.0
"$repository_root/tools/release/verify-local-tool-package.sh" \
  --version 1.0.0 --package-id dotnetjq \
  --authors 'DotNetJq release verification' \
  --repository-url 'https://github.com/example/dotnetjq'

dotnet run \
  --configuration Release \
  --project "$repository_root/tools/DotNetJq.DifferentialProbe/DotNetJq.DifferentialProbe.csproj" \
  -- --seed 18082 --count 5040 --oracle "$oracle_path" \
  --report "$repository_root/porting/DIFFERENTIAL_PROBE_REPORT.md" --check-report

dotnet run \
  --configuration Release \
  --project "$repository_root/tools/DotNetJq.DifferentialProbe/DotNetJq.DifferentialProbe.csproj" \
  -- --corpus regex --oracle "$oracle_path" \
  --report "$repository_root/porting/REGEX_DIFFERENTIAL_PROBE_REPORT.md" --check-report

"$repository_root/tools/verify-isolated-package.sh" \
  --authors 'DotNetJq release verification' \
  --repository-url 'https://github.com/example/dotnetjq'

printf 'DotNetJq release verification passed\n'
