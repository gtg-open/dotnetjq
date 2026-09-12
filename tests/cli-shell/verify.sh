#!/usr/bin/env sh
set -eu

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
  echo "usage: $0 /absolute/path/to/dotnetjq [expected-version]" >&2
  exit 2
fi

cli=$1
expected_version=${2:-1.0.0}
if [ ! -x "$cli" ]; then
  echo "dotnetjq executable is missing or not executable: $cli" >&2
  exit 2
fi

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/dotnetjq-cli.XXXXXX")
trap 'rm -rf "$work_dir"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

fail() {
  echo "CLI smoke test failed: $*" >&2
  exit 1
}

assert_exit() {
  expected=$1
  shift
  set +e
  "$@" >"$work_dir/stdout" 2>"$work_dir/stderr"
  actual=$?
  set -e
  [ "$actual" -eq "$expected" ] || fail "expected exit $expected, got $actual: $*"
}

# Basic invocation and a normal JSON pipeline.
printf '%s' '{"answer":42}' >"$work_dir/input"
"$cli" -c '.answer + 1' <"$work_dir/input" >"$work_dir/actual"
printf '%s\n' '43' >"$work_dir/expected"
cmp "$work_dir/expected" "$work_dir/actual" || fail "basic JSON pipeline bytes differ"

# UTF-8 must be preserved byte-for-byte and --raw-output0 must terminate with NUL.
printf '\303\251\360\237\214\215' >"$work_dir/input"
"$cli" -R -s --raw-output0 . <"$work_dir/input" >"$work_dir/actual"
printf '\303\251\360\237\214\215\000' >"$work_dir/expected"
cmp "$work_dir/expected" "$work_dir/actual" || fail "UTF-8/NUL output bytes differ"

# Paths containing spaces and non-ASCII characters exercise argv and filesystem conversion.
unicode_dir="$work_dir/path with spaces-é"
mkdir -p "$unicode_dir"
printf '%s\n' '.message' >"$unicode_dir/filter.jq"
printf '%s\n' '{"message":"Grüße 🌍"}' >"$unicode_dir/input.json"
"$cli" -r -f "$unicode_dir/filter.jq" "$unicode_dir/input.json" >"$work_dir/actual"
printf 'Gr\303\274\303\237e \360\237\214\215\n' >"$work_dir/expected"
cmp "$work_dir/expected" "$work_dir/actual" || fail "Unicode/space path output differs"

# On Linux, verify the actual tty boundary. Using Console.CancelKeyPress or the
# Console input stack on Unix can emit terminfo keypad-mode bytes before jq's
# first result; the CLI must not mutate the terminal merely by starting.
host_os=$(uname -s)
if [ "$host_os" = Linux ] && command -v script >/dev/null 2>&1; then
  (
    unset NO_COLOR LD_PRELOAD
    TERM=xterm-256color script -q -e -c "\"$cli\" -n null" /dev/null
  ) >"$work_dir/actual"
  printf '\033[0;90mnull\033[0m\r\n' >"$work_dir/expected"
  cmp "$work_dir/expected" "$work_dir/actual" || fail "automatic tty color/output bytes differ"
fi

# jq's public exit contract: false/null=1, empty=4, compile=3, runtime=5.
assert_exit 1 "$cli" -e -n false
assert_exit 4 "$cli" -e -n empty
assert_exit 3 "$cli" -n 'this is not valid jq syntax!'
assert_exit 5 "$cli" -n 'error("cli-smoke")'

"$cli" --version >"$work_dir/version"
printf 'dotnetjq-%s (jq-1.8.2 compatible)\n' "$expected_version" >"$work_dir/expected"
cmp "$work_dir/expected" "$work_dir/version" || fail "version contract differs"

echo "dotnetjq shell verification passed"
