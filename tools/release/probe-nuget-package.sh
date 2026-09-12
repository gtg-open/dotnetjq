#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: probe-nuget-package.sh --package FILE --package-id ID --version VERSION' \
    '       --payload-sha256 HASH --expected-owner OWNER [options]' \
    '' \
    'Options:' \
    '  --flat-container-base URL  package-content base URL' \
    '                             (default: https://api.nuget.org/v3-flatcontainer)' \
    '' \
    'Exit status 0 means present and identical, 3 means absent, 75 means a' \
    'transient probe failure, and any other nonzero status is fail-closed.'
}

package=
package_id=
version=
payload_sha256=
expected_owner=
flat_container_base=https://api.nuget.org/v3-flatcontainer
while [ "$#" -gt 0 ]; do
  case $1 in
    --package) [ "$#" -ge 2 ] || release_die '--package requires a value'; package=$2; shift 2 ;;
    --package-id) [ "$#" -ge 2 ] || release_die '--package-id requires a value'; package_id=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --payload-sha256) [ "$#" -ge 2 ] || release_die '--payload-sha256 requires a value'; payload_sha256=$2; shift 2 ;;
    --expected-owner) [ "$#" -ge 2 ] || release_die '--expected-owner requires a value'; expected_owner=$2; shift 2 ;;
    --flat-container-base) [ "$#" -ge 2 ] || release_die '--flat-container-base requires a value'; flat_container_base=${2%/}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -f "$package" ] || release_die "local NuGet package does not exist: ${package-}"
[ ! -L "$package" ] || release_die "local NuGet package must not be a symbolic link: $package"
release_validate_package_id "$package_id"
release_validate_version "$version"
case $expected_owner in
  *[!A-Za-z0-9._-]*|'') release_die 'expected NuGet package owner is malformed' ;;
esac
case $payload_sha256 in
  *[!0-9a-f]*|'') release_die 'payload SHA-256 is malformed' ;;
esac
[ "${#payload_sha256}" -eq 64 ] || release_die 'payload SHA-256 is malformed'

download_protocol=https
case $flat_container_base in
  https://api.nuget.org/v3-flatcontainer) ;;
  http://127.0.0.1:*|http://localhost:*)
    [ "${DOTNETJQ_RELEASE_TEST_MODE-}" = 1 ] ||
      release_die 'loopback NuGet fixture URL requires DOTNETJQ_RELEASE_TEST_MODE=1'
    download_protocol=http
    ;;
  *)
    release_die 'flat-container base must be the official NuGet.org endpoint'
    ;;
esac

for command in curl dotnet grep python3 sed tr; do
  release_require_command "$command"
done

lower_package_id=$(printf '%s' "$package_id" | LC_ALL=C tr '[:upper:]' '[:lower:]')
lower_version=$(printf '%s' "$version" | LC_ALL=C tr '[:upper:]' '[:lower:]')
package_url="$flat_container_base/$lower_package_id/$lower_version/$lower_package_id.$lower_version.nupkg"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-nuget-probe)
cleanup() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-nuget-probe.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
remote_package="$work_dir/$lower_package_id.$lower_version.nupkg"
signature_log="$work_dir/dotnet-nuget-verify.log"

curl_status=0
http_status=$(curl \
  --silent --show-error --location \
  --proto "=$download_protocol" --proto-redir "=$download_protocol" \
  --connect-timeout 15 --max-time 60 \
  --output "$remote_package" --write-out '%{http_code}' \
  "$package_url") || curl_status=$?
if [ "$curl_status" -ne 0 ]; then
  release_note "NuGet flat-container probe was transiently unavailable for $package_id $version"
  exit 75
fi

case $http_status in
  200) ;;
  404)
    release_note "NuGet package is absent: $package_id $version"
    exit 3
    ;;
  408|425|429|5??)
    release_note "NuGet flat-container returned transient HTTP $http_status for $package_id $version"
    exit 75
    ;;
  *) release_die "NuGet flat-container returned HTTP $http_status for $package_id $version" ;;
esac
[ -s "$remote_package" ] || release_die "NuGet flat-container returned an empty package: $package_id $version"

if ! DOTNET_CLI_UI_LANGUAGE=en-US dotnet nuget verify --all --verbosity detailed \
  "$remote_package" >"$signature_log" 2>&1; then
  sed -n '1,160p' "$signature_log" >&2
  release_die "NuGet.org signature verification failed for $package_id $version"
fi
grep -Fqx 'Signature type: Repository' "$signature_log" ||
  release_die "remote package lacks a verified repository signature: $package_id $version"
grep -Fqx 'Service index: https://api.nuget.org/v3/index.json' "$signature_log" ||
  release_die "remote package repository signature is not bound to NuGet.org: $package_id $version"
if ! python3 - "$signature_log" "$expected_owner" <<'PY'
import pathlib
import sys

log_path = pathlib.Path(sys.argv[1])
expected_owner = sys.argv[2]
repository_section = False
repository_owner_values = []

for line in log_path.read_text(encoding="utf-8").splitlines():
    if line.startswith("Signature type: "):
        repository_section = line == "Signature type: Repository"
        continue
    if repository_section and line.startswith("Owners:"):
        if not line.startswith("Owners: "):
            print("repository-signature Owners line is malformed", file=sys.stderr)
            raise SystemExit(1)
        repository_owner_values.append(line.removeprefix("Owners: "))

if len(repository_owner_values) != 1:
    print(
        "expected exactly one repository-signature Owners line; "
        f"found {len(repository_owner_values)}",
        file=sys.stderr,
    )
    raise SystemExit(1)

owners = [token.strip() for token in repository_owner_values[0].split(",")]
if not owners or any(not token for token in owners):
    print("repository-signature Owners list is malformed", file=sys.stderr)
    raise SystemExit(1)
if expected_owner.casefold() not in {owner.casefold() for owner in owners}:
    print(
        f"repository signature is not bound to expected owner {expected_owner!r}",
        file=sys.stderr,
    )
    raise SystemExit(1)
PY
then
  release_die "remote package repository signature has an untrusted owner: $package_id $version"
fi

python3 "$release_script_dir/verify-nuget-package-payload.py" \
  --package "$package" --signature-policy absent \
  --package-id "$package_id" --version "$version" \
  --expected-payload-sha256 "$payload_sha256" \
  --compare "$remote_package" >/dev/null ||
  release_die "remote NuGet package does not match the planned local payload: $package_id $version"

release_note "verified existing NuGet.org package: $package_id $version"
