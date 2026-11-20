#!/usr/bin/env bash
# One-command Debian build + signed GitHub Releases publication.
# Usage: bash scripts/build-and-release.sh OWNER/REPO 0.9.0 /outside/path/private.pem [--dry-run]
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ $# -ge 3 && $# -le 4 ]] || { echo 'Usage: build-and-release.sh OWNER/REPO VERSION PRIVATE_KEY [--dry-run]' >&2; exit 2; }
# The release publisher expects this stable path; ensure a different destination
# from IMAGEOPTIM_DIST does not accidentally publish a stale ZIP.
export IMAGEOPTIM_DIST="$ROOT/dist"
bash "$ROOT/build-linux.sh" "$ROOT"
bash "$ROOT/scripts/release-github.sh" "$@"
