# -*- coding: utf-8 -*-
"""Package WinIslands for macOS.

Input:  macos/build/publish/osx-arm64 and macos/build/publish/osx-x64
Output: macos/dist/<version>/WinIslands-<version>-osx-<arch>.zip (+ .dmg on macOS)

The script uses Developer ID signing and Apple notarization when the following
environment variables are present. Without them it creates an ad-hoc signed
build that can be opened after the usual macOS first-run confirmation.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

import macho_sign

ROOT = Path(__file__).resolve().parent.parent
CSPROJ = ROOT / 'src' / 'WinIslands.Mac' / 'WinIslands.Mac.csproj'
ICNS = ROOT / 'src' / 'WinIslands.Mac' / 'Assets' / 'AppIcon.icns'
PUBLISH_ROOT = ROOT / 'build' / 'publish'
DEFAULT_OUTPUT = ROOT / 'dist'
RIDS = ('osx-arm64', 'osx-x64')

FILE_ATTR = 0o100755 << 16
FILE_NOEXEC_ATTR = 0o100644 << 16
DIR_ATTR = (0o40755 << 16) | 0x10
TXT_ATTR = 0o100644 << 16

INSTALL_NOTE = """WinIslands for macOS 安装说明

1. 将 WinIslands.app 拖入「应用程序」文件夹。
2. 若首次打开出现系统安全提示，请在「系统设置 → 隐私与安全性」中允许打开，
   或右键点击 App →「打开」。正式版经过 Developer ID 签名和 Apple 公证后不会出现
   “已损坏”提示；本地 ad-hoc 构建仅用于开发测试。
3. 启动后应用常驻菜单栏（无 Dock 图标），点击菜单栏图标可显示/隐藏灵动岛。
4. 媒体控制支持 Apple Music 与 Spotify，请在「系统设置 → 隐私与安全性 → 自动化」
   中允许 WinIslands 控制对应播放器。Cider 通过本地 HTTP API 连接。

文件校验值见同目录的 SHA256SUMS.txt。
"""


def read_version() -> str:
    match = re.search(r'<Version>([^<]+)</Version>', CSPROJ.read_text(encoding='utf-8'))
    if not match:
        raise SystemExit(f'Cannot find <Version> in {CSPROJ}')
    return match.group(1).strip()


def info_plist(version: str) -> str:
    return f'''<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>WinIslands</string>
    <key>CFBundleDisplayName</key><string>WinIslands</string>
    <key>CFBundleIdentifier</key><string>com.winislands.mac</string>
    <key>CFBundleVersion</key><string>{version}</string>
    <key>CFBundleShortVersionString</key><string>{version}</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>WinIslands</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSUIElement</key><true/>
    <key>NSAppleEventsUsageDescription</key><string>WinIslands uses Apple Events to read and control your music player.</string>
</dict>
</plist>
'''


def run(command: list[str]) -> None:
    print('  $', ' '.join(command), flush=True)
    subprocess.run(command, check=True)


def ensure_ad_hoc_signed(macos_dir: Path) -> None:
    """Sign thin Mach-O executables when cross-compiling on Windows/Linux."""
    signed = 0
    for path in sorted(macos_dir.iterdir()):
        if not path.is_file():
            continue
        result = macho_sign.analyze_file(str(path))
        if result is None:
            continue
        if isinstance(result, list):
            for cputype, info in result:
                if not info['signed'] or not info['valid']:
                    raise SystemExit(f'FAT binary signature is invalid: {path.name} [{cputype}]')
            continue
        info = result
        if info['signed'] and info['valid']:
            continue
        if info['filetype'] != 2:  # MH_EXECUTE
            raise SystemExit(f'Unsigned non-executable Mach-O: {path.name}')
        info = macho_sign.sign_file(str(path), ident=path.stem)
        if not info['valid']:
            raise SystemExit(f'ad-hoc signing failed: {path.name}')
        print(f'    ad-hoc signed {path.name}')
        signed += 1
    if not signed:
        print('    all Mach-O files already carry a valid signature')


def sign_app(app: Path, identity: str | None) -> None:
    if sys.platform != 'darwin':
        ensure_ad_hoc_signed(app / 'Contents' / 'MacOS')
        return
    if identity:
        # Sign nested Mach-O files first, then the app bundle. This is the
        # ordering notarytool expects for a self-contained .app.
        for path in sorted((app / 'Contents' / 'MacOS').iterdir()):
            if path.is_file() and is_macho(path):
                run(['codesign', '--force', '--options', 'runtime', '--timestamp',
                     '--sign', identity, str(path)])
        run(['codesign', '--force', '--options', 'runtime', '--timestamp',
             '--sign', identity, str(app)])
        run(['codesign', '--verify', '--deep', '--strict', '--verbose=2', str(app)])
    else:
        # Ad-hoc signing is enough for local development, not for distribution.
        run(['codesign', '--force', '--deep', '--sign', '-', str(app)])


def is_macho(path: Path) -> bool:
    try:
        with path.open('rb') as handle:
            return handle.read(4) in (
                b'\xcf\xfa\xed\xfe', b'\xce\xfa\xed\xfe',
                b'\xca\xfe\xba\xbe', b'\xca\xfe\xba\xbf',
            )
    except OSError:
        return False


def assemble_app(rid: str, version: str, stage: Path) -> Path:
    src = PUBLISH_ROOT / rid
    if not src.is_dir():
        raise SystemExit(f'Missing publish output: {src}. Run dotnet publish first.')
    app = stage / 'WinIslands.app'
    if app.exists():
        shutil.rmtree(app)
    macos = app / 'Contents' / 'MacOS'
    resources = app / 'Contents' / 'Resources'
    macos.mkdir(parents=True)
    resources.mkdir(parents=True)

    count = 0
    for item in src.iterdir():
        if item.name.lower().endswith('.pdb'):
            continue
        target = macos / item.name
        if item.is_dir():
            shutil.copytree(item, target, symlinks=True)
        else:
            shutil.copy2(item, target)
        count += 1
    shutil.copy2(ICNS, resources / 'AppIcon.icns')
    (app / 'Contents' / 'Info.plist').write_text(info_plist(version), encoding='utf-8', newline='\n')
    (stage / '安装说明.txt').write_text(INSTALL_NOTE, encoding='utf-8', newline='\n')
    print(f'  {rid}: assembled {count} items -> {app}')
    return app


def add_tree(zf: zipfile.ZipFile, base: Path, rel_root: str) -> None:
    root = base / rel_root
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        current = Path(dirpath)
        rel = current.relative_to(base).as_posix()
        if rel != rel_root:
            zinfo = zipfile.ZipInfo(rel + '/')
            zinfo.external_attr = DIR_ATTR
            zf.writestr(zinfo, b'')
        for filename in sorted(filenames):
            file = current / filename
            arc = file.relative_to(base).as_posix()
            zinfo = zipfile.ZipInfo(arc)
            zinfo.compress_type = zipfile.ZIP_DEFLATED
            zinfo._compresslevel = 9
            zinfo.external_attr = FILE_ATTR if is_macho(file) else FILE_NOEXEC_ATTR
            zf.writestr(zinfo, file.read_bytes())


def make_zip(stage: Path, destination: Path) -> None:
    if sys.platform == 'darwin':
        # ditto preserves symlinks, extended attributes and code signatures.
        destination.unlink(missing_ok=True)
        run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent',
             str(stage / 'WinIslands.app'), str(destination)])
        return
    destination.unlink(missing_ok=True)
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        note = stage / '安装说明.txt'
        zinfo = zipfile.ZipInfo('安装说明.txt')
        zinfo.external_attr = TXT_ATTR
        zf.writestr(zinfo, note.read_bytes())
        add_tree(zf, stage, 'WinIslands.app')


def make_dmg(stage: Path, destination: Path, version: str) -> None:
    if sys.platform != 'darwin':
        print('  DMG skipped: hdiutil is only available on macOS')
        return
    destination.unlink(missing_ok=True)
    run(['hdiutil', 'create', '-volname', f'WinIslands {version}',
         '-srcfolder', str(stage), '-ov', '-format', 'UDZO', str(destination)])


def notarize(app: Path, identity: str | None) -> None:
    if sys.platform != 'darwin' or not identity:
        print('  notarization skipped: Developer ID identity is not configured')
        return
    apple_id = os.environ.get('APPLE_ID')
    team_id = os.environ.get('APPLE_TEAM_ID')
    password = os.environ.get('APPLE_APP_PASSWORD')
    if not (apple_id and team_id and password):
        print('  notarization skipped: APPLE_ID / APPLE_TEAM_ID / APPLE_APP_PASSWORD are incomplete')
        return
    with tempfile.TemporaryDirectory(prefix='winislands-notary-') as td:
        zip_path = Path(td) / 'notarize.zip'
        run(['ditto', '-c', '-k', '--keepParent', str(app), str(zip_path)])
        run(['xcrun', 'notarytool', 'submit', str(zip_path),
             '--apple-id', apple_id, '--team-id', team_id, '--password', password, '--wait'])
    run(['xcrun', 'stapler', 'staple', str(app)])
    run(['xcrun', 'stapler', 'validate', str(app)])


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b''):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description='Package WinIslands for macOS')
    parser.add_argument('--version', default=None, help='release version; defaults to csproj Version')
    parser.add_argument('--output-dir', default=None, help='output directory; defaults to macos/dist/<version>')
    parser.add_argument('--identity', default=os.environ.get('MACOS_SIGN_IDENTITY'),
                        help='Developer ID Application identity')
    args = parser.parse_args()

    version = args.version or read_version()
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise SystemExit(f'Invalid version: {version}')
    output = Path(args.output_dir) if args.output_dir else DEFAULT_OUTPUT / version
    output.mkdir(parents=True, exist_ok=True)
    print(f'WinIslands macOS package v{version} -> {output}')

    results: list[tuple[Path, str]] = []
    for rid in RIDS:
        arch = rid.rsplit('-', 1)[-1]
        stage = ROOT / 'build' / 'package' / rid
        if stage.exists():
            shutil.rmtree(stage)
        stage.mkdir(parents=True)
        app = assemble_app(rid, version, stage)
        sign_app(app, args.identity)
        notarize(app, args.identity)
        # Build archive after notarization so the notarization ticket is included.
        make_zip(stage, output / f'WinIslands-{version}-osx-{arch}.zip')
        make_dmg(stage, output / f'WinIslands-{version}-osx-{arch}.dmg', version)
        for file in sorted(output.glob(f'WinIslands-{version}-osx-{arch}.*')):
            if file.is_file():
                results.append((file, sha256(file)))
                print(f'  {file.name}: {file.stat().st_size / 1048576:.1f} MB')

    checksum_file = output / 'SHA256SUMS.txt'
    checksum_file.write_text(
        ''.join(f'{digest}  {path.name}\n' for path, digest in results),
        encoding='utf-8', newline='\n')
    print(f'  checksums: {checksum_file}')


if __name__ == '__main__':
    main()
