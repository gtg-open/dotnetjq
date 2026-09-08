#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: create-nuget-publish-plan.sh --directory PATH --package-id ID --version VERSION' \
    '       --authors TEXT --repository-url URL [options]' \
    '' \
    'Options:' \
    '  --library-package-id ID  managed library package ID (default: DotNetJq.Library)' \
    '  --output FILE            output TSV (default: DIRECTORY/nuget-publish-order.tsv)' \
    '' \
    'The generated plan is data only. It never invokes dotnet nuget push.'
}

directory=
package_id=
library_package_id=DotNetJq.Library
version=
output=
authors=
repository_url=
while [ "$#" -gt 0 ]; do
  case $1 in
    --directory) [ "$#" -ge 2 ] || release_die '--directory requires a value'; directory=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --library-package-id) [ "$#" -ge 2 ] || release_die '--library-package-id requires a value'; library_package_id=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --authors) [ "$#" -ge 2 ] || release_die '--authors requires a value'; authors=$2; shift 2 ;;
    --repository-url) [ "$#" -ge 2 ] || release_die '--repository-url requires a value'; repository_url=$2; shift 2 ;;
    --output) [ "$#" -ge 2 ] || release_die '--output requires a value'; output=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -d "$directory" ] || release_die "NuGet directory does not exist: ${directory-}"
release_validate_package_id "$package_id"
release_validate_package_id "$library_package_id"
release_validate_version "$version"
release_validate_single_line authors "$authors"
release_validate_single_line repository-url "$repository_url"
[ -n "$output" ] || output="$directory/nuget-publish-order.tsv"
release_require_command awk
release_require_command python3

"$release_script_dir/verify-nuget-package-set.sh" \
  --directory "$directory" --package-id "$package_id" \
  --library-package-id "$library_package_id" --version "$version" \
  --authors "$authors" --repository-url "$repository_url"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-nuget-plan)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-nuget-plan.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
candidate="$work_dir/nuget-publish-order.tsv"
configured_targets="$work_dir/release-targets.txt"
release_each_target >"$configured_targets"
target_count=$(awk 'END { print NR }' "$configured_targets")
expected_package_count=$((target_count + 2))
actual_package_count=$(find "$directory" -maxdepth 1 -type f -name '*.nupkg' | wc -l | tr -d ' ')
[ "$actual_package_count" -eq "$expected_package_count" ] ||
  release_die "publication plan requires exactly $expected_package_count nupkgs; found $actual_package_count"

printf '# order\tkind\tpackage_id\tversion\tfile\tsha256\tpayload_sha256\n' >"$candidate"
order=1
name="$library_package_id.$version.nupkg"
package_sha256=$(release_sha256 "$directory/$name")
payload_sha256=$(python3 "$release_script_dir/verify-nuget-package-payload.py" \
  --package "$directory/$name" --package-id "$library_package_id" \
  --version "$version" --signature-policy absent)
printf '%02d\tLIBRARY\t%s\t%s\t%s\t%s\t%s\n' \
  "$order" "$library_package_id" "$version" "$name" "$package_sha256" \
  "$payload_sha256" >>"$candidate"
order=$((order + 1))
while IFS= read -r rid; do
  rid_package_id="$package_id.$rid"
  name="$rid_package_id.$version.nupkg"
  package_sha256=$(release_sha256 "$directory/$name")
  payload_sha256=$(python3 "$release_script_dir/verify-nuget-package-payload.py" \
    --package "$directory/$name" --package-id "$rid_package_id" \
    --version "$version" --signature-policy absent)
  printf '%02d\tRID:%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$order" "$rid" "$rid_package_id" "$version" "$name" \
    "$package_sha256" "$payload_sha256" >>"$candidate"
  order=$((order + 1))
done <"$configured_targets"
name="$package_id.$version.nupkg"
package_sha256=$(release_sha256 "$directory/$name")
payload_sha256=$(python3 "$release_script_dir/verify-nuget-package-payload.py" \
  --package "$directory/$name" --package-id "$package_id" \
  --version "$version" --signature-policy absent)
printf '%02d\tPOINTER_LAST\t%s\t%s\t%s\t%s\t%s\n' \
  "$order" "$package_id" "$version" "$name" "$package_sha256" \
  "$payload_sha256" >>"$candidate"

release_assert_new_or_identical "$candidate" "$output"
release_note "created data-only NuGet publication order: $output"
