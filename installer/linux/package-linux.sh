#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
OUT_DIR="artifacts/${RID}"
STAGE_DIR="artifacts/linux-package-root"

dotnet restore --locked-mode
dotnet test -c Release
dotnet publish src/DesktopNotes.Desktop -c Release -r "${RID}" --self-contained true -p:PublishTrimmed=false -o "${OUT_DIR}"

rm -rf "${STAGE_DIR}"
mkdir -p "${STAGE_DIR}/opt/desktopnotes" "${STAGE_DIR}/usr/share/applications"
cp -R "${OUT_DIR}/"* "${STAGE_DIR}/opt/desktopnotes/"
cp installer/linux/desktopnotes.desktop "${STAGE_DIR}/usr/share/applications/desktopnotes.desktop"
chmod +x "${STAGE_DIR}/opt/desktopnotes/DesktopNotes.Desktop"

tar -czf "artifacts/DesktopNotes-${RID}-1.0.0.tar.gz" -C "${STAGE_DIR}" .
echo "Packaged artifacts/DesktopNotes-${RID}-1.0.0.tar.gz"
