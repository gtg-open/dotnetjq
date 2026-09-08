#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

workflow="$release_repository_root/.github/workflows/release-cli.yml"
[ -f "$workflow" ] || release_die "release workflow is missing: $workflow"
release_require_command awk
release_require_command grep

install_count=$(awk '/dotnet tool install/ { count++ } END { print count + 0 }' "$workflow")
cache_counts=$(awk '
  /dotnet tool install/ { in_install = 1 }
  in_install && /(^|[[:space:]])--no-http-cache([[:space:]\\]|$)/ { supported++ }
  in_install && /(^|[[:space:]])--no-cache([[:space:]\\]|$)/ { invalid++ }
  in_install && $0 !~ /\\[[:space:]]*$/ { in_install = 0 }
  END { print supported + 0, invalid + 0 }
' "$workflow")
set -- $cache_counts
http_cache_count=$1
invalid_cache_count=$2

[ "$install_count" -eq 1 ] ||
  release_die "release workflow must contain exactly one direct dotnet tool install; found $install_count"
[ "$http_cache_count" -eq "$install_count" ] ||
  release_die 'every direct dotnet tool install must use the SDK-supported --no-http-cache option'
[ "$invalid_cache_count" -eq 0 ] ||
  release_die 'release workflow uses unsupported dotnet tool install option --no-cache'

local_tool_verifier="$release_script_dir/verify-local-tool-package.sh"
if ! awk '
  index($0, "pwsh -NoLogo -NoProfile -NonInteractive -File") {
    pwsh_count++
    pwsh_line = NR
  }
  index($0, "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass") {
    windows_powershell_count++
    windows_powershell_line = NR
  }
  index($0, "sh \"$release_repository_root/tests/cli-shell/verify.sh\"") {
    posix_shell_count++
    posix_shell_line = NR
  }
  index($0, "NUGET_PACKAGES=\"$install_cache\" DOTNET_CLI_HOME=\"$install_home\"") {
    environment_line = NR
  }
  index($0, "dotnet tool uninstall \"$package_id\" --tool-path \"$tool_path\"") {
    uninstall_count++
    uninstall_line = NR
    uninstall_environment_line = environment_line
  }
  index($0, "[ ! -e \"$executable\" ] && [ ! -L \"$executable\" ]") {
    launcher_count++
    launcher_line = NR
  }
  index($0, "[ -d \"$tool_store\" ] && [ ! -L \"$tool_store\" ]") {
    store_type_count++
    store_type_line = NR
  }
  index($0, "residual_store_payload=$(find \"$tool_store\" -mindepth 1 ! -type d -print -quit)") {
    store_inventory_count++
    store_inventory_line = NR
  }
  index($0, "[ -z \"$residual_store_payload\" ] ||") {
    empty_store_count++
    empty_store_line = NR
  }
  END {
    valid = pwsh_count == 1 &&
      windows_powershell_count == 1 &&
      posix_shell_count == 1 &&
      uninstall_count == 1 &&
      launcher_count == 1 &&
      store_type_count == 1 &&
      store_inventory_count == 1 &&
      empty_store_count == 1 &&
      pwsh_line < uninstall_line &&
      windows_powershell_line < uninstall_line &&
      posix_shell_line < uninstall_line &&
      uninstall_environment_line + 1 == uninstall_line &&
      uninstall_line < launcher_line &&
      launcher_line < store_type_line &&
      store_type_line < store_inventory_line &&
      store_inventory_line < empty_store_line
    exit(valid ? 0 : 1)
  }
' "$local_tool_verifier"; then
  release_die 'local-tool verifier must pin the isolated install/run/uninstall lifecycle and postconditions'
fi

release_verifier="$release_repository_root/tools/verify-release.sh"
if ! awk '
  index($0, "tools/verify-cli-compatibility.sh") {
    cli_count++
    cli_line = NR
  }
  index($0, "DOTNETJQ_PERFORMANCE_UPSTREAM=\"$upstream_root\"") {
    performance_upstream_count++
  }
  index($0, "tools/performance/run.sh") {
    correctness_runner_count++
    correctness_runner_line = NR
    in_correctness_gate = 1
  }
  index($0, "--correctness-only") { correctness_option_count++ }
  index($0, "--native-executable \"$oracle_path\"") { oracle_option_count++ }
  in_correctness_gate && index($0, "--upstream \"$upstream_root\"") {
    upstream_option_count++
  }
  index($0, "tools/native-aot-smoke/verify.sh") {
    in_correctness_gate = 0
    aot_smoke_count++
    aot_smoke_line = NR
  }
  in_correctness_gate && /--(fixture|limit|output-directory|repetitions|warmups|startup-repetitions|seed)(=|[[:space:]])/ {
    forbidden_correctness_option_count++
  }
  END {
    valid = cli_count == 1 &&
      performance_upstream_count == 1 &&
      correctness_runner_count == 1 &&
      correctness_option_count == 1 &&
      oracle_option_count == 1 &&
      upstream_option_count == 1 &&
      aot_smoke_count == 1 &&
      forbidden_correctness_option_count == 0 &&
      cli_line < correctness_runner_line &&
      correctness_runner_line < aot_smoke_line
    exit(valid ? 0 : 1)
  }
' "$release_verifier"; then
  release_die 'release verifier must run the full no-timing framework/NativeAOT correctness gate'
fi

portable_package_chain=(
  "$release_script_dir/common.sh"
  "$release_script_dir/resolve-nativeaot-runtime-licenses.sh"
  "$local_tool_verifier"
  "$release_script_dir/verify-native-archive.sh"
  "$release_script_dir/verify-nuget-package-set.sh"
  "$release_repository_root/tests/cli-shell/verify.sh"
)
if grep -En -- \
  '(^|[[:space:]])(mapfile|readarray)([[:space:]]|$)|-printf|\$\{[^}]*(,,|\^\^)[^}]*\}|(^|[[:space:]])(declare[[:space:]]+-A|local[[:space:]]+-n)([[:space:]]|$)|=[[:space:]]*\(\)|install_rid_arguments' \
  "${portable_package_chain[@]}"; then
  release_die 'macOS release-package chain contains a non-portable Bash/find construct'
elif [ "$?" -ne 1 ]; then
  release_die 'could not scan the macOS release-package chain for non-portable constructs'
fi

job_contains() {
  local job=$1
  local pattern=$2
  awk -v job="  $job:" -v pattern="$pattern" '
    $0 == job { in_job = 1; next }
    in_job && /^  [A-Za-z0-9_-]+:/ { exit(found ? 0 : 1) }
    in_job && index($0, pattern) { found = 1 }
    END {
      if (!in_job) exit 1
      exit(found ? 0 : 1)
    }
  ' "$workflow"
}

[ "$(grep -Fc '  validate-tag:' "$workflow")" -eq 1 ] ||
  release_die 'release workflow must have exactly one validate-tag job'
job_contains validate-tag 'tools/release/assert-release-tag.sh' ||
  release_die 'validate-tag must run the remote tag/ancestry assertion'
job_contains performance-regression 'tools/performance/run.sh' ||
  release_die 'performance regression must time all official fixture scenarios'
job_contains performance-regression 'tools/performance/macro_benchmark.py' ||
  release_die 'performance regression must time all sustained workloads'
job_contains performance-regression 'tools/performance/check_release_gate.py' ||
  release_die 'performance regression must enforce the versioned threshold policy'
job_contains publication-preflight '- performance-regression' ||
  release_die 'publication preflight must depend on the timed performance gate'
job_contains publication-preflight '- windows-installed-package' ||
  release_die 'publication preflight must depend on both Windows installed-package matrix results'
job_contains publication-preflight 'tools/release/verify-release-bundle.sh' ||
  release_die 'publication preflight must redownload and verify the complete bundle'
job_contains attest '- publication-preflight' ||
  release_die 'attestation must follow publication preflight'
job_contains stage-github-release '- attest' ||
  release_die 'the draft GitHub release must follow attestation'
job_contains publish-nuget '- stage-github-release' ||
  release_die 'NuGet publication must follow successful draft staging'
job_contains publish-github-release '- publish-nuget' ||
  release_die 'the GitHub release must remain a draft until NuGet succeeds'
job_contains stage-github-release 'tools/release/verify-github-release-assets.py' ||
  release_die 'the staged GitHub release must be verified byte-for-byte before NuGet publication'
job_contains publish-github-release 'tools/release/verify-github-release-assets.py' ||
  release_die 'the GitHub release must be reverified before and after publication'
job_contains native-musl '--env DOTNETJQ_BUILD_NUMBER' ||
  release_die 'musl builds must inherit the release build number'
job_contains native-musl '--env DOTNETJQ_SOURCE_REVISION' ||
  release_die 'musl builds must inherit the release source revision'
job_contains homebrew "if: needs.validate-tag.outputs.prerelease == 'false'" ||
  release_die 'Homebrew mutation must be stable-release-only'
job_contains winget "if: needs.validate-tag.outputs.prerelease == 'false'" ||
  release_die 'WinGet mutation must be stable-release-only'

while IFS= read -r action_reference; do
  case $action_reference in
    ./*) ;;
    *)
      [[ $action_reference =~ ^[^@[:space:]]+@[0-9a-f]{40}$ ]] ||
        release_die "workflow action is not pinned to a full commit SHA: $action_reference"
      ;;
  esac
done < <(sed -n 's/^[[:space:]]*\(- \)\{0,1\}uses:[[:space:]]*\([^[:space:]#]*\).*/\2/p' \
  "$release_repository_root"/.github/workflows/*.yml)

if grep -En '(^|[[:space:]])gh[[:space:]]' "$workflow"; then
  release_die 'release workflow must not invoke an ambient GitHub CLI'
elif [ "$?" -ne 1 ]; then
  release_die 'could not scan the release workflow for ambient GitHub CLI calls'
fi

if grep -q -- '--skip-duplicate' "$release_script_dir/publish-nuget-plan.sh"; then
  release_die 'NuGet publication must fail closed when a package version already exists'
fi

printf 'release workflow static tests passed (25/25)\n'
