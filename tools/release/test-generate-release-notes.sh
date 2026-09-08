#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

work_dir=$(release_make_temp_dir "${TMPDIR:-/tmp}" dotnetjq-release-notes-test)
cleanup_release_notes_test() {
  case $work_dir in
    "${TMPDIR:-/tmp}"/.dotnetjq-release-notes-test.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe release-notes test cleanup: $work_dir" ;;
  esac
}
trap cleanup_release_notes_test EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

actual="$work_dir/notes.md"
expected="$work_dir/expected.md"
bash "$release_script_dir/generate-release-notes.sh" \
  --version 1.2.3-rc.4 \
  --repository gtg-open/dotnetjq \
  --tag v1.2.3-rc.4 \
  --output "$actual"

cat >"$expected" <<'EOF'
# DotNetJq 1.2.3-rc.4

This release is compatible with jq 1.8.2.

- [Installation and .NET, CLI, and PowerShell examples](https://github.com/gtg-open/dotnetjq/blob/v1.2.3-rc.4/docs/installation.md)
- [Canonical jq 1.8 manual](https://jqlang.org/manual/v1.8/)
- [SHA-256 checksums](https://github.com/gtg-open/dotnetjq/releases/download/v1.2.3-rc.4/SHA256SUMS)
- [Release and verification policy](https://github.com/gtg-open/dotnetjq/blob/v1.2.3-rc.4/RELEASING.md)

The attached archives are self-contained NativeAOT commands for all supported
64-bit Windows, Linux/glibc, Linux/musl, and macOS targets. NuGet provides the
managed library and the RID-selecting `dotnetjq` .NET tool package.
EOF

cmp -- "$expected" "$actual" || release_die 'generated release notes differ from the golden file'

bash "$release_script_dir/generate-release-notes.sh" \
  --version 1.2.3-rc.4 \
  --repository gtg-open/dotnetjq \
  --tag v1.2.3-rc.4 \
  --output "$actual"

if bash "$release_script_dir/generate-release-notes.sh" \
  --version 1.2.3-rc.4 \
  --repository gtg-open/dotnetjq \
  --tag v1.2.3 \
  --output "$work_dir/wrong-tag.md" >/dev/null 2>&1; then
  release_die 'release-notes generator accepted a tag/version mismatch'
fi

printf 'deterministic release-notes tests passed (3/3)\n'
