#!/usr/bin/env python3
"""Fail-closed verification of a signed, offline-ready ImageOptimWin Release."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory
from xml.etree import ElementTree as ET
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parent.parent
ARCHIVE = 'ImageOptimWin-win-x64-Bundled.zip'
REQUIRED = {
    'ImageOptim.Win.exe', 'ImageOptim.Updater.exe', 'update-config.json',
    'tools/oxipng.exe', 'tools/jpegoptim.exe', 'tools/jpegtran.exe',
    'tools/gifsicle.exe', 'tools/pngquant.exe', 'tools/node.exe',
    'tools/svgo/node_modules/svgo/bin/svgo',
}

def die(message):
    raise SystemExit('[INVALID RELEASE] ' + message)

def app_versions():
    values = []
    for name in ('src/ImageOptim.Win/ImageOptim.Win.csproj',
                 'src/ImageOptim.Updater/ImageOptim.Updater.csproj'):
        doc = ET.parse(ROOT / name).getroot()
        values.append(next((e.text for e in doc.iter() if e.tag == 'Version'), None))
    return values

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--signature', type=Path, required=True)
    args = parser.parse_args()

    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repo):
        die('Invalid repository')
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', args.version):
        die('Invalid semantic version')
    if any(v != args.version for v in app_versions()):
        die('Application/updater project versions do not match release')
    if args.archive.name != ARCHIVE or not all(p.is_file() for p in
         (args.archive, args.manifest, args.signature)):
        die('Missing or wrongly named release assets')

    cfg = json.loads((ROOT / 'src/ImageOptim.Win/update-config.json').read_text(encoding='utf-8'))
    if cfg.get('repository') != args.repo or not cfg.get('publicKeyPem', '').startswith('-----BEGIN PUBLIC KEY-----'):
        die('Update trust anchor is missing or for another repository')

    with TemporaryDirectory(prefix='imageoptim-release-verify-') as tmp:
        pub = Path(tmp) / 'public.pem'
        pub.write_text(cfg['publicKeyPem'], encoding='ascii')
        verification = subprocess.run(
            ['openssl', 'dgst', '-sha256', '-verify', str(pub),
             '-signature', str(args.signature), str(args.manifest)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        if verification.returncode != 0:
            die('RSA-SHA256 manifest signature invalid')

    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    if manifest.get('repository') != args.repo or manifest.get('version') != args.version or \
            manifest.get('tag') != 'v' + args.version or manifest.get('archive') != ARCHIVE:
        die('Signed manifest release metadata mismatch')
    size = args.archive.stat().st_size
    if manifest.get('size') != size or size <= 0:
        die('Archive size does not match signed manifest')
    digest = hashlib.sha256()
    with args.archive.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    if digest.hexdigest() != manifest.get('sha256'):
        die('Archive hash does not match signed manifest')

    with ZipFile(args.archive) as zipped:
        missing = REQUIRED.difference(zipped.namelist())
        if missing:
            die('Bundle missing: ' + ', '.join(sorted(missing)))
        if zipped.testzip() is not None:
            die('Archive CRC validation failed')
        if json.loads(zipped.read('update-config.json')) != cfg:
            die('Built application trusts a different update repository/key')
    print('[PASS] RSA-SHA256, version, archive hash/size, bundle and embedded trust anchor')

if __name__ == '__main__':
    main()
