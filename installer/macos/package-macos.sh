#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
OUT_DIR="artifacts/${RID}"
APP_BUNDLE="artifacts/DesktopNotes.app"

dotnet restore --locked-mode
dotnet test -c Release
dotnet publish src/DesktopNotes.Desktop -c Release -r "${RID}" --self-contained true -p:PublishTrimmed=false -o "${OUT_DIR}"

rm -rf "${APP_BUNDLE}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS" "${APP_BUNDLE}/Contents/Resources"
cp installer/macos/Info.plist "${APP_BUNDLE}/Contents/Info.plist"
cp -R "${OUT_DIR}/"* "${APP_BUNDLE}/Contents/MacOS/"
chmod +x "${APP_BUNDLE}/Contents/MacOS/DesktopNotes.Desktop"

echo "Built ${APP_BUNDLE} for ${RID}. Run codesign & notarytool on a physical macOS release runner before distribution."
