import os
import sys
import zipfile
from pathlib import Path

def create_macos_bundle(rid: str, app_version: str = "1.0.0"):
    repo_root = Path(__file__).resolve().parent.parent.parent
    publish_dir = repo_root / "publish" / rid
    if not publish_dir.exists():
        raise FileNotFoundError(f"Publish directory not found: {publish_dir}")
    
    plist_path = repo_root / "installer" / "macos" / "Info.plist"
    icns_path = repo_root / "installer" / "macos" / "app-icon.icns"
    
    app_name = "e-note.app"
    arch_label = "arm64" if "arm64" in rid else "x64"
    zip_name = f"e-note-v1.0-macOS-{arch_label}.zip"
    zip_path = repo_root / zip_name
    
    if zip_path.exists():
        zip_path.unlink()
        
    print(f"Creating {zip_name} from {publish_dir}...")
    
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        # Root and intermediate directories
        dirs_to_add = [
            f"{app_name}/",
            f"{app_name}/Contents/",
            f"{app_name}/Contents/MacOS/",
            f"{app_name}/Contents/Resources/",
        ]
        for d in dirs_to_add:
            dinfo = zipfile.ZipInfo(d)
            dinfo.external_attr = (0o040755 << 16) | 0x10
            zf.writestr(dinfo, "")

        # 1. Add Info.plist
        plist_info = zipfile.ZipInfo(f"{app_name}/Contents/Info.plist")
        plist_info.compress_type = zipfile.ZIP_DEFLATED
        plist_info.external_attr = 0o100644 << 16
        with open(plist_path, "rb") as f:
            zf.writestr(plist_info, f.read())
            
        # 2. Add PkgInfo
        pkginfo_info = zipfile.ZipInfo(f"{app_name}/Contents/PkgInfo")
        pkginfo_info.external_attr = 0o100644 << 16
        zf.writestr(pkginfo_info, b"APPL????")
            
        # 3. Add app-icon.icns
        icns_info = zipfile.ZipInfo(f"{app_name}/Contents/Resources/app-icon.icns")
        icns_info.compress_type = zipfile.ZIP_DEFLATED
        icns_info.external_attr = 0o100644 << 16
        with open(icns_path, "rb") as f:
            zf.writestr(icns_info, f.read())
            
        # 4. Add all files in publish_dir into Contents/MacOS/
        for root, dirs, files in os.walk(publish_dir):
            rel_root = Path(root).relative_to(publish_dir)
            for d in dirs:
                sub_dir = rel_root / d
                dir_arc = f"{app_name}/Contents/MacOS/{sub_dir}".replace("\\", "/") + "/"
                dinfo = zipfile.ZipInfo(dir_arc)
                dinfo.external_attr = (0o040755 << 16) | 0x10
                zf.writestr(dinfo, "")
                
            for f in files:
                file_path = Path(root) / f
                sub_file = rel_root / f if str(rel_root) != "." else Path(f)
                file_rel = f"{app_name}/Contents/MacOS/{sub_file}".replace("\\", "/")
                zinfo = zipfile.ZipInfo(file_rel)
                # Executable files
                is_exec = (f == "DesktopNotes.Desktop" or f == "createdump" or f.endswith(".dylib") or f.endswith(".so"))
                zinfo.compress_type = zipfile.ZIP_DEFLATED
                if is_exec:
                    zinfo.external_attr = 0o100755 << 16
                else:
                    zinfo.external_attr = 0o100644 << 16
                    
                with open(file_path, "rb") as fp:
                    zf.writestr(zinfo, fp.read())

    size_mb = zip_path.stat().st_size / (1024 * 1024)
    print(f"Successfully created {zip_path.name} ({size_mb:.2f} MB)")

if __name__ == "__main__":
    create_macos_bundle("osx-arm64")
    create_macos_bundle("osx-x64")
