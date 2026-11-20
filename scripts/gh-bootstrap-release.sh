#!/usr/bin/env bash
# Release helper for a DISPOSABLE Debian build machine.
# GitHub Releases hosts updates permanently; Debian only builds/signs/uploads.
# The signing private key MUST be backed up somewhere durable before using this script.
set -Eeuo pipefail
umask 077
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
REPO=''
VERSION='0.9.0'
MODE='prepare'
PRIVATE="${IMAGEOPTIM_PRIVATE_KEY_FILE:-}"
usage() {
  cat <<'USAGE'
Usage:
  bash scripts/gh-bootstrap-release.sh --private-key /secure/private.pem [--prepare]
  bash scripts/gh-bootstrap-release.sh --private-key /secure/private.pem --publish
  bash scripts/gh-bootstrap-release.sh --private-key /secure/private.pem --repo OWNER/REPO --publish
  bash scripts/gh-bootstrap-release.sh --private-key /secure/private.pem --version 0.9.1 --publish

Requires 'gh' authentication on the temporary Debian build server. When --repo
is omitted, selects the CURRENT gh login / ImageOptimWin.

--prepare (default): Configure, build and verify signed assets only. No remote changes.
--publish: After successful local build, create a PUBLIC repository if needed and
           upload the signed Windows ZIP and manifest as a stable GitHub Release.

IMPORTANT: This script NEVER generates your long-lived signing private key.
Generate and securely back it up on a durable, trusted machine first; provide a
temporary copy with --private-key. Never keep the only copy on a disposable Debian VM.
No private key is ever embedded in the app, source ZIP, or GitHub Release.
USAGE
}
die() { printf '[ERROR] %s\n' "$*" >&2; exit 1; }
log() { printf '[ImageOptimWin] %s\n' "$*"; }
while (( $# )); do
  case "$1" in
    --repo) (( $# >= 2 )) || die '--repo requires OWNER/REPO'; REPO="$2"; shift 2 ;;
    --version) (( $# >= 2 )) || die '--version requires x.y.z'; VERSION="$2"; shift 2 ;;
    --private-key) (( $# >= 2 )) || die '--private-key requires a private key path'; PRIVATE="$2"; shift 2 ;;
    --publish) MODE='publish'; shift ;;
    --prepare) MODE='prepare'; shift ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown argument: $1" ;;
  esac
done
[[ "$(uname -s)" == 'Linux' ]] || die 'Run on Debian/Ubuntu Linux.'
for tool in gh python3 openssl; do command -v "$tool" >/dev/null 2>&1 || die "Missing command: $tool"; done
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die 'Version must be x.y.z'
[[ -n "$PRIVATE" && -f "$PRIVATE" ]] || die 'Provide --private-key PATH. The permanent signing key MUST live outside this disposable Debian VM.'
PRIVATE="$(realpath -- "$PRIVATE")"
case "$PRIVATE" in "$ROOT/"*) die 'Signing PRIVATE key must not reside in the source tree.' ;; esac
[[ -r "$PRIVATE" ]] || die 'Signing private key is unreadable.'
[[ "$(stat -c '%a' "$PRIVATE")" == '600' ]] || log 'Warning: signing private key permissions are not 0600; consider chmod 600.'
gh auth status >/dev/null || die 'gh is not authorized on THIS Debian build host. Run gh auth login first.'
LOGIN="$(gh api user --jq '.login')" || die 'Cannot obtain authenticated gh login.'
[[ "$LOGIN" =~ ^[A-Za-z0-9-]+$ ]] || die 'Unexpected GitHub user name'
REPO="${REPO:-$LOGIN/ImageOptimWin}"
[[ "$REPO" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die 'Expected GitHub repository OWNER/REPO'
log "Temporary build machine using GitHub account: $LOGIN"
log "Permanent update host: https://github.com/$REPO/releases"
log "Mode: $MODE; version: $VERSION"

# Derive public trust anchor from an existing, externally backed-up private key.
# Never generate a new key on a disposable machine: this would break future updates.
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/imageoptim-public-XXXXXXXX")"
trap 'rm -rf -- "$TEMP"' EXIT
PUBLIC="$TEMP/public.pem"
openssl pkey -in "$PRIVATE" -pubout -out "$PUBLIC" >/dev/null 2>&1 || die 'Private key invalid (or passphrase not supplied).'

# Do not silently rotate the repository or trust key in an already configured tree.
python3 - "$ROOT" "$REPO" "$PUBLIC" <<'PY'
import json, sys
from pathlib import Path
root, repo, pubfile = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
p = root/'src/ImageOptim.Win/update-config.json'
old = json.loads(p.read_text(encoding='utf-8'))
old_repo, old_pub = old.get('repository',''), old.get('publicKeyPem','')
new_pub = pubfile.read_text(encoding='ascii')
if old_repo and old_repo != repo:
    raise SystemExit('[ERROR] This source already trusts another GitHub repo. Refusing to change the update source silently.')
if old_pub and ''.join(old_pub.split()) != ''.join(new_pub.split()):
    raise SystemExit('[ERROR] Existing public signing key differs. Restore the original key from durable storage.')
print('[PASS] Update repository and signing trust anchor consistent')
PY

# Make sure the release's own version matches its signed manifest version.
python3 - "$ROOT" "$VERSION" <<'PY'
import sys, xml.etree.ElementTree as ET
from pathlib import Path
root, version = Path(sys.argv[1]), sys.argv[2]
for rel in ('src/ImageOptim.Win/ImageOptim.Win.csproj', 'src/ImageOptim.Updater/ImageOptim.Updater.csproj'):
    doc = ET.parse(root/rel).getroot()
    actual = next((e.text for e in doc.iter() if e.tag == 'Version'), None)
    if actual != version:
        raise SystemExit(f'[ERROR] {rel} has Version {actual!r}; expected {version!r}. Update both csproj versions before release.')
print('[PASS] App and updater version match the requested release')
PY

python3 "$ROOT/scripts/configure-updates.py" --repo "$REPO" --public-key "$PUBLIC"
for check in check-source-config.py check-v08-regressions.py check-v081-build.py check-v09-updater.py; do
  python3 "$ROOT/scripts/$check"
done
log 'Cross-building on Debian and checking signed GitHub Release assets locally.'
bash "$ROOT/scripts/build-and-release.sh" "$REPO" "$VERSION" "$PRIVATE" --dry-run
if [[ "$MODE" == 'prepare' ]]; then
  log 'Prepared and verified locally. Nothing was created on GitHub.'
  log "Build output: $ROOT/dist/ImageOptimWin-win-x64-Bundled.zip"
  log 'Test the built Windows program and updater on Windows before using --publish.'
  exit 0
fi

# Only now is an explicit --publish allowed to make GitHub changes.
if gh repo view "$REPO" --json visibility --jq '.visibility' >/dev/null 2>&1; then
  visibility="$(gh repo view "$REPO" --json visibility --jq '.visibility')"
  [[ "${visibility,,}" == 'public' ]] || die "Existing repository $REPO is not public. Public Releases are required."
  log "Using existing public GitHub repository $REPO"
else
  log "Creating PUBLIC repository $REPO with initial README (no source code pushed)."
  gh repo create "$REPO" --public --add-readme \
    --description 'ImageOptimWin Windows image optimizer: signed update releases'
  visibility="$(gh repo view "$REPO" --json visibility --jq '.visibility')"
  [[ "${visibility,,}" == 'public' ]] || die 'Repository creation did not result in a public repository.'
fi
if gh release view "v$VERSION" --repo "$REPO" >/dev/null 2>&1; then
  die "Release v$VERSION already exists. Refusing to replace signed assets."
fi
bash "$ROOT/scripts/release-github.sh" "$REPO" "$VERSION" "$PRIVATE"
log "Uploaded to GitHub Releases. This Debian machine is no longer needed."
log "Release: https://github.com/$REPO/releases/tag/v$VERSION"
