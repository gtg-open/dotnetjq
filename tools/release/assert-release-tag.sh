#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: assert-release-tag.sh --tag TAG [options]' \
    '' \
    'Options:' \
    '  --remote NAME         Git remote to verify (default: origin)' \
    '  --main-branch NAME    release ancestry branch (default: main)' \
    '  --expected-commit SHA require the tag to retain this peeled commit' \
    '  --github-output FILE  append version/prerelease/commit outputs'
}

tag=
remote=origin
main_branch=main
expected_commit=
github_output=
while [ "$#" -gt 0 ]; do
  case $1 in
    --tag) [ "$#" -ge 2 ] || release_die '--tag requires a value'; tag=$2; shift 2 ;;
    --remote) [ "$#" -ge 2 ] || release_die '--remote requires a value'; remote=$2; shift 2 ;;
    --main-branch) [ "$#" -ge 2 ] || release_die '--main-branch requires a value'; main_branch=$2; shift 2 ;;
    --expected-commit) [ "$#" -ge 2 ] || release_die '--expected-commit requires a value'; expected_commit=$2; shift 2 ;;
    --github-output) [ "$#" -ge 2 ] || release_die '--github-output requires a value'; github_output=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

if [[ $tag =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.([1-9][0-9]*))?$ ]]; then
  version=${tag#v}
else
  release_die "release tag must be vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N: ${tag:-<empty>}"
fi

declared_version=$(sed -n 's|^[[:space:]]*<DotNetJqVersion>\([^<]*\)</DotNetJqVersion>[[:space:]]*$|\1|p' \
  "$release_repository_root/Directory.Build.props")
[ -n "$declared_version" ] || release_die 'Directory.Build.props does not declare DotNetJqVersion'
[ "$(printf '%s\n' "$declared_version" | wc -l | tr -d ' ')" -eq 1 ] ||
  release_die 'Directory.Build.props must declare DotNetJqVersion exactly once'
base_version=${version%%-rc.*}
[ "$base_version" = "$declared_version" ] ||
  release_die "release tag base $base_version does not match declared DotNetJqVersion $declared_version"
if [ -n "$expected_commit" ]; then
  [[ $expected_commit =~ ^[0-9a-f]{40}$ ]] ||
    release_die '--expected-commit must be a lowercase full 40-character Git commit SHA'
fi

release_require_command git
git -C "$release_repository_root" fetch --force --no-tags "$remote" \
  "refs/heads/$main_branch:refs/remotes/$remote/$main_branch" >/dev/null
local_commit=$(git -C "$release_repository_root" rev-list -n 1 "refs/tags/$tag") ||
  release_die "local release tag does not resolve to a commit: $tag"
[ -z "$expected_commit" ] || [ "$local_commit" = "$expected_commit" ] ||
  release_die "local tag $tag resolves to $local_commit; expected immutable commit $expected_commit"

remote_refs=$(git -C "$release_repository_root" ls-remote "$remote" \
  "refs/tags/$tag" "refs/tags/$tag^{}") ||
  release_die "could not resolve release tag from $remote: $tag"
remote_commit=$(printf '%s\n' "$remote_refs" |
  awk -v peeled="refs/tags/$tag^{}" '$2 == peeled { print $1; found=1 } END { if (!found) exit 1 }') || {
    remote_commit=$(printf '%s\n' "$remote_refs" |
      awk -v direct="refs/tags/$tag" '$2 == direct { print $1; found=1 } END { if (!found) exit 1 }') ||
      release_die "remote release tag does not exist: $tag"
  }
[ "$remote_commit" = "$local_commit" ] ||
  release_die "remote tag $tag resolves to $remote_commit; expected $local_commit"
[ -z "$expected_commit" ] || [ "$remote_commit" = "$expected_commit" ] ||
  release_die "remote tag $tag moved to $remote_commit; expected immutable commit $expected_commit"
git -C "$release_repository_root" merge-base --is-ancestor \
  "$local_commit" "refs/remotes/$remote/$main_branch" ||
  release_die "release commit $local_commit is not on $remote/$main_branch"

case $version in
  *-rc.*) prerelease=true ;;
  *) prerelease=false ;;
esac

if [ -n "$github_output" ]; then
  printf 'version=%s\nprerelease=%s\ncommit=%s\n' \
    "$version" "$prerelease" "$local_commit" >>"$github_output"
fi
release_note "verified $tag at $local_commit on $remote/$main_branch"
