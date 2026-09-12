#!/usr/bin/env bash

set -euo pipefail

script_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
source_root="$(CDPATH= cd -- "$script_directory/../.." && pwd)"
fixture_root=$(mktemp -d "${TMPDIR:-/tmp}/dotnetjq-tag-test.XXXXXX")
cleanup() {
  case $fixture_root in
    "${TMPDIR:-/tmp}"/dotnetjq-tag-test.*) rm -rf -- "$fixture_root" ;;
    *) printf 'Refusing unsafe cleanup: %s\n' "$fixture_root" >&2; exit 1 ;;
  esac
}
trap cleanup EXIT

work="$fixture_root/work"
remote="$fixture_root/remote.git"
mkdir -p "$work/tools/release"
cp "$source_root/tools/release/common.sh" "$work/tools/release/common.sh"
cp "$source_root/tools/release/assert-release-tag.sh" \
  "$work/tools/release/assert-release-tag.sh"
cp "$source_root/Directory.Build.props" "$work/Directory.Build.props"

git -C "$work" init --initial-branch=main >/dev/null
git -C "$work" config user.name 'Release Tag Test'
git -C "$work" config user.email 'release-tag-test@example.invalid'
git -C "$work" add .
git -C "$work" commit -m fixture >/dev/null
git clone --bare "$work" "$remote" >/dev/null 2>&1
git -C "$work" remote add origin "$remote"

expect_failure() {
  local expected=$1
  shift
  local output
  if output=$("$@" 2>&1); then
    printf 'Expected command to fail: %s\n' "$*" >&2
    exit 1
  fi
  case $output in
    *"$expected"*) ;;
    *)
      printf 'Failure did not contain %s:\n%s\n' "$expected" "$output" >&2
      exit 1
      ;;
  esac
}

assert_tag="$work/tools/release/assert-release-tag.sh"
commit=$(git -C "$work" rev-parse HEAD)
output_file="$fixture_root/github-output"

git -C "$work" tag -a v1.0.0 -m 'DotNetJq 1.0.0'
git -C "$work" push origin refs/tags/v1.0.0 >/dev/null
bash "$assert_tag" --tag v1.0.0 --expected-commit "$commit" \
  --github-output "$output_file" >/dev/null
grep -Fxq 'version=1.0.0' "$output_file"
grep -Fxq 'prerelease=false' "$output_file"
grep -Fxq "commit=$commit" "$output_file"

# Reproduce the two actions/checkout fetch paths with an isolated local remote.
# An explicit SHA input checks out the event commit without rewriting its tag.
# The default tag+commit input instead falls back to +COMMIT:refs/tags/TAG
# after comparing the annotated tag object's SHA with the peeled commit SHA.
sha_checkout="$fixture_root/sha-checkout"
git init --initial-branch=main "$sha_checkout" >/dev/null
git -C "$sha_checkout" remote add origin "$remote"
git -C "$sha_checkout" fetch --prune --no-recurse-submodules origin \
  '+refs/heads/*:refs/remotes/origin/*' '+refs/tags/*:refs/tags/*' >/dev/null
git -C "$sha_checkout" checkout --detach "$commit" >/dev/null
[ "$(git -C "$sha_checkout" rev-parse HEAD)" = "$commit" ]
[ "$(git -C "$sha_checkout" rev-parse refs/tags/v1.0.0)" = \
  "$(git -C "$work" rev-parse refs/tags/v1.0.0)" ]
bash "$sha_checkout/tools/release/assert-release-tag.sh" \
  --tag v1.0.0 --expected-commit "$commit" >/dev/null

git -C "$sha_checkout" fetch --no-tags --prune --no-recurse-submodules origin \
  "+$commit:refs/tags/v1.0.0" >/dev/null
[ "$(git -C "$sha_checkout" cat-file -t refs/tags/v1.0.0)" = commit ]
[ "$(git --git-dir="$remote" cat-file -t refs/tags/v1.0.0)" = tag ]
expect_failure 'must be an annotated tag object' \
  bash "$sha_checkout/tools/release/assert-release-tag.sh" \
    --tag v1.0.0 --expected-commit "$commit"

git -C "$work" push origin :refs/tags/v1.0.0 >/dev/null
git -C "$work" tag -d v1.0.0 >/dev/null
git -C "$work" tag v1.0.0
git -C "$work" push origin refs/tags/v1.0.0 >/dev/null
expect_failure 'must be an annotated tag object' \
  bash "$assert_tag" --tag v1.0.0 --expected-commit "$commit"

git -C "$work" push origin :refs/tags/v1.0.0 >/dev/null
git -C "$work" tag -d v1.0.0 >/dev/null
git -C "$work" tag -a inner-tag -m inner
git -C "$work" tag -a v1.0.0 -m nested inner-tag
git -C "$work" push origin refs/tags/v1.0.0 >/dev/null
expect_failure 'must point directly to a commit' \
  bash "$assert_tag" --tag v1.0.0 --expected-commit "$commit"

git -C "$work" push origin :refs/tags/v1.0.0 >/dev/null
git -C "$work" tag -d v1.0.0 inner-tag >/dev/null
git -C "$work" tag -a v1.0.0 -m original
mutator="$fixture_root/mutator"
git clone "$remote" "$mutator" >/dev/null 2>&1
git -C "$mutator" config user.name 'Release Tag Mutator'
git -C "$mutator" config user.email 'release-tag-mutator@example.invalid'
git -C "$mutator" tag -a v1.0.0 -m replacement "$commit"
git -C "$mutator" push origin refs/tags/v1.0.0 >/dev/null
expect_failure 'annotated tag object v1.0.0 changed' \
  bash "$assert_tag" --tag v1.0.0 --expected-commit "$commit"

printf 'release tag tests passed (6/6)\n'
