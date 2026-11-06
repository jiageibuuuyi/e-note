import io
import os
import sys
import tarfile
from pathlib import Path

def make_ar_header(name: str, size: int, mtime: int = 1700000000, uid: int = 0, gid: int = 0, mode: int = 0o100644) -> bytes:
    name_field = f"{name:<16}"
    mtime_field = f"{mtime:<12}"
    uid_field = f"{uid:<6}"
    gid_field = f"{gid:<6}"
    mode_field = f"{oct(mode)[2:]:<8}"
    size_field = f"{size:<10}"
    trailer = b"`\n"
    header = (name_field + mtime_field + uid_field + gid_field + mode_field + size_field).encode("ascii") + trailer
    assert len(header) == 60, f"Header size is {len(header)}, expected 60"
    return header

def build_portable_tarball(repo_root: Path, publish_dir: Path, output_tar: Path):
    print(f"Building portable tarball: {output_tar.name}...")
    if output_tar.exists():
        output_tar.unlink()

    desktop_file = repo_root / "installer" / "linux" / "desktopnotes.desktop"
    icon_file = repo_root / "src" / "DesktopNotes.Desktop" / "Assets" / "Icons" / "app-icon.png"

    install_sh_content = """#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}"

if [ "$(id -u)" -eq 0 ]; then
    INSTALL_PREFIX="/opt/e-note"
    BIN_DIR="/usr/bin"
    APP_DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/256x256/apps"
else
    INSTALL_PREFIX="${HOME}/.local/share/e-note"
    BIN_DIR="${HOME}/.local/bin"
    APP_DESKTOP_DIR="${HOME}/.local/share/applications"
    ICON_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"
fi

echo "=========================================="
echo " Installing e-note (Desktop Notes)"
echo " Target directory: ${INSTALL_PREFIX}"
echo " Binary directory: ${BIN_DIR}"
echo "=========================================="

mkdir -p "${INSTALL_PREFIX}"
mkdir -p "${BIN_DIR}"
mkdir -p "${APP_DESKTOP_DIR}"
mkdir -p "${ICON_DIR}"

echo "Copying application binaries..."
cp -r "${APP_DIR}/bin/"* "${INSTALL_PREFIX}/"
chmod 755 "${INSTALL_PREFIX}/DesktopNotes.Desktop"

echo "Installing launcher & desktop icon..."
cp "${APP_DIR}/e-note.png" "${ICON_DIR}/e-note.png"

# Create standard desktop file
cat <<EOF > "${APP_DESKTOP_DIR}/e-note.desktop"
[Desktop Entry]
Type=Application
Name=e-note
GenericName=Desktop Todo and Notes
Comment=Lightweight desktop todo list and note manager
Exec=${INSTALL_PREFIX}/DesktopNotes.Desktop
Icon=e-note
Terminal=false
Categories=Utility;Office;
StartupNotify=false
EOF
chmod 644 "${APP_DESKTOP_DIR}/e-note.desktop"

# Create command runner scripts
cat <<EOF > "${BIN_DIR}/e-note"
#!/bin/sh
exec "${INSTALL_PREFIX}/DesktopNotes.Desktop" "\\$@"
EOF
chmod 755 "${BIN_DIR}/e-note"

# Symlink alias desktopnotes for backwards compatibility
ln -sf "${BIN_DIR}/e-note" "${BIN_DIR}/desktopnotes" 2>/dev/null || true

# Refresh desktop database & icon cache
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "${APP_DESKTOP_DIR}" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f "$(dirname "$(dirname "${ICON_DIR}")")" >/dev/null 2>&1 || true
fi

echo "=========================================="
echo " Installation complete!"
echo " You can launch 'e-note' from your application menu,"
echo " or run 'e-note' from the terminal."
echo "=========================================="
""".encode("utf-8")

    uninstall_sh_content = """#!/usr/bin/env bash
set -e

if [ "$(id -u)" -eq 0 ]; then
    INSTALL_PREFIX="/opt/e-note"
    BIN_LINK="/usr/bin/e-note"
    BIN_ALIAS="/usr/bin/desktopnotes"
    DESKTOP_FILE="/usr/share/applications/e-note.desktop"
    ICON_FILE="/usr/share/icons/hicolor/256x256/apps/e-note.png"
else
    INSTALL_PREFIX="${HOME}/.local/share/e-note"
    BIN_LINK="${HOME}/.local/bin/e-note"
    BIN_ALIAS="${HOME}/.local/bin/desktopnotes"
    DESKTOP_FILE="${HOME}/.local/share/applications/e-note.desktop"
    ICON_FILE="${HOME}/.local/share/icons/hicolor/256x256/apps/e-note.png"
fi

echo "Uninstalling e-note..."
rm -rf "${INSTALL_PREFIX}"
rm -f "${BIN_LINK}"
rm -f "${BIN_ALIAS}"
rm -f "${DESKTOP_FILE}"
rm -f "${ICON_FILE}"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$(dirname "${DESKTOP_FILE}")" >/dev/null 2>&1 || true
fi

echo "Uninstallation complete!"
""".encode("utf-8")

    readme_content = """e-note (Desktop Notes) Linux Release
======================================================
Requirements: 64-bit Linux distribution (Ubuntu, Debian, Fedora, Arch Linux, openSUSE, etc.)

Method 1 (One-click Installation):
  - System-wide install: sudo ./install.sh
  - Current user install: ./install.sh (No root/sudo required)

Method 2 (Portable / Run Directly):
  - Run ./bin/DesktopNotes.Desktop directly.

Uninstallation:
  - Run ./uninstall.sh (use sudo if installed globally).
======================================================
""".encode("utf-8")

    prefix = "e-note-v1.0-linux-x64"

    with tarfile.open(output_tar, "w:gz") as tar:
        ti = tarfile.TarInfo(name=prefix)
        ti.type = tarfile.DIRTYPE
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti)

        ti = tarfile.TarInfo(name=f"{prefix}/install.sh")
        ti.size = len(install_sh_content)
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(install_sh_content))

        ti = tarfile.TarInfo(name=f"{prefix}/uninstall.sh")
        ti.size = len(uninstall_sh_content)
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(uninstall_sh_content))

        ti = tarfile.TarInfo(name=f"{prefix}/README.txt")
        ti.size = len(readme_content)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(readme_content))

        with open(desktop_file, "rb") as f:
            dbytes = f.read()
        ti = tarfile.TarInfo(name=f"{prefix}/e-note.desktop")
        ti.size = len(dbytes)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(dbytes))

        with open(icon_file, "rb") as f:
            ibytes = f.read()
        ti = tarfile.TarInfo(name=f"{prefix}/e-note.png")
        ti.size = len(ibytes)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(ibytes))

        ti = tarfile.TarInfo(name=f"{prefix}/bin")
        ti.type = tarfile.DIRTYPE
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti)

        for root, dirs, files in os.walk(publish_dir):
            rel_root = Path(root).relative_to(publish_dir)
            for d in dirs:
                dir_arc = f"{prefix}/bin/{rel_root / d}".replace("\\", "/")
                ti = tarfile.TarInfo(name=dir_arc)
                ti.type = tarfile.DIRTYPE
                ti.mode = 0o755
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti)

            for f in files:
                file_path = Path(root) / f
                sub_file = rel_root / f if str(rel_root) != "." else Path(f)
                file_arc = f"{prefix}/bin/{sub_file}".replace("\\", "/")
                is_exec = (f == "DesktopNotes.Desktop" or f == "createdump" or f.endswith(".so"))
                
                with open(file_path, "rb") as fp:
                    content = fp.read()
                    
                ti = tarfile.TarInfo(name=file_arc)
                ti.size = len(content)
                ti.mode = 0o755 if is_exec else 0o644
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti, io.BytesIO(content))

    size_mb = output_tar.stat().st_size / (1024 * 1024)
    print(f"Successfully built {output_tar.name} ({size_mb:.2f} MB)")


def build_deb_package(repo_root: Path, publish_dir: Path, output_deb: Path):
    print(f"Building Debian (.deb) package: {output_deb.name}...")
    if output_deb.exists():
        output_deb.unlink()

    desktop_file = repo_root / "installer" / "linux" / "desktopnotes.desktop"
    icon_file = repo_root / "src" / "DesktopNotes.Desktop" / "Assets" / "Icons" / "app-icon.png"

    total_size = sum(f.stat().st_size for f in publish_dir.rglob("*") if f.is_file())
    installed_size_kb = int(total_size / 1024)

    control_content = f"""Package: e-note
Version: 1.0.0
Architecture: amd64
Maintainer: e-note Team <support@desktopnotes.app>
Installed-Size: {installed_size_kb}
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1
Provides: desktopnotes
Section: utils
Priority: optional
Homepage: https://github.com/desktopnotes/desktopnotes
Description: e-note - Lightweight desktop todo and note manager
 A lightweight, cross-platform desktop sticky note and task management application supporting pinning, subtasks, progress tracking, and recycle bin.
""".encode("utf-8")

    postinst_content = b"""#!/bin/sh
set -e

chmod 755 /opt/e-note/DesktopNotes.Desktop || true
chmod 755 /usr/bin/e-note || true
ln -sf /usr/bin/e-note /usr/bin/desktopnotes 2>/dev/null || true

if [ -x /usr/bin/update-desktop-database ]; then
    /usr/bin/update-desktop-database -q /usr/share/applications || true
fi
if [ -x /usr/bin/gtk-update-icon-cache ]; then
    /usr/bin/gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
"""

    postrm_content = b"""#!/bin/sh
set -e

if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    rm -f /usr/bin/e-note
    rm -f /usr/bin/desktopnotes
    rm -rf /opt/e-note
    if [ -x /usr/bin/update-desktop-database ]; then
        /usr/bin/update-desktop-database -q /usr/share/applications || true
    fi
    if [ -x /usr/bin/gtk-update-icon-cache ]; then
        /usr/bin/gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
    fi
fi
exit 0
"""

    control_buf = io.BytesIO()
    with tarfile.open(fileobj=control_buf, mode="w:gz") as tar:
        ti = tarfile.TarInfo(name="./control")
        ti.size = len(control_content)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(control_content))

        ti = tarfile.TarInfo(name="./postinst")
        ti.size = len(postinst_content)
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(postinst_content))

        ti = tarfile.TarInfo(name="./postrm")
        ti.size = len(postrm_content)
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(postrm_content))

    control_tar_gz = control_buf.getvalue()

    data_buf = io.BytesIO()
    with tarfile.open(fileobj=data_buf, mode="w:gz") as tar:
        for dname in [
            "./opt",
            "./opt/e-note",
            "./usr",
            "./usr/bin",
            "./usr/share",
            "./usr/share/applications",
            "./usr/share/icons",
            "./usr/share/icons/hicolor",
            "./usr/share/icons/hicolor/256x256",
            "./usr/share/icons/hicolor/256x256/apps",
        ]:
            ti = tarfile.TarInfo(name=dname)
            ti.type = tarfile.DIRTYPE
            ti.mode = 0o755
            ti.uname = "root"
            ti.gname = "root"
            tar.addfile(ti)

        launcher_content = b"""#!/bin/sh
exec /opt/e-note/DesktopNotes.Desktop "$@"
"""
        ti = tarfile.TarInfo(name="./usr/bin/e-note")
        ti.size = len(launcher_content)
        ti.mode = 0o755
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(launcher_content))

        with open(desktop_file, "rb") as f:
            dbytes = f.read()
        ti = tarfile.TarInfo(name="./usr/share/applications/e-note.desktop")
        ti.size = len(dbytes)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(dbytes))

        with open(icon_file, "rb") as f:
            ibytes = f.read()
        ti = tarfile.TarInfo(name="./usr/share/icons/hicolor/256x256/apps/e-note.png")
        ti.size = len(ibytes)
        ti.mode = 0o644
        ti.uname = "root"
        ti.gname = "root"
        tar.addfile(ti, io.BytesIO(ibytes))

        for root, dirs, files in os.walk(publish_dir):
            rel_root = Path(root).relative_to(publish_dir)
            for d in dirs:
                dir_arc = f"./opt/e-note/{rel_root / d}".replace("\\", "/")
                ti = tarfile.TarInfo(name=dir_arc)
                ti.type = tarfile.DIRTYPE
                ti.mode = 0o755
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti)

            for f in files:
                file_path = Path(root) / f
                sub_file = rel_root / f if str(rel_root) != "." else Path(f)
                file_arc = f"./opt/e-note/{sub_file}".replace("\\", "/")
                is_exec = (f == "DesktopNotes.Desktop" or f == "createdump" or f.endswith(".so"))
                
                with open(file_path, "rb") as fp:
                    content = fp.read()
                    
                ti = tarfile.TarInfo(name=file_arc)
                ti.size = len(content)
                ti.mode = 0o755 if is_exec else 0o644
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti, io.BytesIO(content))

    data_tar_gz = data_buf.getvalue()

    deb_binary = b"2.0\n"

    with open(output_deb, "wb") as deb:
        deb.write(b"!<arch>\n")
        deb.write(make_ar_header("debian-binary", len(deb_binary)))
        deb.write(deb_binary)
        if len(deb_binary) % 2 != 0:
            deb.write(b"\n")

        deb.write(make_ar_header("control.tar.gz", len(control_tar_gz)))
        deb.write(control_tar_gz)
        if len(control_tar_gz) % 2 != 0:
            deb.write(b"\n")

        deb.write(make_ar_header("data.tar.gz", len(data_tar_gz)))
        deb.write(data_tar_gz)
        if len(data_tar_gz) % 2 != 0:
            deb.write(b"\n")

    size_mb = output_deb.stat().st_size / (1024 * 1024)
    print(f"Successfully built {output_deb.name} ({size_mb:.2f} MB)")


if __name__ == "__main__":
    repo = Path(__file__).resolve().parent.parent.parent
    pub_linux = repo / "publish" / "linux-x64"
    if not pub_linux.exists():
        print(f"Error: {pub_linux} does not exist.")
        sys.exit(1)

    build_portable_tarball(repo, pub_linux, repo / "e-note-v1.0-linux-x64.tar.gz")
    build_deb_package(repo, pub_linux, repo / "e-note_1.0.0_amd64.deb")
