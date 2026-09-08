#!/usr/bin/env bash

set -euo pipefail

# Release builds must not inherit a process-injection shim from the caller.
unset LD_PRELOAD

release_script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
release_repository_root=$(CDPATH= cd -- "$release_script_dir/../.." && pwd)
release_targets_file="$release_repository_root/packaging/release-targets.tsv"
release_license_paths_file="$release_repository_root/packaging/release-license-paths.txt"
release_aot_source_paths_file="$release_repository_root/packaging/aot-source-paths.txt"

release_die() {
  printf 'release tooling: %s\n' "$*" >&2
  exit 1
}

release_note() {
  printf 'release tooling: %s\n' "$*" >&2
}

release_require_command() {
  command -v "$1" >/dev/null 2>&1 || release_die "required command not found: $1"
}

release_validate_version() {
  local value=${1-}
  [[ $value =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] ||
    release_die "invalid normalized package version: ${value:-<empty>}"
  local prerelease=${value#*-}
  if [ "$prerelease" != "$value" ]; then
    local identifier
    local old_ifs=$IFS
    IFS=.
    for identifier in $prerelease; do
      case $identifier in
        *[!0-9]*|0) ;;
        0*) IFS=$old_ifs; release_die "invalid normalized package version: $value" ;;
      esac
    done
    IFS=$old_ifs
  fi
}

release_find_source_files() {
  local relative=$1
  local source="$release_repository_root/$relative"
  if [ -d "$source" ]; then
    find "$source" -type f \
      ! -path '*/bin/*' \
      ! -path '*/obj/*' \
      -print
  elif [ -f "$source" ]; then
    printf '%s\n' "$source"
  else
    release_die "required corresponding-source path is missing: $relative"
  fi
}

release_validate_package_id() {
  local value=${1-}
  [[ $value =~ ^[0-9A-Za-z]+([._-][0-9A-Za-z]+)*$ ]] ||
    release_die "invalid package identifier: ${value:-<empty>}"
}

release_validate_repository() {
  case ${1-} in
    */*) ;;
    *) release_die "repository must be explicit owner/name: ${1-<empty>}" ;;
  esac
  case $1 in
    *[!0-9A-Za-z._/-]*|/*|*/|*//*|*/*/*) release_die "invalid GitHub owner/name: $1" ;;
  esac
}

release_validate_single_line() {
  local label=$1
  local value=${2-}
  [ -n "$value" ] || release_die "$label must not be empty"
  case $value in
    *$'\n'*|*$'\r'*|*$'\t'*) release_die "$label must be a single plain-text value" ;;
  esac
}

release_sha256() {
  local path=$1
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -- "$path" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -- "$path" | awk '{print $1}'
  else
    release_die "sha256sum or shasum is required"
  fi
}

release_target_field() {
  local rid=$1
  local field=$2
  local row
  row=$(awk -F '\t' -v rid="$rid" '$1 == rid { print; found=1 } END { if (!found) exit 1 }' "$release_targets_file") ||
    release_die "unsupported RID: $rid"
  printf '%s\n' "$row" | awk -F '\t' -v field="$field" '{ print $field }'
}

release_each_target() {
  awk -F '\t' '!/^#/ && NF { print $1 }' "$release_targets_file"
}

release_write_parent_directory_inventory() {
  local file_inventory=$1
  local output=$2
  local path
  local directory
  : >"$output"
  while IFS= read -r path; do
    directory=${path%/*}
    [ "$directory" != "$path" ] || continue
    while :; do
      printf '%s\n' "$directory" >>"$output"
      case $directory in
        */*) directory=${directory%/*} ;;
        *) break ;;
      esac
    done
  done <"$file_inventory"
  LC_ALL=C sort -u -o "$output" "$output"
}

release_host_os() {
  local kernel
  kernel=$(uname -s) || return
  case $kernel in
    Linux*) printf 'linux\n' ;;
    Darwin*) printf 'macos\n' ;;
    MINGW*|MSYS*|CYGWIN*) printf 'windows\n' ;;
    *) release_die "unsupported build host OS: $kernel" ;;
  esac
}

release_normalize_architecture() {
  local value=${1-}
  value=$(printf '%s' "$value" | LC_ALL=C tr '[:lower:]' '[:upper:]') || return
  case $value in
    AMD64|X64|X86_64) printf 'x64\n' ;;
    ARM64|AARCH64) printf 'arm64\n' ;;
    *) release_die "unsupported host architecture: ${value:-<empty>}" ;;
  esac
}

release_host_architecture() {
  local host=$1
  local machine
  if [ "$host" = windows ]; then
    # Git for Windows can itself be x64-emulated on an ARM64 OS, so uname -m
    # describes the Bash process rather than the native Windows architecture.
    # PROCESSOR_ARCHITEW6432 is Windows' native-architecture value for an
    # emulated process; otherwise PROCESSOR_ARCHITECTURE is authoritative.
    if [ -n "${PROCESSOR_ARCHITEW6432-}" ]; then
      machine=$PROCESSOR_ARCHITEW6432
    elif [ -n "${PROCESSOR_ARCHITECTURE-}" ]; then
      machine=$PROCESSOR_ARCHITECTURE
    else
      release_die 'Windows native architecture environment is unavailable'
    fi
  else
    machine=$(uname -m) || return
  fi
  release_normalize_architecture "$machine"
}

release_host_rid() {
  local host
  host=$(release_host_os) || return
  local architecture
  architecture=$(release_host_architecture "$host") || return
  case $host in
    windows) printf 'win-%s\n' "$architecture" ;;
    macos) printf 'osx-%s\n' "$architecture" ;;
    linux)
      if [ -f /etc/alpine-release ] || ldd --version 2>&1 | grep -qi musl; then
        printf 'linux-musl-%s\n' "$architecture"
      else
        printf 'linux-%s\n' "$architecture"
      fi
      ;;
  esac
}

release_archive_name() {
  local version=$1
  local rid=$2
  local format
  format=$(release_target_field "$rid" 2) || return
  printf 'dotnetjq-%s-%s.%s\n' "$version" "$rid" "$format"
}

release_verify_binary_format() {
  local path=$1
  local rid=$2
  release_require_command file
  local description
  # A `dotnet tool --tool-path` command is a symlink on Unix. Verify the
  # executable it resolves to; archive verifiers reject links before calling
  # this helper, so following here does not weaken archive inventory checks.
  description=$(file -Lb -- "$path") ||
    release_die "could not inspect executable format: $path"
  case $rid in
    win-x64)
      case $description in *PE32+*x86-64*) ;; *) release_die "$rid binary has unexpected format: $description" ;; esac
      ;;
    win-arm64)
      case $description in *PE32+*Aarch64*|*PE32+*ARM64*) ;; *) release_die "$rid binary has unexpected format: $description" ;; esac
      ;;
    linux-x64)
      case $description in *ELF\ 64-bit*x86-64*interpreter*ld-linux*) ;; *) release_die "$rid binary is not an x64 glibc ELF: $description" ;; esac
      ;;
    linux-arm64)
      case $description in *ELF\ 64-bit*ARM\ aarch64*interpreter*ld-linux*) ;; *) release_die "$rid binary is not an ARM64 glibc ELF: $description" ;; esac
      ;;
    linux-musl-x64)
      case $description in *ELF\ 64-bit*x86-64*interpreter*ld-musl*) ;; *) release_die "$rid binary is not an x64 musl ELF: $description" ;; esac
      ;;
    linux-musl-arm64)
      case $description in *ELF\ 64-bit*ARM\ aarch64*interpreter*ld-musl*) ;; *) release_die "$rid binary is not an ARM64 musl ELF: $description" ;; esac
      ;;
    osx-x64)
      case $description in *Mach-O\ 64-bit*x86_64*) ;; *) release_die "$rid binary has unexpected format: $description" ;; esac
      ;;
    osx-arm64)
      case $description in *Mach-O\ 64-bit*arm64*) ;; *) release_die "$rid binary has unexpected format: $description" ;; esac
      ;;
    *) release_die "cannot verify binary format for unsupported RID: $rid" ;;
  esac
}

release_template_escape() {
  printf '%s' "$1" | sed 's/[\\&|]/\\&/g'
}

release_render_template() {
  local template=$1
  local destination=$2
  shift 2
  cp -- "$template" "$destination"
  while [ "$#" -gt 0 ]; do
    [ "$#" -ge 2 ] || release_die 'template replacement requires token/value pairs'
    local token=$1
    local value=$2
    shift 2
    local escaped
    escaped=$(release_template_escape "$value") ||
      release_die "could not escape template value for token: $token"
    sed "s|$token|$escaped|g" "$destination" >"$destination.next"
    mv -- "$destination.next" "$destination"
  done
  if grep -E '__[A-Z0-9_]+__' "$destination" >/dev/null 2>&1; then
    release_die "unresolved template token in $destination"
  elif [ "$?" -ne 1 ]; then
    release_die "could not scan rendered template for unresolved tokens: $destination"
  fi
}

release_assert_new_or_identical() {
  local candidate=$1
  local destination=$2
  if [ -e "$destination" ]; then
    cmp -s -- "$candidate" "$destination" ||
      release_die "refusing to overwrite a different file: $destination"
    rm -f -- "$candidate"
  else
    mv -- "$candidate" "$destination"
  fi
}

release_make_temp_dir() {
  local parent=$1
  local label=$2
  mkdir -p -- "$parent"
  mktemp -d "$parent/.${label}.XXXXXX"
}
