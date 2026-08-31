#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "${script_dir}/.." && pwd -P)"
output_directory="artifacts/payload"
runtime_identifier="linux-x64"
version_file="$repo_root/VERSION"
[[ -f "$version_file" ]] || { printf 'Error: canonical VERSION file was not found: %s\n' "$version_file" >&2; exit 1; }
version="$(tr -d '\r\n' < "$version_file")"
[[ -n "$version" ]] || { printf 'Error: canonical VERSION file is empty: %s\n' "$version_file" >&2; exit 1; }

usage() {
    cat <<'EOF'
Usage: scripts/build.sh [options]

Options:
  -o, --output <path>   Payload output directory (default: artifacts/payload)
  -r, --runtime <rid>   Linux runtime identifier (default: linux-x64)
  -h, --help            Show this help
EOF
}

die() {
    printf 'Error: %s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        -o|--output)
            (($# >= 2)) || die "${1} requires a value."
            output_directory="$2"
            shift 2
            ;;
        -r|--runtime)
            (($# >= 2)) || die "${1} requires a value."
            runtime_identifier="$2"
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

[[ "$runtime_identifier" == linux-* ]] || die "The Linux build script only accepts Linux runtime identifiers."

project_path="$repo_root/src/Avalonia/ManiaMapAnalyzerOverlay.Avalonia.csproj"
updater_project_path="$repo_root/src/Updater/ManiaMapAnalyzerOverlay.Updater.csproj"
if [[ "$output_directory" = /* ]]; then
    output_path="$(realpath -m -- "$output_directory")"
else
    output_path="$(realpath -m -- "$repo_root/$output_directory")"
fi
repo_prefix="$repo_root/"

[[ "$output_path" == "$repo_root" || "$output_path" == "$repo_prefix"* ]] \
    || die "Output directory must be inside the repository."
[[ "$output_path" != "$repo_root" ]] || die "Output directory cannot be the repository root."
[[ ! -L "$output_path" ]] || die "Output directory cannot be a symbolic link."
[[ -f "$project_path" ]] || die "Avalonia project was not found: $project_path"
[[ -f "$updater_project_path" ]] || die "Updater project was not found: $updater_project_path"

dotnet_command="$(command -v dotnet || true)"
[[ -n "$dotnet_command" ]] || die ".NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"

if [[ -e "$output_path" ]]; then
    rm -rf -- "$output_path"
fi
mkdir -p -- "$output_path"

updater_output="$output_path/.updater-build"
mkdir -p -- "$updater_output"
"$dotnet_command" publish "$updater_project_path" \
    --configuration Release \
    --runtime "$runtime_identifier" \
    --self-contained true \
    --output "$updater_output" \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    --nologo

updater_binary="$updater_output/Mania Map Analyzer Overlay.Updater"
[[ -f "$updater_binary" ]] || die "Published updater was not found: $updater_binary"

"$dotnet_command" publish "$project_path" \
    --configuration Release \
    --runtime "$runtime_identifier" \
    --self-contained true \
    --output "$output_path" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    -p:EmbeddedUpdaterPath="$updater_binary" \
    --nologo
rm -rf -- "$updater_output"

launcher_binary="$output_path/Mania Map Analyzer Overlay"
[[ -f "$launcher_binary" ]] || die "Published launcher was not found: $launcher_binary"
chmod +x -- "$launcher_binary"
payload_count="$(find "$output_path" -mindepth 1 -maxdepth 1 -print | wc -l)"
[[ "$payload_count" -eq 1 ]] || die "Single-file payload must contain only the launcher executable."
verification_root="$(mktemp -d)"
trap 'rm -rf -- "$verification_root"' EXIT
"$launcher_binary" --verify-runtime-package "$verification_root"
rm -rf -- "$verification_root"
trap - EXIT

printf 'Mania Map Analyzer Overlay %s built at: %s\n' "$version" "$output_path"
printf 'Launch the application executable; component setup runs inside the GUI.\n'
