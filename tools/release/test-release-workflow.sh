#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

workflow="$release_repository_root/.github/workflows/release-cli.yml"
[ -f "$workflow" ] || release_die "release workflow is missing: $workflow"
semantic_workflow="$release_repository_root/.github/workflows/semantic-compatibility.yml"
[ -f "$semantic_workflow" ] ||
  release_die "semantic workflow is missing: $semantic_workflow"
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

grep -Fq \
  'subject="$PWD/src/DotNetJq.Cli/bin/Release/net10.0/DotNetJq.Cli.dll"' \
  "$semantic_workflow" ||
  release_die 'semantic post-gate CLI tests must select the managed DLL that survives package verification'

local_tool_verifier="$release_script_dir/verify-local-tool-package.sh"
bash "$release_script_dir/test-package-host-prerequisites.sh"
grep -Fq 'release_require_native_dotnet_sdk "$rid"' "$local_tool_verifier" ||
  release_die 'local-only tool installation must require a matching native SDK'
grep -Fq -- '--framework net10.0' "$local_tool_verifier" ||
  release_die 'local-only native tool installation must not treat framework any as an old managed target'
if awk '!/^[[:space:]]*#/ && /--arch/ { found = 1 } END { exit(found ? 0 : 1) }' "$local_tool_verifier"; then
  release_die 'local-only tool installation must not request a downloadable SDK apphost with --arch'
fi
grep -Fq 'pointer_any_payload=$(release_pointer_any_payload <"$pointer_entries")' \
  "$release_script_dir/verify-nuget-package-set.sh" ||
  release_die 'pointer verification must use the tested portable inventory filter'
grep -Fq 'verify-windows-tool-shim.py' "$local_tool_verifier" ||
  release_die 'Windows native installation must bind the SDK cmd shim to its exact package payload'
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

job_contains_in_order() {
  local job=$1
  local first=$2
  local second=$3
  awk -v job="  $job:" -v first="$first" -v second="$second" '
    $0 == job { in_job = 1; next }
    in_job && /^  [A-Za-z0-9_-]+:/ { exit(found_first && found_second ? 0 : 1) }
    in_job && !found_first && index($0, first) { found_first = 1; next }
    in_job && found_first && index($0, second) { found_second = 1 }
    END {
      if (!in_job) exit 1
      exit(found_first && found_second ? 0 : 1)
    }
  ' "$workflow"
}

if grep -Fq '  publish-core:' "$workflow"; then
  core_publish_job=publish-core
elif grep -Fq '  publish-nuget:' "$workflow"; then
  core_publish_job=publish-nuget
else
  release_die 'release workflow must define publish-core or publish-nuget'
fi

[ "$(grep -Fc '  validate-tag:' "$workflow")" -eq 1 ] ||
  release_die 'release workflow must have exactly one validate-tag job'
job_contains validate-tag 'tools/release/assert-release-tag.sh' ||
  release_die 'validate-tag must run the remote tag/ancestry assertion'
# Bind every source checkout to the event SHA. The implicit tag+SHA pair in
# actions/checkout can overwrite an annotated local tag with a commit ref.
# The separate Homebrew repository intentionally follows its own default branch.
awk '
  function finish_checkout() {
    if (checkout && !external_repository) {
      source_checkouts++
      if (!pinned_sha) invalid++
    }
    checkout = external_repository = pinned_sha = 0
  }
  /^      - / { finish_checkout() }
  /^      - uses: actions\/checkout@/ { checkout = 1 }
  checkout && /^          repository:/ { external_repository = 1 }
  checkout && $0 == "          ref: ${{ github.sha }}" { pinned_sha = 1 }
  END {
    finish_checkout()
    exit(source_checkouts > 0 && invalid == 0 ? 0 : 1)
  }
' "$workflow" ||
  release_die 'every release source checkout must explicitly pin ref to github.sha'
grep -Fxq '          ref: ${{ github.sha }}' "$semantic_workflow" ||
  release_die 'the reusable semantic workflow must also pin its source checkout to github.sha'
job_contains performance-regression 'tools/performance/run.sh' ||
  release_die 'performance regression must time all official fixture scenarios'
job_contains performance-regression '- native' ||
  release_die 'performance regression must consume the canonical native producer job'
job_contains performance-regression 'name: release-input-linux-x64' ||
  release_die 'performance regression must download the exact linux-x64 producer artifact'
job_contains performance-regression '--external-aot-provenance "$DOTNETJQ_PERF_PROVENANCE"' ||
  release_die 'performance regression must verify producer provenance for its NativeAOT subject'
job_contains native '--performance-provenance' ||
  release_die 'the canonical linux-x64 release builder must emit performance provenance'
[ "$(grep -Fc -- '--artifacts-path "$work_dir/build"' "$release_script_dir/build-native-archive.sh")" -eq 1 ] ||
  release_die 'the native archive builder must isolate its build graph from later verification restores'
[ "$(grep -Fc -- 'performance provenance must be the canonical archive sidecar' "$release_script_dir/build-native-archive.sh")" -eq 1 ] ||
  release_die 'performance provenance output must be constrained to its canonical archive sidecar'
[ "$(grep -Fc -- '"$candidate_performance_provenance" "$performance_provenance"' "$release_script_dir/build-native-archive.sh")" -eq 1 ] ||
  release_die 'performance provenance must use new-or-identical publication semantics'
job_contains performance-regression 'tools/performance/macro_benchmark.py' ||
  release_die 'performance regression must time all sustained workloads'
job_contains performance-regression 'tools/performance/check_release_gate.py' ||
  release_die 'performance regression must enforce the versioned threshold policy'
job_contains performance-regression '--startup-repetitions 300' ||
  release_die 'release startup measurements must retain the 300-sample noise check'
job_contains performance-regression 'tools/performance/paired_baseline.py --build' ||
  release_die 'performance baseline must be freshly built in the measurement job'
[ "$(sed -n '/^  performance-regression:/,/^  native:/p' "$workflow" | grep -Fc -- '--baseline-root "$PWD/artifacts/performance-baseline"')" -eq 2 ] ||
  release_die 'both full benchmark drivers must interleave the pinned baseline'
job_contains performance-regression 'ref: 0ceaaaadc5582dac77491de183b6a8f1104adbf8' ||
  release_die 'performance baseline checkout must retain the reviewed portable initial source commit'
performance_evidence_retains_certification() {
  awk -v artifact="$2" '
    /^          name:/ { active = ($2 == artifact); if (active) count++ }
    active && /^          include-hidden-files: true$/ { hidden++ }
    active && /^          path: \|$/ { in_paths = 1; next }
    in_paths && /^            / {
      paths++
      if ($1 == "artifacts/performance/results") fixtures++
      if ($1 == "artifacts/performance/macro-results") macros++
      next
    }
    { in_paths = 0 }
    /^      - / { active = 0 }
    END { exit !(count == 1 && hidden == 1 && paths == 2 && fixtures == 1 && macros == 1) }
  ' "$1"
}
performance_evidence_retains_certification "$workflow" release-performance-evidence ||
  release_die 'release evidence must retain certification markers and upload only the two report directories'
performance_evidence_retains_certification \
  "$release_repository_root/.github/workflows/performance-pair-check.yml" same-runner-performance-evidence ||
  release_die 'PR/main performance evidence must retain certification markers and upload only the two report directories'
job_contains publication-preflight '- performance-regression' ||
  release_die 'publication preflight must depend on the timed performance gate'
job_contains publication-preflight '- installed-package' ||
  release_die 'publication preflight must depend on the complete installed-package matrix'
for release_rid in \
  win-x64 win-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 \
  osx-x64 osx-arm64; do
  job_contains installed-package "- rid: $release_rid" ||
    release_die "installed-package matrix does not cover $release_rid"
done
job_contains publication-preflight 'tools/release/verify-release-bundle.sh' ||
  release_die 'publication preflight must redownload and verify the complete bundle'
job_contains attest '- publication-preflight' ||
  release_die 'attestation must follow publication preflight'
job_contains stage-github-release '- attest' ||
  release_die 'the draft GitHub release must follow attestation'
job_contains "$core_publish_job" '- stage-github-release' ||
  release_die 'NuGet publication must follow successful draft staging'
job_contains "$core_publish_job" 'environment: release' ||
  release_die 'the core publication job must use the protected release environment'
job_contains "$core_publish_job" 'id-token: write' ||
  release_die 'the core publication job must request GitHub OIDC tokens'
job_contains "$core_publish_job" 'NuGet/login@' ||
  release_die 'the core publication job must exchange GitHub OIDC for a short-lived NuGet key'
job_contains "$core_publish_job" 'user: ${{ vars.NUGET_USER }}' ||
  release_die 'NuGet OIDC login must use the configured individual NuGet username'
job_contains "$core_publish_job" '--expected-owner "$NUGET_OWNER"' ||
  release_die 'NuGet resume verification must bind repository signatures to the configured package owner'
job_contains "$core_publish_job" 'scopes != {"public_repo"}' ||
  release_die 'a configured WinGet token must reject every scope set except public_repo'
job_contains "$core_publish_job" 'homebrew_configured: ${{ steps.optional-publishers.outputs.homebrew_configured }}' ||
  release_die 'core publication must expose whether optional Homebrew submission is configured'
job_contains "$core_publish_job" 'winget_configured: ${{ steps.optional-publishers.outputs.winget_configured }}' ||
  release_die 'core publication must expose whether optional WinGet submission is configured'
job_contains "$core_publish_job" "steps.optional-publishers.outputs.homebrew_configured == 'true'" ||
  release_die 'Homebrew credentials must be validated only when fully configured'
job_contains "$core_publish_job" "steps.optional-publishers.outputs.winget_configured == 'true'" ||
  release_die 'WinGet credentials must be validated only when configured'
if [ "$core_publish_job" = publish-core ]; then
  job_contains_in_order publish-core 'tools/release/publish-nuget-plan.sh' 'draft: false' ||
    release_die 'the merged core publication job must publish NuGet before making the GitHub release public'
  github_release_publish_job=publish-core
else
  job_contains publish-github-release '- publish-nuget' ||
    release_die 'the GitHub release must remain a draft until NuGet succeeds'
  github_release_publish_job=publish-github-release
fi
job_contains stage-github-release 'tools/release/verify-github-release-assets.py' ||
  release_die 'the staged GitHub release must be verified byte-for-byte before NuGet publication'
[ "$(grep -Fhc 'official = api_url == "https://api.github.com"' "$release_script_dir/probe-github-release.py" "$release_script_dir/probe-winget-pull-request.py" | awk '{ total += $1 } END { print total + 0 }')" -eq 2 ] ||
  release_die 'GitHub probes must send tokens only to the official production API origin'
[ "$(grep -Fhc 'DOTNETJQ_RELEASE_TEST_MODE' "$release_script_dir/probe-github-release.py" "$release_script_dir/probe-winget-pull-request.py" | awk '{ total += $1 } END { print total + 0 }')" -eq 2 ] ||
  release_die 'loopback GitHub fixtures must require explicit release test mode'
job_contains_in_order stage-github-release 'tools/release/probe-github-release.py' 'softprops/action-gh-release@' ||
  release_die 'draft staging must classify an existing release before attempting creation'
job_contains_in_order stage-github-release 'tools/release/generate-release-notes.sh' 'tools/release/probe-github-release.py' ||
  release_die 'draft staging must generate the deterministic body before release classification'
job_contains stage-github-release "steps.release-state.outputs.state == 'missing' ||" ||
  release_die 'draft upload must run when no release already exists'
job_contains stage-github-release "steps.release-state.outputs.state == 'partial-draft'" ||
  release_die 'draft upload must safely resume an exact partial draft'
job_contains "$core_publish_job" 'tools/release/probe-github-release.py' ||
  release_die 'the protected publication boundary must reclassify the GitHub release'
job_contains_in_order "$core_publish_job" 'tools/release/generate-release-notes.sh' 'tools/release/probe-github-release.py' ||
  release_die 'the protected boundary must regenerate the deterministic body before classification'
job_contains "$core_publish_job" "steps.protected-release-state.outputs.state != 'exact-draft' &&" ||
  release_die 'core publication must reject an incomplete draft before NuGet mutation'
job_contains "$core_publish_job" "steps.protected-release-state.outputs.state != 'published'" ||
  release_die 'core publication may resume only from an exact published release'
job_contains "$core_publish_job" "if: steps.protected-release-state.outputs.state == 'exact-draft'" ||
  release_die 'the protected boundary must publish GitHub only from an exact draft'
job_contains "$github_release_publish_job" 'tools/release/verify-github-release-assets.py' ||
  release_die 'the GitHub release must be reverified before and after publication'
[ "$(grep -Fc 'generate_release_notes:' "$workflow")" -eq 0 ] ||
  release_die 'release notes must not depend on mutable GitHub-generated content'
[ "$(grep -Fc 'body_path: ${{ runner.temp }}/dotnetjq-release-notes.md' "$workflow")" -eq 2 ] ||
  release_die 'draft creation and publication must use the same deterministic release body'
[ "$(grep -Fc -- '--body-file "$RUNNER_TEMP/dotnetjq-release-notes.md"' "$workflow")" -eq 5 ] ||
  release_die 'every GitHub release classification and verification must compare the deterministic body'
job_contains "$core_publish_job" 'RELEASE_SETTINGS_GITHUB_TOKEN: ${{ secrets.RELEASE_SETTINGS_GITHUB_TOKEN }}' ||
  release_die 'the protected boundary must use a dedicated release-settings token'
job_contains "$core_publish_job" '/immutable-releases' ||
  release_die 'the protected boundary must query the repository immutable-release setting'
[ "$(grep -Fhc "published release" "$release_script_dir/probe-github-release.py" "$release_script_dir/verify-github-release-assets.py" | awk '{ total += $1 } END { print total + 0 }')" -ge 2 ] ||
  release_die 'release classification and verification must reject a mutable published release'
job_contains_in_order "$core_publish_job" 'Reject any incomplete staged release' 'Require GitHub release immutability' ||
  release_die 'immutable-release verification must follow exact draft classification'
job_contains_in_order "$core_publish_job" 'Require GitHub release immutability' 'id: optional-publishers' ||
  release_die 'external publication credentials must not be requested before immutability is verified'
job_contains native-musl '--env DOTNETJQ_BUILD_NUMBER' ||
  release_die 'musl builds must inherit the release build number'
job_contains native-musl '--env DOTNETJQ_SOURCE_REVISION' ||
  release_die 'musl builds must inherit the release source revision'
job_contains native 'tools/release/test-nuget-reproducibility.sh' ||
  release_die 'every native runner must prove cold package-build reproducibility'
job_contains native-musl 'tools/release/test-nuget-reproducibility.sh' ||
  release_die 'each musl runner must prove cold package-build reproducibility in Alpine'
job_contains homebrew "needs.validate-tag.outputs.prerelease == 'false' &&" ||
  release_die 'Homebrew mutation must be stable-release-only'
job_contains homebrew "needs.publish-core.outputs.superseded == 'false'" ||
  release_die 'Homebrew mutation must not downgrade a newer stable release'
job_contains homebrew "needs.publish-core.outputs.homebrew_configured == 'true'" ||
  release_die 'Homebrew mutation must be skipped when its optional publisher is not configured'
job_contains_in_order homebrew 'classify-homebrew-formula.py' 'cp artifacts/bundle/dotnetjq.rb' ||
  release_die 'Homebrew must classify the current tap formula before staging an update'
job_contains homebrew "if: startsWith(steps.homebrew-formula.outputs.state, 'publish:')" ||
  release_die 'Homebrew pull-request mutation must run only for a proven publish state'
job_contains winget "needs.validate-tag.outputs.prerelease == 'false' &&" ||
  release_die 'WinGet mutation must be stable-release-only'
job_contains winget "needs.publish-core.outputs.superseded == 'false'" ||
  release_die 'WinGet mutation must not resubmit an older stable release'
job_contains winget "needs.publish-core.outputs.winget_configured == 'true'" ||
  release_die 'WinGet mutation must be skipped when its optional publisher is not configured'
job_contains_in_order winget 'tools/release/probe-winget-pull-request.py' 'wingetcreate.exe submit' ||
  release_die 'WinGet must classify an exact existing pull request before submission'
job_contains_in_order winget 'wingetcreate.exe submit' '--pull-request-number' ||
  release_die 'WinGet must directly verify the exact pull request returned by submission'
[ "$(grep -Fc 'canonicalize-winget-manifest.py' "$release_script_dir/generate-winget-manifests.sh")" -eq 1 ] ||
  release_die 'generated WinGet bytes must match the pinned WingetCreate serialization'
job_contains winget "if: steps.winget-pr.outputs.state == 'missing'" ||
  release_die 'WinGet creation must run only when no exact pull request exists'
job_contains winget "if: steps.winget-pr.outputs.state == 'exact-existing'" ||
  release_die 'WinGet must report a byte-verified existing pull request on rerun'
job_contains winget "if: steps.winget-pr.outputs.state == 'exact-published'" ||
  release_die 'WinGet must report and skip a byte-verified published version on rerun'
job_contains "$core_publish_job" 'superseded: ${{ steps.protected-release-state.outputs.superseded }}' ||
  release_die 'core publication must expose the protected release supersession result'
job_contains "$github_release_publish_job" "steps.protected-release-state.outputs.superseded == 'false'" ||
  release_die 'an older stable release must never be marked Latest'

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

for packaging_workflow in "$workflow" "$release_repository_root/.github/workflows/cli-cross-platform.yml"; do
  [ "$(grep -Fc 'test-nuget-reproducibility.sh --rid '\''${{ matrix.rid }}'\' "$packaging_workflow")" -eq 2 ] ||
    release_die "native and musl cold-build checks must pin the matrix RID: $packaging_workflow"
  [ "$(grep -Fc 'path: artifacts/reproducibility/${{ matrix.rid }}' "$packaging_workflow")" -eq 2 ] ||
    release_die "native and musl cold-build failures must preserve evidence: $packaging_workflow"
  grep -Fq 'apk add --no-cache bash file icu-libs python3 tar unzip' "$packaging_workflow" ||
    release_die "musl cold-package installation requires full unzip: $packaging_workflow"
done
grep -Fq 'bash "$release_script_dir/verify-local-tool-package.sh"' \
  "$release_script_dir/test-nuget-reproducibility.sh" ||
  release_die 'cold package verification must exercise installation before discarding its packages'

job_contains performance-regression 'env DOTNETJQ_PERFORMANCE_UPSTREAM="$DOTNETJQ_UPSTREAM"' ||
  release_die 'benchmark self-tests must use the same provisioned upstream as the measured fixtures'
job_contains packages 'sudo apt-get install --yes binutils bubblewrap clang file ripgrep ruby unzip zip zlib1g-dev' ||
  release_die 'package isolation and relinking prerequisites must be installed explicitly'
job_contains installed-package 'apk add --no-cache bash file icu-libs python3 unzip' ||
  release_die 'Alpine installed-package checks must provision full unzip with -Z1 support'
job_contains publication-preflight 'global-json-file: global.json' ||
  release_die 'bundle preflight must provision the pinned SDK used for runtime-license resolution'
job_contains publication-preflight 'sudo apt-get install --yes file ruby unzip' ||
  release_die 'bundle preflight must install its native-format, metadata, and package readers'
job_contains "$core_publish_job" 'sudo apt-get install --yes file ruby unzip' ||
  release_die 'protected bundle reverification must install its verification prerequisites'

grep -Fq 'binutils bubblewrap clang gcc perl ripgrep ruby unzip zip zlib1g-dev' "$semantic_workflow" ||
  release_die 'semantic validation must install Ruby for canonical WinGet YAML integration tests'

printf 'release workflow static tests passed (108/108)\n'
