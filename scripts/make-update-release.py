#!/usr/bin/env python3
"""Generate the exact, signed GitHub Release assets from an offline Windows ZIP.

Calls OpenSSL with a private-key file held OUTSIDE the source tree.
Outputs update-manifest.json + update-manifest.sig alongside the ZIP.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--repo', required=True)
parser.add_argument('--version', required=True)
parser.add_argument('--private-key', type=Path, required=True)
parser.add_argument('--archive', type=Path, default=ROOT / 'dist/ImageOptimWin-win-x64-Bundled.zip')
parser.add_argument('--out', type=Path, default=ROOT / 'dist/release-assets')
a = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', a.repo):
    parser.error('Repository must be OWNER/REPO')
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', a.version):
    parser.error('Version must be MAJOR.MINOR.PATCH')
if a.archive.name != 'ImageOptimWin-win-x64-Bundled.zip' or not a.archive.is_file():
    parser.error('Expected the bundled Windows ZIP from build-linux.sh')
if not a.private_key.is_file():
    parser.error('Private signing key not found')

# Prevent publishing a release with a different public trust anchor.
config_path = ROOT / 'src/ImageOptim.Win/update-config.json'
config = json.loads(config_path.read_text(encoding='utf-8'))
if config.get('repository') != a.repo or 'BEGIN PUBLIC KEY' not in config.get('publicKeyPem', ''):
    parser.error('Configure the matching repository and public key before building/releasing')
pub_from_private = subprocess.run(['openssl', 'pkey', '-in', str(a.private_key), '-pubout'],
                                  capture_output=True, check=True).stdout.decode('ascii')
if ''.join(pub_from_private.split()) != ''.join(config['publicKeyPem'].split()):
    parser.error('Private key does not match the configured update public key')

with ZipFile(a.archive) as z:
    names = set(z.namelist())
    expected = {'ImageOptim.Win.exe', 'ImageOptim.Updater.exe', 'tools/oxipng.exe',
                'tools/svgo/node_modules/svgo/bin/svgo', 'update-config.json'}
    missing = expected - names
    if missing: parser.error('ZIP missing: ' + ', '.join(sorted(missing)))
    packaged_config = json.loads(z.read('update-config.json'))
    if packaged_config != config:
        parser.error('Build ZIP contains different update-config.json. Rebuild after configure-updates.py')
    if z.testzip() is not None:
        parser.error('ZIP CRC failure')

size = a.archive.stat().st_size
h = hashlib.sha256()
with a.archive.open('rb') as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        h.update(chunk)
manifest = {'repository': a.repo, 'version': a.version, 'tag': 'v'+a.version,
            'archive': a.archive.name, 'sha256': h.hexdigest(), 'size': size}
a.out.mkdir(parents=True, exist_ok=True)
manifest_file = a.out / 'update-manifest.json'
sig_file = a.out / 'update-manifest.sig'
manifest_file.write_text(json.dumps(manifest, ensure_ascii=False, separators=(',', ':'),
                                    sort_keys=True)+'\n', encoding='utf-8')
subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(a.private_key),
                '-out', str(sig_file), str(manifest_file)], check=True)
public_file = a.out / 'release-public.pem'
public_file.write_text(config['publicKeyPem'], encoding='ascii')
subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(public_file),
                '-signature', str(sig_file), str(manifest_file)], check=True)
public_file.unlink()
print('Release assets prepared:', a.archive, manifest_file, sig_file)
