#!/usr/bin/env bash
# Publish a signed stable GitHub Release. Run after build-linux.sh.
# bash scripts/release-github.sh OWNER/REPO 0.9.0 /safe/path/signing-private.pem [--dry-run]
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
(( $# >= 3 && $# <= 4 )) || { echo 'Usage: release-github.sh OWNER/REPO 0.9.0 /outside/project/private.pem [--dry-run]' >&2; exit 2; }
REPO="$1"; VERSION="$2"; PRIVATE="$3"; MODE="${4:-}"
[[ -z "$MODE" || "$MODE" == '--dry-run' ]] || { echo 'Unknown option' >&2; exit 2; }
python3 "$ROOT/scripts/make-update-release.py" --repo "$REPO" --version "$VERSION" \
  --private-key "$PRIVATE" --archive "$ROOT/dist/ImageOptimWin-win-x64-Bundled.zip" \
  --out "$ROOT/dist/release-assets"
if [[ "$MODE" == '--dry-run' ]]; then
  echo '[DRY RUN] Manifest, signature and package validated. No GitHub actions performed.'
  exit 0
fi
command -v gh >/dev/null 2>&1 || { echo 'Install gh CLI on your Debian build server.' >&2; exit 1; }
gh auth status >/dev/null
TAG="v$VERSION"
if gh release view "$TAG" --repo "$REPO" >/dev/null 2>&1; then
  echo "Release $TAG already exists. Refusing to overwrite signed assets." >&2
  exit 1
fi
gh release create "$TAG" \
  "$ROOT/dist/ImageOptimWin-win-x64-Bundled.zip" \
  "$ROOT/dist/release-assets/update-manifest.json" \
  "$ROOT/dist/release-assets/update-manifest.sig" \
  --repo "$REPO" --verify-tag --title "ImageOptimWin $TAG" \
  --notes "ImageOptimWin $TAG Windows x64 offline release (signed update manifest)." --latest
printf 'Published: https://github.com/%s/releases/tag/%s\n' "$REPO" "$TAG"
