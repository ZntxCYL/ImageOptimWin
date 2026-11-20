#!/usr/bin/env bash
# Debian/Ubuntu x86_64 -> Windows x64 WPF release with all bundled engines.
# Usage: bash build-linux.sh [ImageOptimWin_WPF_Source_v0.8.1.zip | extracted-project-directory]
set -Eeuo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export NUGET_XMLDOC_MODE=skip

HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
TMP_WORK=''
CURRENT_STEP='startup'
cleanup() { if [[ -n "$TMP_WORK" && -d "$TMP_WORK" ]]; then rm -rf -- "$TMP_WORK"; fi; }
trap cleanup EXIT
trap 'echo "[ERROR] Step: ${CURRENT_STEP}; line ${LINENO}; exit status $?" >&2' ERR
say() { printf '\n\033[1;36m[ImageOptimWin] %s\033[0m\n' "$*"; }
die() { echo "[ERROR] $*" >&2; exit 1; }
usage() {
  cat <<'EOF'
Debian one-command Windows build (x86_64 only):
  bash build-linux.sh /path/to/ImageOptimWin_WPF_Source_v0.8.1.zip
  bash build-linux.sh /path/to/extracted/ImageOptimWin
  bash build-linux.sh   # inside the extracted project, or beside the source ZIP

Output: dist/ImageOptimWin-win-x64-Bundled.zip and dist/win-x64/ImageOptim.Win.exe
The ZIP includes the supported Windows image compressors and .NET runtime.
Only build-time network access is required. Windows is still required to test the GUI.
Environment: IMAGEOPTIM_CACHE (downloads cache), IMAGEOPTIM_DIST (output directory).
EOF
}
[[ "${1:-}" != '-h' && "${1:-}" != '--help' ]] || { usage; exit 0; }
[[ "$(uname -s)" == Linux ]] || die 'This build script requires Linux.'
[[ "$(uname -m)" == x86_64 ]] || die 'The currently pinned toolchains require x86_64 Debian/Ubuntu.'

CURRENT_STEP='dependencies'
need_commands=(curl unzip tar xz sha256sum find od stat python3)
missing=()
for command in "${need_commands[@]}"; do command -v "$command" >/dev/null 2>&1 || missing+=("$command"); done
if ((${#missing[@]})); then
  if command -v apt-get >/dev/null 2>&1; then
    if (( EUID == 0 )); then APT=(apt-get); elif command -v sudo >/dev/null 2>&1; then APT=(sudo apt-get); else die "Missing commands: ${missing[*]}. Install: curl unzip tar xz-utils coreutils findutils python3 ca-certificates"; fi
    say 'Installing Debian build prerequisites (one time)'
    "${APT[@]}" update
    DEBIAN_FRONTEND=noninteractive "${APT[@]}" install -y ca-certificates curl unzip tar xz-utils coreutils findutils python3
  else
    die "Missing tools: ${missing[*]}. Install them first."
  fi
fi

TMP_WORK="$(mktemp -d "${TMPDIR:-/tmp}/imageoptim-win.XXXXXXXX")"
INPUT="${1:-}"
if [[ -z "$INPUT" ]]; then
  if [[ -f "$HERE/ImageOptim.Win.sln" ]]; then
    INPUT="$HERE"
  else
    shopt -s nullglob
    candidates=("$HERE"/ImageOptimWin*.zip)
    shopt -u nullglob
    (( ${#candidates[@]} > 0 )) || die 'No project or source ZIP found. Pass its path as the first argument.'
    # Choose the most recent matching source archive when called beside ZIPs.
    INPUT="${candidates[${#candidates[@]}-1]}"
  fi
fi
[[ -e "$INPUT" ]] || die "Missing source: $INPUT"
CURRENT_STEP='input project'
if [[ -d "$INPUT" ]]; then
  PROJECT="$(cd -- "$INPUT" && pwd -P)"
  OUTPUT_DEFAULT="$PROJECT/dist"
else
  INPUT="$(realpath -- "$INPUT")"
  [[ "$INPUT" == *.zip ]] || die 'Expected an extracted project folder or a .zip source package.'
  say 'Extracting WPF source archive'
  unzip -q "$INPUT" -d "$TMP_WORK/source"
  if [[ -f "$TMP_WORK/source/ImageOptimWin/ImageOptim.Win.sln" ]]; then
    PROJECT="$TMP_WORK/source/ImageOptimWin"
  elif [[ -f "$TMP_WORK/source/ImageOptim.Win.sln" ]]; then
    PROJECT="$TMP_WORK/source"
  else
    die 'ZIP does not contain ImageOptim.Win.sln in the expected location.'
  fi
  OUTPUT_DEFAULT="$(dirname -- "$INPUT")/dist"
fi
[[ -f "$PROJECT/src/ImageOptim.Win/ImageOptim.Win.csproj" ]] || die 'WPF project file is absent.'
CURRENT_STEP='source configuration validation'
python3 "$PROJECT/scripts/check-source-config.py" || die 'Source configuration check failed; review csproj and ThemeManager.cs.'
python3 "$PROJECT/scripts/check-v08-regressions.py" || die 'v0.8 UI/icon regression check failed. Do not package this source.'
python3 "$PROJECT/scripts/check-v081-build.py" || die 'v0.8.1 Bash/SVGO build regression check failed. Do not package this source.'
python3 "$PROJECT/scripts/check-v09-updater.py" || die 'v0.9 updater regression check failed. Do not package this source.' 
OUTPUT_DIR="${IMAGEOPTIM_DIST:-$OUTPUT_DEFAULT}"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd -- "$OUTPUT_DIR" && pwd -P)"
CACHE="${IMAGEOPTIM_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/imageoptimwin}"
mkdir -p "$CACHE"

# Locked download: reuse only when SHA-256 still matches. Do not bypass mismatches.
fetch() {
  local filename="$1" url="$2" expected="$3"
  local dest="$CACHE/$filename" got
  if [[ -f "$dest" ]]; then
    got="$(sha256sum "$dest" | cut -d ' ' -f1)"
    [[ "${got,,}" == "${expected,,}" ]] || rm -f -- "$dest"
  fi
  if [[ ! -f "$dest" ]]; then
    echo "Downloading $filename" >&2
    curl --fail --location --retry 3 --retry-all-errors --connect-timeout 25 --max-time 900 \
      --silent --show-error "$url" --output "$dest.partial"
    got="$(sha256sum "$dest.partial" | cut -d ' ' -f1)"
    if [[ "${got,,}" != "${expected,,}" ]]; then
      rm -f -- "$dest.partial"
      die "SHA256 mismatch: $filename; actual=$got; expected=$expected. Upstream may have changed."
    fi
    mv -f -- "$dest.partial" "$dest"
  fi
  printf '%s\n' "$dest"
}
fetch_unpinned_source() { # For source archives only; never for executables.
  local filename="$1" url="$2"
  local dest="$CACHE/$filename"
  if [[ ! -s "$dest" ]]; then
    echo "Downloading source: $filename" >&2
    curl --fail --location --retry 3 --connect-timeout 25 --max-time 900 --silent --show-error \
      "$url" --output "$dest.partial"
    mv -- "$dest.partial" "$dest"
  fi
  printf '%s\n' "$dest"
}
extract_zip() {
  local name="$1" url="$2" hash="$3" archive="$4"
  local pkg; pkg="$(fetch "$name" "$url" "$hash")"
  mkdir -p "$archive"
  unzip -q "$pkg" -d "$archive"
}
verify_pe() {
  local f="$1" sig
  [[ -s "$f" ]] || die "Missing Windows EXE: $f"
  (( $(stat -c %s "$f") >= 1024 )) || die "Suspiciously small Windows EXE: $f"
  sig="$(od -An -tx1 -N2 "$f" | tr -d ' \n')"
  [[ "$sig" == '4d5a' ]] || die "Not a Windows PE file: $f"
}
TOOLS_STAGE="$TMP_WORK/tools"
mkdir -p "$TOOLS_STAGE/licenses/corresponding-source" "$TOOLS_STAGE/licenses/from-vendor-archives"
copy_engine() {
  local root="$1" upstream="$2" destname="$3" origin
  origin="$(find "$root" -type f -name "$upstream" -print -quit)"
  [[ -n "$origin" ]] || die "The upstream archive does not contain $upstream"
  cp -f -- "$origin" "$TOOLS_STAGE/$destname"
  verify_pe "$TOOLS_STAGE/$destname"
  # Preserve DLLs distributed alongside EXEs.
  while IFS= read -r -d '' dll; do cp -f -- "$dll" "$TOOLS_STAGE/"; done \
    < <(find "$(dirname -- "$origin")" -maxdepth 1 -type f -iname '*.dll' -print0)
  echo "  + $destname"
}
copy_vendor_notice() {
  local id="$1" base="$2" license
  license="$(find "$base" -type f \( -iname 'LICENSE' -o -iname 'LICENSE.*' -o -iname 'COPYING' -o -iname 'COPYING.*' -o -iname 'COPYRIGHT' \) -print -quit)"
  if [[ -n "$license" ]]; then cp -f -- "$license" "$TOOLS_STAGE/licenses/from-vendor-archives/${id}-$(basename -- "$license")"; fi
}

CURRENT_STEP='Windows engine downloads'
say 'Fetching and bundling Windows x64 engines (SHA-256 verified)'
extract_zip 'oxipng-10.2.1-win64.zip' \
  'https://github.com/oxipng/oxipng/releases/download/v10.2.1/oxipng-10.2.1-x86_64-pc-windows-msvc.zip' \
  '7e940f83ee46874b73f53031f96a15834cb70b220af27391fb06fe7b4dd798e1' "$TMP_WORK/ox"
copy_engine "$TMP_WORK/ox" oxipng.exe oxipng.exe
copy_vendor_notice oxipng "$TMP_WORK/ox"
extract_zip 'jpegoptim-1.5.6-win64.zip' \
  'https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6-x64-windows.zip' \
  'db2d8caee88f2665b772c4591cba223b8b729500b88266dc06d4bf79d28fe11a' "$TMP_WORK/jo"
copy_engine "$TMP_WORK/jo" jpegoptim.exe jpegoptim.exe
copy_vendor_notice jpegoptim "$TMP_WORK/jo"
extract_zip 'mozjpeg-4.1.5-win64.zip' \
  'https://github.com/garyzyg/mozjpeg-windows/releases/download/4.1.5/mozjpeg-x64.zip' \
  '176ef4f632b09dca449e27bc11df090c1222528dff9f583a14bbd026a0c8eb2f' "$TMP_WORK/moz"
copy_engine "$TMP_WORK/moz" jpegtran-static.exe jpegtran.exe
copy_engine "$TMP_WORK/moz" cjpeg-static.exe cjpeg.exe
copy_engine "$TMP_WORK/moz" djpeg-static.exe djpeg.exe
copy_vendor_notice mozjpeg "$TMP_WORK/moz"
extract_zip 'gifsicle-1.97-win64.zip' \
  'https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.97-win64.zip' \
  'bb3ad4e1521a3953152f406eaafabda153fb5986938405860a7e7f12a3d13227' "$TMP_WORK/gif"
copy_engine "$TMP_WORK/gif" gifsicle.exe gifsicle.exe
copy_vendor_notice gifsicle "$TMP_WORK/gif"
extract_zip 'pngquant-2.18.0-win.zip' \
  'https://pngquant.org/pngquant-windows.zip' \
  'bd0257aeeccfe446a4cd764927e26f8af6051796f28abed104307284107b120d' "$TMP_WORK/pq"
copy_engine "$TMP_WORK/pq" pngquant.exe pngquant.exe
copy_vendor_notice pngquant "$TMP_WORK/pq"

CURRENT_STEP='portable Node and SVG optimizer'
say 'Bundling portable Windows Node.js and installing SVGO via Linux Node.js'
extract_zip 'node-v22.16.0-win-x64.zip' \
  'https://nodejs.org/dist/v22.16.0/node-v22.16.0-win-x64.zip' \
  '21c2d9735c80b8f86dab19305aa6a9f6f59bbc808f68de3eef09d5832e3bfbbd' "$TMP_WORK/node-win"
copy_engine "$TMP_WORK/node-win" node.exe node.exe
LINUX_NODE_ARCHIVE="$(fetch 'node-v22.16.0-linux-x64.tar.xz' \
  'https://nodejs.org/dist/v22.16.0/node-v22.16.0-linux-x64.tar.xz' \
  'f4cb75bb036f0d0eddf6b79d9596df1aaab9ddccd6a20bf489be5abe9467e84e')"
mkdir -p "$TMP_WORK/node-linux"
tar -xf "$LINUX_NODE_ARCHIVE" -C "$TMP_WORK/node-linux"
NODE_ROOT="$TMP_WORK/node-linux/node-v22.16.0-linux-x64"
"$NODE_ROOT/bin/node" --version
mkdir -p "$TOOLS_STAGE/svgo"
"$NODE_ROOT/bin/node" "$NODE_ROOT/lib/node_modules/npm/bin/npm-cli.js" install \
  --prefix "$TOOLS_STAGE/svgo" 'svgo@3.3.2' --omit=dev --ignore-scripts --no-audit --no-fund --save-exact
# SVGO 3.3.2's package.json declares the CLI as "./bin/svgo" (no .js suffix).
SVGO_PACKAGE="$TOOLS_STAGE/svgo/node_modules/svgo/package.json"
[[ -s "$SVGO_PACKAGE" ]] || die 'SVGO package.json is missing after npm install.'
SVGO_CLI_REL="$(python3 - "$SVGO_PACKAGE" <<'PYJSON'
import json, sys
p = json.load(open(sys.argv[1], encoding='utf-8'))
assert p.get('version') == '3.3.2', 'Unexpected SVGO version'
b = p.get('bin')
if isinstance(b, dict): b = b.get('svgo')
assert b in ('./bin/svgo', 'bin/svgo'), f'Unexpected SVGO CLI path: {b!r}'
print(b.removeprefix('./'))
PYJSON
)" || die 'SVGO manifest validation failed.'
[[ -s "$TOOLS_STAGE/svgo/node_modules/svgo/$SVGO_CLI_REL" ]] || die "SVGO CLI is missing at $SVGO_CLI_REL."

CURRENT_STEP='license source archives'
say 'Collecting third-party license notices and corresponding source archives'
cp -f -- "$PROJECT/THIRD_PARTY.md" "$TOOLS_STAGE/THIRD_PARTY.md"
for spec in \
  'jpegoptim-1.5.6.tar.gz|https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6.tar.gz|661a808dfffa933d78c6beb47a2937d572b9f03e94cbaaab3d4c0d72f410e9be' \
  'pngquant-2.18.0-src.tar.gz|https://pngquant.org/pngquant-2.18.0-src.tar.gz|e72194b52b36f040deaec49a1ddd5dcd8d4feecc3a5fe6c5e9589a9707b233d4'; do
  IFS='|' read -r fname url sha <<< "$spec"
  cp -- "$(fetch "$fname" "$url" "$sha")" "$TOOLS_STAGE/licenses/corresponding-source/$fname"
done
cp -- "$(fetch_unpinned_source 'gifsicle-1.97.tar.gz' \
  'https://www.lcdf.org/gifsicle/gifsicle-1.97.tar.gz')" \
  "$TOOLS_STAGE/licenses/corresponding-source/gifsicle-1.97.tar.gz"
[[ -f "$PROJECT/src/ImageOptim.Win/tools/README.txt" ]] && \
  cp "$PROJECT/src/ImageOptim.Win/tools/README.txt" "$TOOLS_STAGE/README.txt"

CURRENT_STEP='SDK bootstrapping'
say 'Preparing .NET 10 SDK'
DOTNET_CMD=''
if command -v dotnet >/dev/null 2>&1 && [[ "$(dotnet --version 2>/dev/null || :)" == 10.* ]]; then
  DOTNET_CMD="$(command -v dotnet)"
else
  LOCAL_DOTNET="${DOTNET_ROOT:-${HOME}/.dotnet}"
  if [[ -x "$LOCAL_DOTNET/dotnet" && "$("$LOCAL_DOTNET/dotnet" --version 2>/dev/null || :)" == 10.* ]]; then
    DOTNET_CMD="$LOCAL_DOTNET/dotnet"
  else
    say 'Downloading official dotnet-install.sh (build host only)'
    curl --fail --location --retry 3 --silent --show-error \
      'https://dot.net/v1/dotnet-install.sh' --output "$TMP_WORK/dotnet-install.sh"
    bash "$TMP_WORK/dotnet-install.sh" --channel 10.0 --install-dir "$LOCAL_DOTNET" --no-path
    DOTNET_CMD="$LOCAL_DOTNET/dotnet"
  fi
fi
[[ -x "$DOTNET_CMD" ]] || die 'Failed to prepare .NET 10 SDK.'
"$DOTNET_CMD" --version

CURRENT_STEP='publish WPF exe'
say 'Cross-publishing Windows x64 WPF executable on Linux'
PROJECT_FILE="$PROJECT/src/ImageOptim.Win/ImageOptim.Win.csproj"
PROJECT_TOOLS="$PROJECT/src/ImageOptim.Win/tools"
mkdir -p "$PROJECT_TOOLS"
# Keep the source tree in sync before SDK evaluates Content Include tools/**/*.
cp -a "$TOOLS_STAGE/." "$PROJECT_TOOLS/"
OUT_DIR="$OUTPUT_DIR/win-x64"
mkdir -p "$OUT_DIR"
"$DOTNET_CMD" publish "$PROJECT_FILE" -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$OUT_DIR"

CURRENT_STEP='publish detached updater'
say 'Cross-publishing independent Windows updater executable'
UPDATER_PROJECT="$PROJECT/src/ImageOptim.Updater/ImageOptim.Updater.csproj"
UPDATER_OUT="$TMP_WORK/published-updater"
"$DOTNET_CMD" publish "$UPDATER_PROJECT" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false \
  -p:DebugType=None -p:DebugSymbols=false -o "$UPDATER_OUT"
verify_pe "$UPDATER_OUT/ImageOptim.Updater.exe"
cp -f -- "$UPDATER_OUT/ImageOptim.Updater.exe" "$OUT_DIR/ImageOptim.Updater.exe"

CURRENT_STEP='publish validation'
say 'Validating output and creating offline ZIP'
APP_EXE="$OUT_DIR/ImageOptim.Win.exe"
verify_pe "$APP_EXE"
verify_pe "$OUT_DIR/ImageOptim.Updater.exe"
[[ -s "$OUT_DIR/update-config.json" ]] || die 'Missing update-config.json in release package.' 
required=(oxipng.exe jpegoptim.exe jpegtran.exe cjpeg.exe djpeg.exe gifsicle.exe pngquant.exe node.exe 'svgo/node_modules/svgo/bin/svgo')
for entry in "${required[@]}"; do
  [[ -s "$OUT_DIR/tools/$entry" ]] || die "Published package misses: tools/$entry"
done
cp -f -- "$PROJECT/快速使用-中文.txt" "$OUT_DIR/快速使用-中文.txt"
cp -f -- "$PROJECT/THIRD_PARTY.md" "$OUT_DIR/THIRD_PARTY.md"
ZIP_OUT="$OUTPUT_DIR/ImageOptimWin-win-x64-Bundled.zip"
python3 - "$OUT_DIR" "$ZIP_OUT" <<'PY'
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import sys
root, dst = Path(sys.argv[1]), Path(sys.argv[2])
with ZipFile(dst, 'w', compression=ZIP_DEFLATED, compresslevel=6, allowZip64=True) as z:
    for p in sorted(root.rglob('*')):
        if p.is_file():
            z.write(p, p.relative_to(root).as_posix())
with ZipFile(dst) as z:
    bad = z.testzip()
    if bad:
        raise RuntimeError(f'Corrupted ZIP member: {bad}')
    assert 'ImageOptim.Win.exe' in z.namelist()
    assert 'ImageOptim.Updater.exe' in z.namelist()
    assert 'update-config.json' in z.namelist()
    assert 'tools/oxipng.exe' in z.namelist()
print(f'Created: {dst} ({dst.stat().st_size / 1024 / 1024:.1f} MiB)')
PY
say 'Success: Windows offline package created'
printf 'Windows executable: %s\nOffline ZIP:        %s\n' "$APP_EXE" "$ZIP_OUT"
printf 'Note: GUI and real image optimization must still be tested on Windows 10/11 x64.\n'
