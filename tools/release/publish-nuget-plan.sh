#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: publish-nuget-plan.sh --directory PATH [options]' \
    '' \
    'Options:' \
    '  --plan FILE    verified plan (default: DIRECTORY/nuget-publish-order.tsv)' \
    '  --source URL   NuGet V3 source (default: https://api.nuget.org/v3/index.json)' \
    '' \
    'NUGET_API_KEY must contain a short-lived publication key.'
}

directory=
plan=
source=https://api.nuget.org/v3/index.json
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --plan) [ "$#" -ge 2 ] || release_die '--plan requires a value'; plan=$2; shift 2 ;;
    --source) [ "$#" -ge 2 ] || release_die '--source requires a value'; source=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -d "$directory" ] || release_die "package directory does not exist: ${directory-}"
[ -n "$plan" ] || plan="$directory/nuget-publish-order.tsv"
[ -f "$plan" ] || release_die "NuGet publication plan does not exist: $plan"
[ -n "${NUGET_API_KEY-}" ] || release_die 'NUGET_API_KEY is required'
case $source in https://*) ;; *) release_die 'NuGet source must use HTTPS' ;; esac
release_require_command dotnet

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

expected_order=1
while IFS=$'\t' read -r order kind filename expected_sha; do
  case $order in \#*) continue ;; esac
  [ -n "$order" ] && [ -n "$kind" ] && [ -n "$filename" ] && [ -n "$expected_sha" ] ||
    release_die 'NuGet publication plan contains an incomplete row'
  printf -v expected_order_text '%02d' "$expected_order"
  [ "$order" = "$expected_order_text" ] ||
    release_die "NuGet publication order is not contiguous at $order"
  case $filename in
    */*|*\\*|.|..) release_die "planned package filename is not a leaf name: $filename" ;;
    *.nupkg) ;;
    *) release_die "planned publication file is not a NuGet package: $filename" ;;
  esac
  case $expected_sha in
    *[!0-9a-f]*|'') release_die "planned package hash is malformed: $filename" ;;
  esac
  [ "${#expected_sha}" -eq 64 ] ||
    release_die "planned package hash is not SHA-256: $filename"
  package="$directory/$filename"
  [ -f "$package" ] || release_die "planned package does not exist: $filename"
  [ "$(release_sha256 "$package")" = "$expected_sha" ] ||
    release_die "planned package hash changed: $filename"
  case $expected_order in
    1) [ "$kind" = LIBRARY ] || release_die 'NuGet publication plan must start with LIBRARY' ;;
    10) [ "$kind" = POINTER_LAST ] || release_die 'NuGet publication plan must end with POINTER_LAST' ;;
    *) case $kind in RID:*) ;; *) release_die "NuGet publication row $order must be RID:*" ;; esac ;;
  esac
  printf '%s\t%s\t%s\t%s\n' "$order" "$kind" "$filename" "$expected_sha" >>"$validated_plan"
  expected_order=$((expected_order + 1))
done <"$plan"

validated_count=$((expected_order - 1))
[ "$validated_count" -eq 10 ] ||
  release_die "NuGet publication plan must contain exactly ten package rows; found $validated_count"

published=0
while IFS=$'\t' read -r order kind filename expected_sha; do
  package="$directory/$filename"
  release_note "publishing NuGet plan row $order ($kind): $filename"
  dotnet nuget push "$package" \
    --api-key "$NUGET_API_KEY" \
    --source "$source"
  published=$((published + 1))
done <"$validated_plan"

[ "$published" -eq 10 ] ||
  release_die "NuGet publication plan must contain exactly ten package rows; found $published"
release_note 'NuGet publication plan completed in verified dependency order'
