#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "${script_dir}/.." && pwd -P)"
output_directory="artifacts/payload"
runtime_identifier="linux-x64"

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

"$dotnet_command" publish "$project_path" \
    --configuration Release \
    --runtime "$runtime_identifier" \
    --self-contained true \
    --output "$output_path" \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    --nologo

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
cp -- "$updater_binary" "$output_path/"
rm -rf -- "$updater_output"

cp -- "$repo_root/assets/overlay-custom.css" "$output_path/"
mkdir -p -- "$output_path/Assets/overlay"
cp -R -- "$repo_root/assets/overlay/." "$output_path/Assets/overlay/"
mkdir -p -- "$output_path/Assets/analyzers"
cp -R -- "$repo_root/assets/analyzers/." "$output_path/Assets/analyzers/"
mkdir -p -- "$output_path/Assets/analyzer-engines"
cp -R -- "$repo_root/assets/analyzer-engines/." "$output_path/Assets/analyzer-engines/"
mkdir -p -- "$output_path/Assets/localization"
cp -R -- "$repo_root/assets/localization/." "$output_path/Assets/localization/"
for asset in \
    "Assets/overlay/presets/default/manifest.json" \
    "Assets/overlay/presets/horizontal/manifest.json" \
    "Assets/overlay/presets/companella/manifest.json"; do
    [[ -f "$output_path/$asset" ]] || die "Published package is missing overlay resource: $asset"
done
for asset in \
    "Assets/localization/manifest.json" \
    "Assets/localization/en.json" \
    "Assets/localization/ru.json"; do
    [[ -f "$output_path/$asset" ]] || die "Published package is missing localization resource: $asset"
done
for asset in \
    "Assets/analyzers/mania-map-analyser/manifest.json" \
    "Assets/analyzers/mania-map-analyser/adapter.js"; do
    [[ -f "$output_path/$asset" ]] || die "Published package is missing analyzer adapter resource: $asset"
done
for asset in \
    "Assets/analyzer-engines/mania-map-analyser/manifest.json" \
    "Assets/analyzer-engines/mania-map-analyser/runtime.mjs" \
    "Assets/analyzer-engines/mania-map-analyser/worker.mjs"; do
    [[ -f "$output_path/$asset" ]] || die "Published package is missing analyzer engine resource: $asset"
done
cp -- "$repo_root/README.md" "$output_path/"
cp -- "$repo_root/LICENSE" "$output_path/"
cp -R -- "$repo_root/LICENSES" "$output_path/"
cp -R -- "$repo_root/docs" "$output_path/"

launcher_binary="$output_path/Mania Map Analyzer Overlay"
[[ -f "$launcher_binary" ]] || die "Published launcher was not found: $launcher_binary"
chmod +x -- "$launcher_binary" "$output_path/Mania Map Analyzer Overlay.Updater"

if find "$output_path" -type f \( -name '*.cmd' -o -name '*.ps1' \) -print -quit | grep -q .; then
    die "Runtime package must not contain .cmd or .ps1 files."
fi

printf 'Mania Map Analyzer Overlay 2.3.0 built at: %s\n' "$output_path"
printf 'Launch the application executable; component setup runs inside the GUI.\n'
