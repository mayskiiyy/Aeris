#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
rid=${1:-linux-x64}
case "$rid" in linux-x64|linux-arm64) ;; *) echo 'Usage: ./build-linux.sh [linux-x64|linux-arm64]' >&2; exit 2 ;; esac
version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid release version' >&2; exit 1; }
mkdir -p artifacts
stage=$(mktemp -d "$(pwd)/artifacts/linux-package.XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT
app="$stage/Aeris-$version-$rid"
mkdir -p "$app"
dotnet publish src/Aeris.Desktop -c Release -r "$rid" --self-contained true -o "$app" --nologo \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
dotnet publish src/Aeris.Cli -c Release -r "$rid" --self-contained true -o "$app" --nologo \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
cp LICENSE "$app/"
python3 - "$app" "$rid" <<'PY'
import json, pathlib, shutil, sys
app, rid = pathlib.Path(sys.argv[1]), sys.argv[2]
destination = app / 'licenses'
destination.mkdir()
for notice in pathlib.Path('packaging/linux/licenses').iterdir():
    shutil.copy2(notice, destination / notice.name)
assets = json.loads(pathlib.Path('src/Aeris.Desktop/obj/project.assets.json').read_text())
roots = [pathlib.Path(p) for p in assets['packageFolders']]
for name, library in assets['libraries'].items():
    if library['type'] != 'package':
        continue
    folder = next(p / library['path'] for p in roots if (p / library['path']).is_dir())
    target = destination / name.replace('/', '-')
    target.mkdir()
    for file in folder.iterdir():
        if file.is_file() and (file.suffix == '.nuspec' or file.name.lower().startswith(('license', 'third-party', 'notice'))):
            shutil.copy2(file, target / file.name)
runtime_notices = 0
for root in roots:
    for runtime in (root / ('microsoft.netcore.app.runtime.' + rid)).glob('*'):
        for file in runtime.iterdir():
            if file.is_file() and file.name.lower().startswith(('license', 'third-party')):
                shutil.copy2(file, destination / ('dotnet-' + runtime.name + '-' + file.name))
                runtime_notices += 1
assert runtime_notices >= 2, 'Missing .NET runtime license/notices'
PY
cp packaging/linux/install.sh "$app/"
cp src/Aeris/Assets/aeris.svg "$app/"
cp packaging/linux/aeris-cleanup.service packaging/linux/aeris-cleanup.timer "$app/"
printf '%s\n' "$version-$rid" > "$app/build-id"
chmod 755 "$app/Aeris.Desktop" "$app/aeris-cli" "$app/install.sh"
case "$(uname -m):$rid" in
  x86_64:linux-x64|aarch64:linux-arm64)
    mkdir -p "$stage/home" "$stage/data" "$stage/cache"
    HOME="$stage/home" XDG_DATA_HOME="$stage/data" XDG_CACHE_HOME="$stage/cache" "$app/aeris-cli" categories --json >/dev/null
    "$app/aeris-cli" --version
    ;;
esac
tar -C "$stage" -czf "artifacts/Aeris-$version-$rid.tar.gz" "$(basename "$app")"
tar -C "$app" -czf "artifacts/aeris-cli-$version-$rid.tar.gz" aeris-cli LICENSE licenses aeris-cleanup.service aeris-cleanup.timer
(
  cd artifacts
  sha256sum "Aeris-$version-$rid.tar.gz" "aeris-cli-$version-$rid.tar.gz" > "SHA256SUMS-$rid.txt"
)
echo "Aeris $version packages ready for $rid"
