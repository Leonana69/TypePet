#!/usr/bin/env bash
#
# Build a distributable, ad-hoc-signed TypePet.app from the net10.0 head.
# Usage: packaging/macos/build-macos-bundle.sh [rid] [config]
#   rid    : osx-arm64 (default) | osx-x64
#   config : Release (default) | Debug
#
# Needs only the preinstalled macOS tools (sips, iconutil, codesign). No Apple account required —
# ad-hoc signing lets the bundle launch on the building Mac. For distribution to other Macs, see the
# Tier C (Developer ID + notarization) notes in the README.
set -euo pipefail

cd "$(dirname "$0")/../.."   # repo root

RID="${1:-osx-arm64}"
CONFIG="${2:-Release}"
APP_NAME="TypePet"
OUT="artifacts"
PUBLISH="$OUT/publish/$RID"
APP="$OUT/$APP_NAME.app"
SRC_ICON="Assets/Program/icon.png"

VERSION="$(dotnet msbuild TypePet.csproj -getProperty:Version -p:TargetFramework=net10.0 -nologo 2>/dev/null || echo '1.0.0')"
echo "==> TypePet $VERSION  ($RID, $CONFIG)"

echo "==> Publishing self-contained payload..."
rm -rf "$PUBLISH"
# Do NOT single-file / IncludeNativeLibrariesForSelfExtract: both break CoreCLR startup inside a bundle.
dotnet publish TypePet.csproj -f net10.0 -r "$RID" -c "$CONFIG" \
  --self-contained true -p:UseAppHost=true -o "$PUBLISH"

echo "==> Generating $APP_NAME.icns..."
ICONSET="$OUT/$APP_NAME.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z "$s" "$s"             "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}.png"     >/dev/null
  sips -z "$((s*2))" "$((s*2))" "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}@2x.png"  >/dev/null
done
iconutil -c icns "$ICONSET" -o "$OUT/$APP_NAME.icns"

echo "==> Assembling $APP..."
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$OUT/$APP_NAME.icns" "$APP/Contents/Resources/$APP_NAME.icns"
sed "s/@VERSION@/$VERSION/g" packaging/macos/Info.plist.in > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$APP_NAME"

echo "==> Ad-hoc signing..."
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP" || true

echo "==> Done: $APP"
echo "    Launch it with:  open \"$APP\""
