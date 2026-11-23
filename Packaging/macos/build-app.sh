#!/bin/sh
set -eu

case "${1:-}" in
  osx-arm64|osx-x64) rid=$1 ;;
  *) echo "Usage: sh Packaging/macos/build-app.sh osx-arm64|osx-x64" >&2; exit 2 ;;
esac

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
bundle="$root/bin/publish/$rid/ProtoDeck.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
dotnet publish "$root/ProtoDeck.csproj" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -o "$bundle/Contents/MacOS"
cp "$root/Packaging/macos/Info.plist" "$bundle/Contents/Info.plist"
cp "$root/Assets/icon.icns" "$bundle/Contents/Resources/icon.icns"
echo "App bundle: $bundle"
