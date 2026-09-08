#!/usr/bin/env bash

set -euo pipefail
. "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/common.sh"

usage() {
  printf '%s\n' \
    'Usage: verify-native-archive.sh --archive PATH --rid RID --version VERSION [options]' \
    '' \
    'Options:' \
    '  --source-root PATH              license source root (default: repository root)' \
    '  --project PATH                  CLI project used to resolve the NativeAOT runtime pack' \
    '  --execute                       run the extracted executable' \
    '  --expected-version-output TEXT  exact output (default: derived from VERSION)'
}

archive=
rid=
version=
source_root=$release_repository_root
project="$release_repository_root/src/DotNetJq.Cli/DotNetJq.Cli.csproj"
execute_binary=0
expected_version_output=

while [ "$#" -gt 0 ]; do
  case $1 in
    --archive) [ "$#" -ge 2 ] || release_die '--archive requires a value'; archive=$2; shift 2 ;;
    --rid) [ "$#" -ge 2 ] || release_die '--rid requires a value'; rid=$2; shift 2 ;;
    --version) [ "$#" -ge 2 ] || release_die '--version requires a value'; version=$2; shift 2 ;;
    --source-root) [ "$#" -ge 2 ] || release_die '--source-root requires a value'; source_root=$2; shift 2 ;;
    --project) [ "$#" -ge 2 ] || release_die '--project requires a value'; project=$2; shift 2 ;;
    --execute) execute_binary=1; shift ;;
    --expected-version-output) [ "$#" -ge 2 ] || release_die '--expected-version-output requires a value'; expected_version_output=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) release_die "unknown argument: $1" ;;
  esac
done

[ -n "$archive" ] || release_die '--archive is required'
[ -f "$archive" ] || release_die "archive does not exist: $archive"
[ -n "$rid" ] || release_die '--rid is required'
release_validate_version "$version"
release_target_field "$rid" 2 >/dev/null
[ -f "$project" ] || release_die "CLI project does not exist: $project"
[ -n "$expected_version_output" ] || expected_version_output="dotnetjq-$version (jq-1.8.2 compatible)"
release_require_command python3
host_os=$(release_host_os)

expected_name=$(release_archive_name "$version" "$rid")
[ "$(basename -- "$archive")" = "$expected_name" ] ||
  release_die "archive name must be $expected_name"

format=$(release_target_field "$rid" 2)
python3 "$release_script_dir/verify-archive-metadata.py" native "$archive" --format "$format"
work_parent=${TMPDIR:-/tmp}
work_dir=$(release_make_temp_dir "$work_parent" dotnetjq-archive-verify)
cleanup() {
  case $work_dir in
    "$work_parent"/.dotnetjq-archive-verify.*) rm -rf -- "$work_dir" ;;
    *) release_die "refusing unsafe temporary cleanup: $work_dir" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

entries="$work_dir/entries.txt"
zip_extractor=
case $format in
  zip)
    if command -v unzip >/dev/null 2>&1 && command -v zipinfo >/dev/null 2>&1; then
      zip_extractor=unzip
      unzip -Z1 "$archive" >"$entries"
      zip_metadata="$work_dir/zipinfo.txt"
      zipinfo -l "$archive" >"$zip_metadata" ||
        release_die 'zipinfo could not inspect the ZIP archive'
      if grep '^l' "$zip_metadata" >/dev/null 2>&1; then
        release_die 'ZIP archive contains a symbolic link'
      elif [ "$?" -ne 1 ]; then
        release_die 'could not scan ZIP metadata for symbolic links'
      fi
    else
      if command -v pwsh >/dev/null 2>&1; then
        powershell_command=pwsh
      elif command -v powershell.exe >/dev/null 2>&1; then
        powershell_command=powershell.exe
      else
        release_die 'unzip plus zipinfo, pwsh, or powershell.exe is required to inspect a ZIP archive'
      fi
      zip_extractor=powershell
      powershell_archive=$archive
      if [ "$host_os" = windows ] && command -v cygpath >/dev/null 2>&1; then
        powershell_archive=$(cygpath -aw -- "$archive")
      fi
      DOTNETJQ_ARCHIVE="$powershell_archive" "$powershell_command" \
        -NoLogo -NoProfile -NonInteractive -Command '
          $ErrorActionPreference = "Stop"
          Add-Type -AssemblyName System.IO.Compression.FileSystem
          $zip = [IO.Compression.ZipFile]::OpenRead($env:DOTNETJQ_ARCHIVE)
          try {
            foreach ($entry in $zip.Entries) {
              $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
              if ($unixType -eq 0xA000) { throw "ZIP archive contains a symbolic link: $($entry.FullName)" }
              [Console]::WriteLine($entry.FullName)
            }
          } finally {
            $zip.Dispose()
          }
        ' >"$entries" || release_die 'PowerShell could not safely inspect the ZIP archive'
    fi
    ;;
  tar.gz)
    release_require_command tar
    tar -tzf "$archive" >"$entries"
    if ! tar -tvzf "$archive" | awk '{ type=substr($1,1,1); if (type != "-" && type != "d") bad=1 } END { exit bad }'; then
      release_die 'tar archive contains a link or special file'
    fi
    ;;
  *) release_die "unsupported archive format: $format" ;;
esac

[ -s "$entries" ] || release_die 'archive is empty'
canonical_entries="$work_dir/canonical-entries.txt"
archive_files="$work_dir/archive-files.txt"
: >"$canonical_entries"
: >"$archive_files"
while IFS= read -r entry; do
  [ -n "$entry" ] || release_die 'archive contains an empty entry name'
  case $entry in
    /*|../*|*/../*|*/..|*\\*) release_die "unsafe archive entry: $entry" ;;
  esac
  canonical=$entry
  case $canonical in .|./) continue ;; ./*) canonical=${canonical#./} ;; esac
  case $canonical in
    ''|.|./*|*/./*|*/.|*//*|../*|*/../*|*/..) release_die "non-canonical archive entry: $entry" ;;
  esac
  case $canonical in
    */) canonical=${canonical%/} ;;
    *) printf '%s\n' "$canonical" >>"$archive_files" ;;
  esac
  printf '%s\n' "$canonical" >>"$canonical_entries"
done <"$entries"

duplicate_entries="$work_dir/duplicate-entries.txt"
LC_ALL=C sort "$canonical_entries" | uniq -d >"$duplicate_entries"
if [ -s "$duplicate_entries" ]; then
  release_die 'archive contains duplicate or colliding entries'
fi

expected_files="$work_dir/expected-files.txt"
case $rid in win-*) printf 'dotnetjq.exe\n' ;; *) printf 'dotnetjq\n' ;; esac >"$expected_files"
while IFS= read -r required; do
  case $required in ''|'#'*) continue ;; esac
  [ -e "$source_root/$required" ] || release_die "license source is missing: $required"
  if [ -d "$source_root/$required" ]; then
    find "$source_root/$required" -type f -exec sh -c '
      prefix=$1
      shift
      for path do printf "%s\n" "${path#"$prefix/"}"; done
    ' sh "$source_root" {} + >>"$expected_files"
  else
    printf '%s\n' "$required" >>"$expected_files"
  fi
done <"$release_license_paths_file"
runtime_license_paths_file="$release_repository_root/packaging/nativeaot-runtime-license-paths.txt"
sed '/^#/d; /^$/d' "$runtime_license_paths_file" >>"$expected_files"
LC_ALL=C sort -u -o "$expected_files" "$expected_files"
LC_ALL=C sort -u -o "$archive_files" "$archive_files"
cmp -s -- "$expected_files" "$archive_files" ||
  release_die 'archive file inventory does not exactly match the executable and required license payload'

extract_root="$work_dir/extracted"
mkdir -p -- "$extract_root"
case $format in
  zip)
    if [ "$zip_extractor" = unzip ]; then
      unzip -q "$archive" -d "$extract_root"
    else
      powershell_archive=$archive
      powershell_extract_root=$extract_root
      if [ "$host_os" = windows ] && command -v cygpath >/dev/null 2>&1; then
        powershell_archive=$(cygpath -aw -- "$archive")
        powershell_extract_root=$(cygpath -aw -- "$extract_root")
      fi
      DOTNETJQ_ARCHIVE="$powershell_archive" DOTNETJQ_EXTRACT_ROOT="$powershell_extract_root" \
        "$powershell_command" -NoLogo -NoProfile -NonInteractive -Command '
          $ErrorActionPreference = "Stop"
          Add-Type -AssemblyName System.IO.Compression.FileSystem
          [IO.Compression.ZipFile]::ExtractToDirectory($env:DOTNETJQ_ARCHIVE, $env:DOTNETJQ_EXTRACT_ROOT)
        ' || release_die 'PowerShell could not extract the ZIP archive'
    fi
    ;;
  tar.gz) tar -xzf "$archive" -C "$extract_root" ;;
esac

extracted_symlink=$(find "$extract_root" -type l -print -quit)
if [ -n "$extracted_symlink" ]; then
  release_die 'archive must not contain symbolic links'
fi

expected_directories="$work_dir/expected-directories.txt"
actual_directories="$work_dir/actual-directories.txt"
release_write_parent_directory_inventory "$expected_files" "$expected_directories"
find "$extract_root" -mindepth 1 -type d -exec sh -c '
  prefix=$1
  shift
  for path do printf "%s\n" "${path#"$prefix/"}"; done
' sh "$extract_root" {} + | LC_ALL=C sort -u >"$actual_directories" ||
  release_die 'could not enumerate extracted archive directories'
cmp -s -- "$expected_directories" "$actual_directories" ||
  release_die 'archive contains a missing or unexpected internal directory'

case $rid in
  win-*) executable=dotnetjq.exe ;;
  *) executable=dotnetjq ;;
esac
[ -f "$extract_root/$executable" ] || release_die "archive is missing $executable"
case $rid in
  win-*) ;;
  *) [ -x "$extract_root/$executable" ] || release_die "$executable is not executable" ;;
esac
release_verify_binary_format "$extract_root/$executable" "$rid"

managed_archive_payload=$(find "$extract_root" -type f \
  \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) \
  -print -quit)
if [ -n "$managed_archive_payload" ]; then
  release_die 'NativeAOT archive contains managed runtime payload files'
fi

while IFS= read -r required; do
  case $required in ''|'#'*) continue ;; esac
  [ -e "$extract_root/$required" ] || release_die "archive is missing required path: $required"
  if [ -d "$source_root/$required" ]; then
    diff -qr -- "$source_root/$required" "$extract_root/$required" >/dev/null ||
      release_die "archive license directory differs from source: $required"
  else
    cmp -s -- "$source_root/$required" "$extract_root/$required" ||
      release_die "archive license file differs from source: $required"
  fi
done <"$release_license_paths_file"

resolved_runtime_licenses="$work_dir/resolved-nativeaot-runtime-licenses.txt"
"$release_script_dir/resolve-nativeaot-runtime-licenses.sh" \
  --project "$project" --rid "$rid" --output "$resolved_runtime_licenses"
while IFS= read -r runtime_license_source; do
  runtime_license_name=$(printf '%s\n' "$runtime_license_source" | sed 's#\\#/#g; s#.*/##')
  [ -f "$extract_root/$runtime_license_name" ] ||
    release_die "archive is missing resolved NativeAOT runtime license: $runtime_license_name"
  cmp -s -- "$runtime_license_source" "$extract_root/$runtime_license_name" ||
    release_die "archive NativeAOT runtime license differs from the resolved $rid pack: $runtime_license_name"
done <"$resolved_runtime_licenses"

if [ "$execute_binary" -eq 1 ]; then
  actual_output=$(env -u LD_PRELOAD "$extract_root/$executable" --version 2>"$work_dir/version.err") ||
    release_die 'extracted executable failed --version'
  [ ! -s "$work_dir/version.err" ] || release_die 'extracted executable wrote diagnostics for --version'
  [ "$actual_output" = "$expected_version_output" ] ||
    release_die "unexpected --version output: $actual_output"
fi

release_note "verified $expected_name"
