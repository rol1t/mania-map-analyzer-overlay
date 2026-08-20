#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "${script_dir}/.." && pwd -P)"
version="2.1.0"
runtime_identifier="linux-x64"
payload_directory="artifacts/payload"

usage() {
    cat <<'EOF'
Usage: scripts/package.sh [options]

Options:
  -v, --version <version>  Release version (default: 2.1.0)
  -r, --runtime <rid>      Linux runtime identifier (default: linux-x64)
  -p, --payload <path>     Payload directory (default: artifacts/payload)
  -h, --help               Show this help
EOF
}

die() {
    printf 'Error: %s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        -v|--version)
            (($# >= 2)) || die "${1} requires a value."
            version="$2"
            shift 2
            ;;
        -r|--runtime)
            (($# >= 2)) || die "${1} requires a value."
            runtime_identifier="$2"
            shift 2
            ;;
        -p|--payload)
            (($# >= 2)) || die "${1} requires a value."
            payload_directory="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "Unknown argument: $1"
            ;;
    esac
done

[[ "$runtime_identifier" == linux-* ]] || die "The Linux package script only accepts Linux runtime identifiers."
[[ "$version" != */* && "$version" != *\\* && "$version" != *' '* ]] \
    || die "Version must not contain path separators or spaces."

artifacts_path="$(realpath -m -- "$repo_root/artifacts")"
if [[ "$payload_directory" = /* ]]; then
    payload_path="$(realpath -m -- "$payload_directory")"
else
    payload_path="$(realpath -m -- "$repo_root/$payload_directory")"
fi
repo_prefix="$repo_root/"

[[ "$artifacts_path" == "$repo_prefix"* ]] || die "Artifacts directory must be inside the repository."
[[ "$payload_path" == "$repo_prefix"* ]] || die "Payload directory must be inside the repository."
[[ ! -L "$artifacts_path" ]] || die "Artifacts directory cannot be a symbolic link."
[[ ! -L "$payload_path" ]] || die "Payload directory cannot be a symbolic link."

launcher_binary="$payload_path/Mania Map Analyzer Overlay"
[[ -f "$launcher_binary" ]] || die "Build the launcher payload before packaging the application package."
[[ -x "$launcher_binary" ]] || die "The Linux launcher is not executable: $launcher_binary"

mkdir -p -- "$artifacts_path"
archive_path="$artifacts_path/Mania-Map-Analyzer-Overlay-${version}-${runtime_identifier}.tar.gz"
staging_path="$(mktemp -d "$artifacts_path/.installer-stage-${version}-${runtime_identifier}.XXXXXX")"
cleanup() {
    rm -rf -- "$staging_path"
}
trap cleanup EXIT

cp -a -- "$payload_path/." "$staging_path/"

if find "$staging_path" -type f \( -name '*.cmd' -o -name '*.ps1' \) -print -quit | grep -q .; then
    die "Runtime package must not contain .cmd or .ps1 files."
fi

rm -f -- "$archive_path"
tar -C "$staging_path" -czf "$archive_path" .
printf 'Application package created: %s\n' "$archive_path"
