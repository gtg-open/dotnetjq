#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: publish-nuget-plan.sh --directory PATH --expected-owner OWNER [options]' \
    '' \
    'Options:' \
    '  --plan FILE                 verified plan (default: DIRECTORY/nuget-publish-order.tsv)' \
    '  --source URL                push source (default: https://api.nuget.org/v3/index.json)' \
    '  --flat-container-base URL   content API (default: https://api.nuget.org/v3-flatcontainer)' \
    '  --expected-owner OWNER      required NuGet repository-signature owner' \
    '  --push-attempts COUNT       bounded push attempts after proven absence (default: 3)' \
    '  --probe-attempts COUNT      probes after each push response (default: 24)' \
    '  --probe-delay SECONDS       delay between post-push probes (default: 5)' \
    '  --push-timeout SECONDS      limit for one dotnet push process (default: 120)' \
    '' \
    'NUGET_API_KEY is required only when the preflight proves a package absent.'
}

directory=
plan=
source=https://api.nuget.org/v3/index.json
flat_container_base=https://api.nuget.org/v3-flatcontainer
expected_owner=
push_attempts=3
probe_attempts=24
probe_delay=5
push_timeout=120
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --plan) [ "$#" -ge 2 ] || release_die '--plan requires a value'; plan=$2; shift 2 ;;
    --source) [ "$#" -ge 2 ] || release_die '--source requires a value'; source=$2; shift 2 ;;
    --flat-container-base) [ "$#" -ge 2 ] || release_die '--flat-container-base requires a value'; flat_container_base=${2%/}; shift 2 ;;
    --expected-owner) [ "$#" -ge 2 ] || release_die '--expected-owner requires a value'; expected_owner=$2; shift 2 ;;
    --push-attempts) [ "$#" -ge 2 ] || release_die '--push-attempts requires a value'; push_attempts=$2; shift 2 ;;
    --probe-attempts) [ "$#" -ge 2 ] || release_die '--probe-attempts requires a value'; probe_attempts=$2; shift 2 ;;
    --probe-delay) [ "$#" -ge 2 ] || release_die '--probe-delay requires a value'; probe_delay=$2; shift 2 ;;
    --push-timeout) [ "$#" -ge 2 ] || release_die '--push-timeout requires a value'; push_timeout=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -d "$directory" ] || release_die "package directory does not exist: ${directory-}"
[ -n "$plan" ] || plan="$directory/nuget-publish-order.tsv"
[ -f "$plan" ] || release_die "NuGet publication plan does not exist: $plan"
[ ! -L "$plan" ] || release_die "NuGet publication plan must not be a symbolic link: $plan"
case $expected_owner in
  *[!A-Za-z0-9._-]*|'') release_die 'expected NuGet package owner is malformed' ;;
esac
case "$source|$flat_container_base" in
  'https://api.nuget.org/v3/index.json|https://api.nuget.org/v3-flatcontainer') ;;
  http://127.0.0.1:*|http://localhost:*)
    [ "${DOTNETJQ_RELEASE_TEST_MODE-}" = 1 ] ||
      release_die 'loopback NuGet fixture endpoints require DOTNETJQ_RELEASE_TEST_MODE=1'
    ;;
  *) release_die 'publication endpoints must be the official NuGet.org V3 endpoints' ;;
esac

validate_count() {
  local label=$1
  local value=$2
  local minimum=$3
  local maximum=$4
  case $value in *[!0-9]*|'') release_die "$label must be an integer" ;; esac
  [ "$value" -ge "$minimum" ] && [ "$value" -le "$maximum" ] ||
    release_die "$label must be between $minimum and $maximum"
}
validate_count push-attempts "$push_attempts" 1 5
validate_count probe-attempts "$probe_attempts" 1 60
validate_count probe-delay "$probe_delay" 0 60
validate_count push-timeout "$push_timeout" 1 600

for command in awk dotnet grep python3 timeout; do
  release_require_command "$command"
done

validation_root=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-nuget-publication)
cleanup_validation_root() {
  case $validation_root in
    "${TMPDIR:-/tmp}"/.dotnetjq-nuget-publication.*) rm -rf -- "$validation_root" ;;
    *) release_die "refusing unsafe temporary cleanup: $validation_root" ;;
  esac
}
trap cleanup_validation_root EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
validated_plan="$validation_root/validated-plan.tsv"
publication_state="$validation_root/publication-state.tsv"
configured_targets="$validation_root/release-targets.txt"
planned_ids="$validation_root/planned-package-ids.txt"
push_log="$validation_root/dotnet-nuget-push.log"
release_each_target >"$configured_targets"
: >"$validated_plan"
: >"$publication_state"
: >"$planned_ids"

expected_header=$'# order\tkind\tpackage_id\tversion\tfile\tsha256\tpayload_sha256'
exec 3<"$plan"
IFS= read -r actual_header <&3 || release_die 'NuGet publication plan is empty'
[ "$actual_header" = "$expected_header" ] ||
  release_die 'NuGet publication plan header is not the required identity-and-payload schema'

expected_order=1
plan_version=
tool_package_id=
while IFS= read -r row <&3; do
  [ -n "$row" ] || release_die 'NuGet publication plan contains a blank row'
  field_count=$(awk -F '\t' '{ print NF }' <<<"$row")
  [ "$field_count" -eq 7 ] ||
    release_die 'NuGet publication plan rows must contain exactly seven fields'
  IFS=$'\t' read -r order kind planned_id planned_version filename expected_sha payload_sha <<<"$row"
  [ -n "$order" ] && [ -n "$kind" ] && [ -n "$planned_id" ] &&
    [ -n "$planned_version" ] && [ -n "$filename" ] &&
    [ -n "$expected_sha" ] && [ -n "$payload_sha" ] ||
    release_die 'NuGet publication plan contains an incomplete row'
  printf -v expected_order_text '%02d' "$expected_order"
  [ "$order" = "$expected_order_text" ] ||
    release_die "NuGet publication order is not contiguous at $order"
  release_validate_package_id "$planned_id"
  release_validate_version "$planned_version"
  if [ -z "$plan_version" ]; then
    plan_version=$planned_version
  else
    [ "$planned_version" = "$plan_version" ] ||
      release_die "NuGet publication row $order has a different version"
  fi
  if grep -Fqx -- "$planned_id" "$planned_ids"; then
    release_die "NuGet publication plan repeats package ID: $planned_id"
  fi
  printf '%s\n' "$planned_id" >>"$planned_ids"
  case $filename in
    */*|*\\*|.|..) release_die "planned package filename is not a leaf name: $filename" ;;
    *.nupkg) ;;
    *) release_die "planned publication file is not a NuGet package: $filename" ;;
  esac
  [ "$filename" = "$planned_id.$planned_version.nupkg" ] ||
    release_die "planned filename is not bound to ID/version: $filename"
  for hash in "$expected_sha" "$payload_sha"; do
    case $hash in
      *[!0-9a-f]*|'') release_die "planned package hash is malformed: $filename" ;;
    esac
    [ "${#hash}" -eq 64 ] || release_die "planned package hash is not SHA-256: $filename"
  done
  package="$directory/$filename"
  [ -f "$package" ] || release_die "planned package does not exist: $filename"
  [ ! -L "$package" ] || release_die "planned package must not be a symbolic link: $filename"
  [ "$(release_sha256 "$package")" = "$expected_sha" ] ||
    release_die "planned package hash changed: $filename"
  actual_payload_sha=$(python3 "$release_script_dir/verify-nuget-package-payload.py" \
    --package "$package" --package-id "$planned_id" --version "$planned_version" \
    --signature-policy absent --expected-payload-sha256 "$payload_sha") ||
    release_die "planned NuGet payload or nuspec identity is invalid: $filename"
  [ "$actual_payload_sha" = "$payload_sha" ] ||
    release_die "planned NuGet payload hash changed: $filename"

  case $expected_order in
    1) [ "$kind" = LIBRARY ] || release_die 'NuGet publication plan must start with LIBRARY' ;;
    10)
      [ "$kind" = POINTER_LAST ] || release_die 'NuGet publication plan must end with POINTER_LAST'
      [ "$planned_id" = "$tool_package_id" ] ||
        release_die 'pointer package ID does not match the RID package family'
      ;;
    *)
      expected_rid=$(awk -v line="$((expected_order - 1))" \
        'NR == line { print; found=1 } END { if (!found) exit 1 }' "$configured_targets") ||
        release_die "NuGet publication plan has an unexpected RID row: $order"
      [ "$kind" = "RID:$expected_rid" ] ||
        release_die "NuGet publication row $order must be RID:$expected_rid"
      case $planned_id in
        *."$expected_rid") rid_family=${planned_id%."$expected_rid"} ;;
        *) release_die "RID package ID is not bound to $expected_rid: $planned_id" ;;
      esac
      if [ -z "$tool_package_id" ]; then
        tool_package_id=$rid_family
      else
        [ "$rid_family" = "$tool_package_id" ] ||
          release_die "RID package row $order belongs to a different package family"
      fi
      ;;
  esac
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$order" "$kind" "$planned_id" "$planned_version" "$filename" \
    "$expected_sha" "$payload_sha" >>"$validated_plan"
  expected_order=$((expected_order + 1))
done
exec 3<&-

validated_count=$((expected_order - 1))
[ "$validated_count" -eq 10 ] ||
  release_die "NuGet publication plan must contain exactly ten package rows; found $validated_count"

probe_package() {
  local package_path=$1
  local probe_id=$2
  local probe_version=$3
  local probe_payload_sha=$4
  "$release_script_dir/probe-nuget-package.sh" \
    --package "$package_path" --package-id "$probe_id" \
    --version "$probe_version" --payload-sha256 "$probe_payload_sha" \
    --expected-owner "$expected_owner" \
    --flat-container-base "$flat_container_base"
}

release_note 'preflighting all ten exact NuGet package IDs and versions before publication'
preflight_failed=0
while IFS=$'\t' read -r order kind planned_id planned_version filename expected_sha payload_sha; do
  package="$directory/$filename"
  probe_status=0
  probe_package "$package" "$planned_id" "$planned_version" "$payload_sha" || probe_status=$?
  case $probe_status in
    0) state=PRESENT ;;
    3) state=ABSENT ;;
    75) state=UNAVAILABLE; preflight_failed=1 ;;
    *) state=UNPROVEN; preflight_failed=1 ;;
  esac
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$order" "$kind" "$planned_id" "$planned_version" "$filename" \
    "$expected_sha" "$payload_sha" "$state" >>"$publication_state"
done <"$validated_plan"
[ "$preflight_failed" -eq 0 ] ||
  release_die 'NuGet publication preflight could not prove every planned package present-identical or absent'

probe_until_match() {
  local package_path=$1
  local probe_id=$2
  local probe_version=$3
  local probe_payload_sha=$4
  local attempt=1
  local status=75
  while [ "$attempt" -le "$probe_attempts" ]; do
    status=0
    probe_package "$package_path" "$probe_id" "$probe_version" "$probe_payload_sha" || status=$?
    case $status in
      0) return 0 ;;
      1) return 1 ;;
      3|75) ;;
      *) return 1 ;;
    esac
    if [ "$attempt" -lt "$probe_attempts" ] && [ "$probe_delay" -gt 0 ]; then
      sleep "$probe_delay"
    fi
    attempt=$((attempt + 1))
  done
  return "$status"
}

publish_absent_package() {
  local order=$1
  local kind=$2
  local planned_id=$3
  local planned_version=$4
  local filename=$5
  local payload_sha=$6
  local package="$directory/$filename"
  local attempt=1
  local push_status
  local probe_status

  probe_status=0
  probe_package "$package" "$planned_id" "$planned_version" "$payload_sha" || probe_status=$?
  case $probe_status in
    0)
      release_note "package became present and was proven identical before push: $planned_id $planned_version"
      return 0
      ;;
    3) ;;
    75) release_die "NuGet became unavailable before publishing $planned_id $planned_version" ;;
    *) release_die "NuGet package state changed to an unproven value: $planned_id $planned_version" ;;
  esac

  [ -n "${NUGET_API_KEY-}" ] ||
    release_die "NUGET_API_KEY is required to publish absent package $planned_id $planned_version"
  while [ "$attempt" -le "$push_attempts" ]; do
    release_note "publishing absent NuGet plan row $order ($kind), attempt $attempt: $filename"
    push_status=0
    timeout --signal=TERM --kill-after=10s "${push_timeout}s" \
      dotnet nuget push "$package" \
        --api-key "$NUGET_API_KEY" \
        --source "$source" >"$push_log" 2>&1 || push_status=$?
    if [ "$push_status" -ne 0 ]; then
      release_note "dotnet nuget push returned status $push_status; probing exact remote content before any retry"
    fi

    probe_status=0
    probe_until_match "$package" "$planned_id" "$planned_version" "$payload_sha" || probe_status=$?
    case $probe_status in
      0)
        release_note "NuGet publication is remotely proven: $planned_id $planned_version"
        return 0
        ;;
      1)
        release_die "NuGet publication produced mismatched or unproven remote content: $planned_id $planned_version"
        ;;
      75)
        release_die "NuGet remained transiently unavailable after push; refusing a blind retry: $planned_id $planned_version"
        ;;
      3)
        if [ "$push_status" -eq 0 ]; then
          release_die "NuGet accepted the push but the exact package never became remotely provable: $planned_id $planned_version"
        fi
        if [ "$attempt" -ge "$push_attempts" ]; then
          release_die "NuGet package remained absent after $attempt bounded push attempts: $planned_id $planned_version"
        fi
        release_note "package is still proven absent after the ambiguous push response; retrying"
        ;;
      *) release_die "unexpected NuGet probe result for $planned_id $planned_version" ;;
    esac
    attempt=$((attempt + 1))
  done
  release_die "bounded NuGet publication attempts were exhausted: $planned_id $planned_version"
}

processed=0
while IFS=$'\t' read -r order kind planned_id planned_version filename expected_sha payload_sha state; do
  case $state in
    PRESENT)
      release_note "skipping remotely proven identical NuGet package: $planned_id $planned_version"
      ;;
    ABSENT)
      publish_absent_package "$order" "$kind" "$planned_id" "$planned_version" \
        "$filename" "$payload_sha"
      ;;
    *) release_die "invalid preflight state for $planned_id $planned_version: $state" ;;
  esac
  processed=$((processed + 1))
done <"$publication_state"
[ "$processed" -eq 10 ] || release_die "NuGet publication state contained $processed package rows"

release_note 'reverifying all ten published NuGet packages from the flat-container API'
remote_verified=0
final_failed=0
while IFS=$'\t' read -r order kind planned_id planned_version filename expected_sha payload_sha; do
  package="$directory/$filename"
  probe_status=0
  probe_until_match "$package" "$planned_id" "$planned_version" "$payload_sha" || probe_status=$?
  if [ "$probe_status" -eq 0 ]; then
    remote_verified=$((remote_verified + 1))
  else
    final_failed=1
    release_note "final remote verification failed for $planned_id $planned_version (probe status $probe_status)"
  fi
done <"$validated_plan"
[ "$final_failed" -eq 0 ] && [ "$remote_verified" -eq 10 ] ||
  release_die "final NuGet verification proved $remote_verified of ten packages"

release_note 'NuGet publication plan completed in verified dependency order; all ten remote payloads match'
