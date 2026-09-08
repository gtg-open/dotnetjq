#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: assert-publication-ready.sh --version VERSION --artifacts PATH --proof FILE' \
    '       --repository OWNER/NAME --publisher TEXT --winget-package-id ID [options]' \
    '' \
    'Options:' \
    '  --rid LINUX_RID          relink proof RID (default: linux-x64)' \
    '  --package-id ID          CLI tool package ID (default: dotnetjq)' \
    '  --library-package-id ID  managed library package ID (default: DotNetJq.Library)' \
    '  --tag TAG                release tag used by generated URLs (default: vVERSION)' \
    '' \
    'Validates engineering evidence only; it never uploads or publishes artifacts.'
}

version=
artifacts=
proof=
rid=linux-x64
package_id=dotnetjq
library_package_id=DotNetJq.Library
repository=
publisher=
winget_package_id=
tag=
while [ "$#" -gt 0 ]; do
  case $1 in
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --artifacts) [ "$#" -ge 2 ] || release_die '--artifacts requires a value'; artifacts=$2; shift 2 ;;
    --proof) [ "$#" -ge 2 ] || release_die '--proof requires a value'; proof=$2; shift 2 ;;
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --library-package-id) [ "$#" -ge 2 ] || release_die '--library-package-id requires a value'; library_package_id=$2; shift 2 ;;
    --repository) [ "$#" -ge 2 ] || release_die '--repository requires a value'; repository=$2; shift 2 ;;
    --publisher) [ "$#" -ge 2 ] || release_die '--publisher requires a value'; publisher=$2; shift 2 ;;
    --winget-package-id) [ "$#" -ge 2 ] || release_die '--winget-package-id requires a value'; winget_package_id=$2; shift 2 ;;
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

release_validate_version "$version"
release_validate_package_id "$package_id"
release_validate_package_id "$library_package_id"
release_validate_repository "$repository"
release_validate_single_line publisher "$publisher"
release_validate_package_id "$winget_package_id"
[ -n "$tag" ] || tag="v$version"
case $tag in *[!0-9A-Za-z._-]*) release_die "invalid release tag: $tag" ;; esac
[ -d "$artifacts" ] || release_die "artifact directory does not exist: ${artifacts-}"
[ -f "$proof" ] || release_die "AOT relink proof does not exist: ${proof-}"
case $rid in linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64) ;; *) release_die "invalid relink proof RID: $rid" ;; esac
for command in awk tar; do
  release_require_command "$command"
done

publication_plan="$artifacts/nuget-publish-order.tsv"
[ -f "$publication_plan" ] || release_die "NuGet publication plan is missing: $publication_plan"
plan_work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-publication-plan)
cleanup_plan() {
  case $plan_work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-publication-plan.*) rm -rf -- "$plan_work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $plan_work_dir" ;;
  esac
}
trap cleanup_plan EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
expected_plan="$plan_work_dir/nuget-publish-order.tsv"
configured_targets="$plan_work_dir/release-targets.txt"
release_each_target >"$configured_targets"
target_count=$(awk 'END { print NR }' "$configured_targets")
expected_package_count=$((target_count + 2))
actual_package_count=$(find "$artifacts" -maxdepth 1 -type f -name '*.nupkg' | wc -l | tr -d ' ')
[ "$actual_package_count" -eq "$expected_package_count" ] ||
  release_die "publication requires exactly $expected_package_count nupkgs; found $actual_package_count"

printf '# order\tkind\tfile\tsha256\n' >"$expected_plan"
order=1
name="$library_package_id.$version.nupkg"
[ -f "$artifacts/$name" ] || release_die "managed library package is missing: $name"
package_sha256=$(release_sha256 "$artifacts/$name")
printf '%02d\tLIBRARY\t%s\t%s\n' "$order" "$name" "$package_sha256" >>"$expected_plan"
order=$((order + 1))
while IFS= read -r package_rid; do
  name="$package_id.$package_rid.$version.nupkg"
  [ -f "$artifacts/$name" ] || release_die "RID package is missing: $name"
  package_sha256=$(release_sha256 "$artifacts/$name")
  printf '%02d\tRID:%s\t%s\t%s\n' "$order" "$package_rid" "$name" \
    "$package_sha256" >>"$expected_plan"
  order=$((order + 1))
done <"$configured_targets"
name="$package_id.$version.nupkg"
[ -f "$artifacts/$name" ] || release_die "pointer package is missing: $name"
package_sha256=$(release_sha256 "$artifacts/$name")
printf '%02d\tPOINTER_LAST\t%s\t%s\n' "$order" "$name" \
  "$package_sha256" >>"$expected_plan"
cmp -s -- "$expected_plan" "$publication_plan" ||
  release_die 'NuGet publication plan does not exactly match the ten verified packages and hashes'

source_archive="$artifacts/dotnetjq-aot-source-$version.tar.gz"
"$release_script_dir/verify-aot-source-archive.sh" \
  --archive "$source_archive" --checksum "$source_archive.sha256" --version "$version"

proof_value() {
  local key=$1
  local matches
  matches=$(awk -v prefix="${key}=" '
    index($0, prefix) == 1 { print; count++ }
    END { if (count != 1) exit 1 }
  ' "$proof") || release_die "proof must contain exactly one $key record"
  printf '%s\n' "${matches#*=}"
}

aot_relink_proof=$(proof_value AOT_RELINK_PROOF)
proof_rid=$(proof_value RID)
proof_source_archive=$(proof_value SOURCE_ARCHIVE_SHA256)
source_archive_sha256=$(release_sha256 "$source_archive")
[ "$aot_relink_proof" = PASS ] || release_die 'AOT relink proof did not pass'
[ "$proof_rid" = "$rid" ] || release_die 'AOT relink proof records a different RID'
[ "$proof_source_archive" = "$source_archive_sha256" ] ||
  release_die 'AOT relink proof is not bound to the distributed source archive'

original_component=$(proof_value ORIGINAL_COMPONENT_SHA256)
modified_component=$(proof_value MODIFIED_COMPONENT_SHA256)
original_binary=$(proof_value ORIGINAL_BINARY_SHA256)
modified_binary=$(proof_value MODIFIED_BINARY_SHA256)
for hash in "$original_component" "$modified_component" "$original_binary" "$modified_binary"; do
  [[ $hash =~ ^[0-9a-f]{64}$ ]] || release_die "proof contains a malformed SHA-256 value: $hash"
done
[ "$original_component" != "$modified_component" ] || release_die 'proof did not modify the LGPL-derived component'
[ "$original_binary" != "$modified_binary" ] || release_die 'proof produced identical original and modified binaries'
modified_result=$(proof_value MODIFIED_RESULT)
original_result=$(proof_value ORIGINAL_RESULT)
[ "$modified_result" = 424242.5 ] || release_die 'modified proof binary did not expose the controlled result'
[ "$original_result" != 424242.5 ] || release_die 'original proof binary unexpectedly exposed the controlled result'

source_component="$plan_work_dir/original-component"
tar -xOf "$source_archive" \
  "dotnetjq-aot-source-$version/src/DotNetJq.GlibcCompat/GlibcCompatMath.cs" \
  >"$source_component" ||
  release_die 'could not extract the proof component from the distributed corresponding-source archive'
[ "$original_component" = "$(release_sha256 "$source_component")" ] ||
  release_die 'relink proof original-component hash is not bound to the distributed corresponding source'

# Bind the unmodified corresponding-source rebuild to the actual executable
# being distributed for the proof RID. A relink proof with a valid-looking but
# unrelated ORIGINAL_BINARY_SHA256 must never make a different release image
# publication-ready.
proof_archive="$artifacts/$(release_archive_name "$version" "$rid")"
[ -f "$proof_archive" ] ||
  release_die "proof-RID native archive is missing: $(basename -- "$proof_archive")"
proof_archive_executable="$plan_work_dir/proof-rid-executable"
case $(release_target_field "$rid" 2) in
  tar.gz)
    tar -xOf "$proof_archive" ./dotnetjq >"$proof_archive_executable" ||
      release_die 'could not extract the proof-RID executable from its release archive'
    ;;
  *)
    release_die "unsupported relink proof archive format for $rid"
    ;;
esac
[ -s "$proof_archive_executable" ] ||
  release_die 'proof-RID release archive contains an empty executable'
shipped_proof_binary=$(release_sha256 "$proof_archive_executable")
[ "$original_binary" = "$shipped_proof_binary" ] ||
  release_die 'unmodified relink-proof binary is not byte-identical to the shipped proof-RID executable'

"$release_script_dir/verify-package-manager-metadata.sh" \
  --version "$version" --repository "$repository" --tag "$tag" \
  --publisher "$publisher" --winget-package-id "$winget_package_id" \
  --artifacts "$artifacts"

release_note "publication engineering evidence is ready for $version ($expected_package_count packages; $rid relink proof)"
release_note 'no upload or publication was performed'
