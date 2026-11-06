#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo"
arch=$(uname -m)
if [ "$arch" = x86_64 ]; then arch=x64; fi
case "$arch" in arm64|x64) ;; *) printf 'Unsupported Mac architecture: %s\n' "$arch" >&2; exit 1 ;; esac
sdk=${CAPRI_DOTNET:-dotnet}
signing="$repo/out/codesign-identity"
if [ -n "${CAPRI_CODESIGN_IDENTITY:-}" ]; then
  identity=$CAPRI_CODESIGN_IDENTITY
elif [ -f "$signing" ]; then
  identity=$(cat "$signing")
  if [ -z "$identity" ] || [ "$identity" = - ]; then
    printf 'Invalid saved signing identity: %s\n' "$signing" >&2
    exit 1
  fi
else
  identity=-
fi
if [ "$identity" != - ]; then
  security find-identity -v -p codesigning | python3 -c '
import re, sys
requested = sys.argv[1]
matches = [fingerprint for fingerprint, name in re.findall(r"\b([0-9A-Fa-f]{40})\s+\"([^\"]+)\"", sys.stdin.read()) if fingerprint.lower() == requested.lower() or name == requested]
if len(matches) != 1:
    sys.exit("Signing identity unavailable or ambiguous. Fix the certificate in Keychain; Capri has not been rebuilt. No ad-hoc fallback was used.")
' "$identity"
fi
publish="$repo/out/dsh-mac-$arch"
bundle=${CAPRI_APP_BUNDLE:-${CAPRI_PREVIEW_BUNDLE:-"$repo/out/Capri.app"}}
"$sdk" publish cross/Capri.Cross.csproj -c Release -r "osx-$arch" --self-contained true -o "$publish" -p:DebugType=none
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$publish/." "$bundle/Contents/MacOS/"
rm -f "$bundle/Contents/MacOS/capri-preview"
python3 - "$repo" "$bundle" <<'PY'
import pathlib, plistlib, re, sys
repo, bundle = map(pathlib.Path, sys.argv[1:])
version = re.search(r'<Version>(.*?)</Version>', (repo / 'cross/Capri.Cross.csproj').read_text()).group(1)
data = plistlib.loads((repo / 'cross/mac/Info.plist').read_bytes().replace(b'@VERSION@', version.encode()))
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
codesign --force --deep --sign "$identity" --entitlements cross/mac/Capri.entitlements "$bundle"
codesign --verify --deep --strict "$bundle"
printf '\nFinder-ready Capri: %s\n' "$bundle"
