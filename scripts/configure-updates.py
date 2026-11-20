#!/usr/bin/env python3
"""Embed the public GitHub repository and trusted RSA public key into the app.

Never copy your private key into this project or a release ZIP.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--repo', required=True, help='GitHub OWNER/REPO, public repository')
parser.add_argument('--public-key', required=True, type=Path, help='PEM RSA public key')
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repo):
    parser.error('--repo must be OWNER/REPO')
key = args.public_key.read_text(encoding='ascii')
if not key.startswith('-----BEGIN PUBLIC KEY-----'):
    parser.error('Expected a PEM PUBLIC KEY, not a private key')
try:
    subprocess.run(['openssl', 'pkey', '-pubin', '-in', str(args.public_key), '-noout'],
                   check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
except (OSError, subprocess.CalledProcessError):
    parser.error('Could not validate PEM public key with OpenSSL')
path = ROOT / 'src/ImageOptim.Win/update-config.json'
path.write_text(json.dumps({'repository': args.repo, 'publicKeyPem': key},
                           ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'Updated {path} for {args.repo}. Private key remains outside project.')
