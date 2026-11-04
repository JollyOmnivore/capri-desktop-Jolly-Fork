#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo"
arch=$(uname -m)
if [ "$arch" = x86_64 ]; then arch=x64; fi
case "$arch" in arm64|x64) ;; *) printf 'Unsupported Mac architecture: %s\n' "$arch" >&2; exit 1 ;; esac
sdk=${CAPRI_DOTNET:-dotnet}
publish="$repo/out/dsh-mac-$arch"
bundle="$repo/out/Capri DSH Preview.app"
"$sdk" publish cross/Capri.Cross.csproj -c Release -r "osx-$arch" --self-contained true -o "$publish" -p:DebugType=none
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$publish/." "$bundle/Contents/MacOS/"
printf 'Open the preview Mainframe at launch.\n' > "$bundle/Contents/MacOS/capri-preview"
python3 - "$repo" "$bundle" <<'PY'
import pathlib, plistlib, re, sys
repo, bundle = map(pathlib.Path, sys.argv[1:])
version = re.search(r'<Version>(.*?)</Version>', (repo / 'cross/Capri.Cross.csproj').read_text()).group(1)
data = plistlib.loads((repo / 'cross/mac/Info.plist').read_bytes().replace(b'@VERSION@', version.encode()))
data.update(CFBundleName='Capri DSH Preview', CFBundleDisplayName='Capri DSH Preview', CFBundleIdentifier='io.github.lachydotmcg.capri.dsh-preview')
(bundle / 'Contents/Info.plist').write_bytes(plistlib.dumps(data))
PY
icons="$repo/out/dsh-icon-assets/Capri.iconset"
mkdir -p "$icons"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" cross/icon.png --out "$icons/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" cross/icon.png --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$bundle/Contents/Resources/Capri.icns"
codesign --force --deep --sign - --entitlements cross/mac/Capri.entitlements "$bundle"
codesign --verify --deep --strict "$bundle"
printf '\nFinder-ready preview: %s\n' "$bundle"
