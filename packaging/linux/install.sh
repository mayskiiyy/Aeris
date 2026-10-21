#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
[[ $(id -u) != 0 ]] || { echo 'Run this installer as your desktop user, without sudo.' >&2; exit 1; }
build=$(cat build-id)
[[ "$build" =~ ^[0-9]+\.[0-9]+\.[0-9]+-linux-(x64|arm64)$ ]] || { echo 'Invalid build ID' >&2; exit 1; }
case "$(uname -m):$build" in x86_64:*-x64|aarch64:*-arm64) ;; *) echo 'This build does not match your CPU.' >&2; exit 1 ;; esac
base="$HOME/.local/opt/aeris"
target="$base/$build"
bin="$HOME/.local/bin"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
[[ ! -e "$target" && ! -L "$target" ]] || { echo "$target already exists; close Aeris and use a new release directory." >&2; exit 1; }
for command in aeris aeris-cli; do
    [[ ! -e "$bin/$command" || -L "$bin/$command" ]] || { echo "Refusing to replace existing file $bin/$command" >&2; exit 1; }
done
mkdir -p "$base" "$bin" "$data/applications" "$data/icons/hicolor/scalable/apps"
stage=$(mktemp -d "$base/.install.XXXXXXXX")
trap '[[ -z "$stage" ]] || rm -rf -- "$stage"' EXIT
cp -R . "$stage/"
chmod 755 "$stage/Aeris.Desktop" "$stage/aeris-cli"
mv -- "$stage" "$target"
stage=''
ln -sfn -- "$target/Aeris.Desktop" "$bin/aeris"
ln -sfn -- "$target/aeris-cli" "$bin/aeris-cli"
cp "$target/aeris.svg" "$data/icons/hicolor/scalable/apps/aeris.svg"
# Desktop Entry Exec has its own quoting rules, including doubled percent signs.
exe="$bin/aeris"
exe=${exe//\\/\\\\}
exe=${exe//\"/\\\"}
exe=${exe//\$/\\\$}
exe=${exe//\`/\\\`}
exe=${exe//%/%%}
cat > "$data/applications/io.github.mayskiiyy.Aeris.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Aeris
Comment=Analyze disk usage and review cache cleanup
Exec="$exe"
Icon=aeris
Terminal=false
Categories=Utility;FileTools;
StartupWMClass=Aeris.Desktop
EOF
if command -v update-desktop-database >/dev/null; then update-desktop-database "$data/applications"; fi
printf 'Installed to %s\nLaunch Aeris from your applications menu or %s/aeris\n' "$target" "$bin"
