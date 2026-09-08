#!/usr/bin/env bash
set -euo pipefail

unset LD_PRELOAD

if ! environment_names="$(compgen -e)"; then
  printf '%s\n' 'Unable to enumerate inherited environment variables.' >&2
  exit 1
fi
while IFS= read -r environment_name; do
  case "$environment_name" in
    DOTNET_ROOT|DOTNET_ROOT_X64|DOTNET_ROOT_X86|DOTNET_ROOT_ARM64|DOTNET_ROOT_ARM)
      ;;
    COMPlus_*|CORECLR_*|COREHOST_*|DOTNET_*|JQ_*|LD_*|MALLOC_*|GLIBC_TUNABLES)
      unset "$environment_name"
      ;;
  esac
done <<< "$environment_names"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
skip_build=0
limited_run=0
correctness_only_count=0
timing_option_seen=0
benchmark_arguments=()
report_output_directory="$repository_root/artifacts/performance/results"
report_output_option_seen=0
expect_report_output_value=0
external_framework_executable=
external_aot_executable=
external_aot_archive=
external_aot_provenance=
external_framework_seen=0
external_aot_seen=0
external_archive_seen=0
external_provenance_seen=0
expect_external_option=
ordinary_executable_option_seen=0
external_artifact_mode=0

for argument in "$@"; do
  if [[ -n "$expect_external_option" ]]; then
    if [[ -z "$argument" || "$argument" == --* ]]; then
      printf '%s\n' "$expect_external_option requires a non-empty path." >&2
      exit 1
    fi
    case "$expect_external_option" in
      --external-framework-executable) external_framework_executable="$argument" ;;
      --external-aot-executable) external_aot_executable="$argument" ;;
      --external-aot-archive) external_aot_archive="$argument" ;;
      --external-aot-provenance) external_aot_provenance="$argument" ;;
    esac
    expect_external_option=
    continue
  fi
  case "$argument" in
    --external-framework-executable)
      [[ "$external_framework_seen" -eq 0 ]] || { printf '%s\n' "$argument may be supplied only once." >&2; exit 1; }
      external_framework_seen=1; expect_external_option="$argument"; continue ;;
    --external-aot-executable)
      [[ "$external_aot_seen" -eq 0 ]] || { printf '%s\n' "$argument may be supplied only once." >&2; exit 1; }
      external_aot_seen=1; expect_external_option="$argument"; continue ;;
    --external-aot-archive)
      [[ "$external_archive_seen" -eq 0 ]] || { printf '%s\n' "$argument may be supplied only once." >&2; exit 1; }
      external_archive_seen=1; expect_external_option="$argument"; continue ;;
    --external-aot-provenance)
      [[ "$external_provenance_seen" -eq 0 ]] || { printf '%s\n' "$argument may be supplied only once." >&2; exit 1; }
      external_provenance_seen=1; expect_external_option="$argument"; continue ;;
  esac

  if [[ "$expect_report_output_value" -eq 1 ]]; then
    if [[ -z "$argument" || "$argument" == --* ]]; then
      printf '%s\n' '--output-directory requires a non-empty path.' >&2
      exit 1
    fi
    report_output_directory="$argument"
    expect_report_output_value=0
  else
    case "$argument" in
      --output-directory)
        if [[ "$report_output_option_seen" -eq 1 ]]; then
          printf '%s\n' '--output-directory may be supplied only once.' >&2
          exit 1
        fi
        report_output_option_seen=1
        expect_report_output_value=1
        ;;
      --output-directory=*)
        if [[ "$report_output_option_seen" -eq 1 ]]; then
          printf '%s\n' '--output-directory may be supplied only once.' >&2
          exit 1
        fi
        report_output_option_seen=1
        report_output_directory="${argument#--output-directory=}"
        if [[ -z "$report_output_directory" ]]; then
          printf '%s\n' '--output-directory requires a non-empty path.' >&2
          exit 1
        fi
        ;;
    esac
  fi

  if [[ "$argument" == --skip-build ]]; then
    skip_build=1
  elif [[ "$argument" == --correctness-only ]]; then
    correctness_only_count=$((correctness_only_count + 1))
    benchmark_arguments+=("$argument")
  elif [[ "$argument" == --publishable || "$argument" == --non-publishable ]]; then
    printf '%s\n' "$argument is controlled by run.sh and must not be supplied directly." >&2
    exit 1
  else
    benchmark_arguments+=("$argument")
    case "$argument" in
      --framework-executable|--framework-executable=*|--aot-executable|--aot-executable=*)
        ordinary_executable_option_seen=1
        ;;
      --fixture|--fixture=*|--limit|--limit=*)
        limited_run=1
        ;;
      --repetitions|--repetitions=*|--warmups|--warmups=*|--startup-repetitions|--startup-repetitions=*|--seed|--seed=*)
        timing_option_seen=1
        ;;
    esac
  fi
done

if [[ -n "$expect_external_option" ]]; then
  printf '%s\n' "$expect_external_option requires a non-empty path." >&2
  exit 1
fi
if [[ "$expect_report_output_value" -eq 1 ]]; then
  printf '%s\n' '--output-directory requires a non-empty path.' >&2
  exit 1
fi
external_option_count=$((external_framework_seen + external_aot_seen + external_archive_seen + external_provenance_seen))
if [[ "$external_option_count" -ne 0 ]]; then
  if [[ "$external_option_count" -ne 4 ]]; then
    printf '%s\n' 'external artifact mode requires framework executable and NativeAOT executable/archive/provenance.' >&2
    exit 1
  fi
  if [[ "$ordinary_executable_option_seen" -eq 1 || "$skip_build" -eq 1 ]]; then
    printf '%s\n' 'external artifact mode cannot be combined with ordinary executable options or --skip-build.' >&2
    exit 1
  fi
  external_artifact_mode=1
fi
if [[ "$correctness_only_count" -gt 1 ]]; then
  printf '%s\n' '--correctness-only may be supplied only once.' >&2
  exit 1
fi
if [[ "$correctness_only_count" -eq 1 ]]; then
  if [[ "$limited_run" -eq 1 ]]; then
    printf '%s\n' '--fixture and --limit cannot be used with --correctness-only.' >&2
    exit 1
  fi
  if [[ "$report_output_option_seen" -eq 1 ]]; then
    printf '%s\n' '--output-directory cannot be used with --correctness-only.' >&2
    exit 1
  fi
  if [[ "$timing_option_seen" -eq 1 ]]; then
    printf '%s\n' 'timing options cannot be used with --correctness-only.' >&2
    exit 1
  fi
fi

if [[ -z "${DOTNETJQ_REPORT_LEASE_ID:-}" || -z "${DOTNETJQ_REPORT_LEASE_FD:-}" ]]; then
  if [[ -n "${DOTNETJQ_REPORT_LEASE_ID:-}" || -n "${DOTNETJQ_REPORT_LEASE_FD:-}" ]]; then
    printf '%s\n' 'Incomplete inherited performance report lease.' >&2
    exit 1
  fi
  exec python3 -B "$script_directory/report_output.py" \
    --root "$repository_root" --kind fixture --output "$report_output_directory" \
    --run-command -- "$script_directory/run.sh" "$@"
fi

cd "$repository_root"
if [[ "$correctness_only_count" -eq 0 ]]; then
  python3 -B "$script_directory/report_output.py" \
    --root "$repository_root" --kind fixture --output "$report_output_directory"
fi
env -u DOTNETJQ_REPORT_LEASE_ID -u DOTNETJQ_REPORT_LEASE_FD \
  python3 -B "$script_directory/test_harness.py" --require-pinned-upstream
env -u DOTNETJQ_REPORT_LEASE_ID -u DOTNETJQ_REPORT_LEASE_FD \
  python3 -B "$script_directory/test_paired_baseline.py"

if [[ "$external_artifact_mode" -eq 1 ]]; then
  if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    printf '%s\n' 'External release performance mode requires a Linux x64 host.' >&2
    exit 1
  fi
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" create-external-aot \
    --executable "$external_aot_executable" \
    --archive "$external_aot_archive" \
    --provenance "$external_aot_provenance"
  benchmark_arguments+=(
    --framework-executable "$external_framework_executable"
    --aot-executable "$external_aot_executable"
  )
elif [[ "$skip_build" -eq 0 ]]; then
  if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    printf '%s\n' 'The default benchmark build requires a Linux x64 host.' >&2
    exit 1
  fi

  attestation_temporary_directory="$(mktemp -d)"
  trap 'rm -rf -- "$attestation_temporary_directory"' EXIT
  prebuild_snapshot="$attestation_temporary_directory/prebuild.json"
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" prepare-output --deployment framework-dotnetjq
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" prepare-output --deployment aot-dotnetjq
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" snapshot --output "$prebuild_snapshot"

  framework_publish=(
    dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj
    --configuration Release
    --runtime linux-x64
    --self-contained false
    --output artifacts/performance/framework
    --artifacts-path artifacts/performance/build/framework
    -p:PublishAot=false
    -p:NuGetAudit=false
    --nologo
  )
  aot_publish=(
    dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj
    --configuration Release
    --runtime linux-x64
    --self-contained true
    --output artifacts/performance/native-aot
    --artifacts-path artifacts/performance/build/aot
    -p:PublishAot=true
    -p:DebugSymbols=false
    -p:DebugType=none
    -p:NuGetAudit=false
    --nologo
  )
  "${framework_publish[@]}"
  "${aot_publish[@]}"
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" create --snapshot "$prebuild_snapshot" \
    --deployment framework-dotnetjq -- "${framework_publish[@]}"
  python3 -B "$script_directory/build_attestation.py" \
    --root "$repository_root" create --snapshot "$prebuild_snapshot" \
    --deployment aot-dotnetjq -- "${aot_publish[@]}"
fi

if [[ "$correctness_only_count" -eq 0 ]]; then
  if [[ "$skip_build" -eq 1 || "$limited_run" -eq 1 ]]; then
    benchmark_arguments+=(--non-publishable)
  else
    benchmark_arguments+=(--publishable)
  fi
fi

python3 "$script_directory/benchmark.py" "${benchmark_arguments[@]}"
