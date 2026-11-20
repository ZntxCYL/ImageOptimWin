#!/usr/bin/env python3
"""v0.8.1 build regression checks: Bash 5.2 and SVGO 3.3.2 CLI path.

This check is offline and safe to run on Debian before any engine downloads.
"""
from pathlib import Path
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
LINUX = ROOT / "build-linux.sh"
CSPROJ = ROOT / "src/ImageOptim.Win/ImageOptim.Win.csproj"
CHECKS = {
    "Linux bundling": (LINUX, "svgo/node_modules/svgo/bin/svgo"),
    "Windows bundling": (ROOT / "scripts/bundle-engines.ps1", r"svgo\bin\svgo"),
    "Windows publish": (ROOT / "publish.ps1", r"svgo\bin\svgo"),
    "Windows smoke test": (ROOT / "scripts/smoke-test.ps1", r"svgo\bin\svgo"),
    "C# optimizer": (ROOT / "src/ImageOptim.Win/Services/OptimizerService.cs", '"bin", "svgo"'),
}
errors = []

for name, (path, needed) in CHECKS.items():
    try:
        content = path.read_text(encoding="utf-8")
    except OSError as exc:
        errors.append(f"{name}: cannot read {path}: {exc}")
        continue
    if needed not in content:
        errors.append(f"{name}: missing expected SVGO 3.3.2 entrypoint: {needed!r}")
    if re.search(r"\bsvgo\.js\b", content):
        errors.append(f"{name}: old svgo.js entrypoint found")

try:
    content = CSPROJ.read_text(encoding="utf-8")
    version = re.search(r"<Version>([0-9]+)\.([0-9]+)\.([0-9]+)</Version>", content)
    if not version or tuple(map(int, version.groups())) < (0, 8, 1):
        errors.append("csproj: expected a version >= 0.8.1")
except OSError as exc:
    errors.append(f"csproj read failed: {exc}")

try:
    shell = LINUX.read_text(encoding="utf-8")
except OSError as exc:
    errors.append(f"Linux script unreadable: {exc}")
    shell = ""

# In Bash, RHS expansions on the same 'local' statement see the prior scope.
# Under set -u, dest="$CACHE/$filename" fails if filename is newly declared.
if re.search(r"^\s*local\s+filename=.*\s+dest=", shell, flags=re.M):
    errors.append("Bash 5.2: local filename and dependent dest must be separate statements")

if "SVGO_PACKAGE=" not in shell or "Unexpected SVGO CLI path" not in shell:
    errors.append("Linux script: missing package.json bin validation")


def shell_function(name):
    """Extract top-level one-function block without evaluating the build script."""
    m = re.search(rf"^{re.escape(name)}\(\) \{{[^\n]*\n.*?^\}}", shell, flags=re.M | re.S)
    if not m:
        raise ValueError(f"Missing function: {name}")
    return m.group(0)


bash = shutil.which("bash")
if bash and not errors:
    with tempfile.TemporaryDirectory(prefix="imageoptim-v081-test-") as folder:
        cache = Path(folder)
        (cache / "cache-test.bin").write_bytes(b"verified binary data\n")
        (cache / "source-test.tar.gz").write_bytes(b"verified source data\n")
        expected_hash = hashlib.sha256((cache / "cache-test.bin").read_bytes()).hexdigest()
        program = "\n".join([
            "set -Eeuo pipefail",
            "die() { echo \"$*\" >&2; exit 1; }",
            shell_function("fetch"),
            shell_function("fetch_unpinned_source"),
            'result="$(fetch cache-test.bin https://invalid.example/download "$EXPECTED_HASH")"',
            '[[ "$result" == "$CACHE/cache-test.bin" ]]',
            'result="$(fetch_unpinned_source source-test.tar.gz https://invalid.example/source)"',
            '[[ "$result" == "$CACHE/source-test.tar.gz" ]]',
            'echo "Bash cache functions passed"',
        ])
        environment = {**os.environ, "CACHE": str(cache), "EXPECTED_HASH": expected_hash}
        run = subprocess.run([bash, "-c", program], capture_output=True, text=True, env=environment)
        if run.returncode != 0:
            errors.append(f"Bash -u regression test failed (exit {run.returncode}): {run.stderr.strip()}")
        else:
            print(f"[OK] Bash {subprocess.run([bash, '-c', 'echo ${BASH_VERSION}'], capture_output=True, text=True).stdout.strip()} set -u cached fetch functions")

        # Test the actual post-npm SVGO metadata/path verification block offline.
        try:
            m = re.search(
                r'^SVGO_PACKAGE=.*?^\[\[ -s "\$TOOLS_STAGE/svgo/node_modules/svgo/\$SVGO_CLI_REL" \]\][^\n]*$',
                shell, flags=re.M | re.S,
            )
            if not m:
                raise ValueError("Cannot find SVGO validation block")
            fixture_root = cache / "engine-stage"
            package_dir = fixture_root / "svgo/node_modules/svgo"
            (package_dir / "bin").mkdir(parents=True)
            (package_dir / "bin/svgo").write_text("#!/usr/bin/env node\n", encoding="utf-8")
            manifest = package_dir / "package.json"
            manifest.write_text(json.dumps({"version": "3.3.2", "bin": "./bin/svgo"}))
            svgo_program = "\n".join([
                "set -Eeuo pipefail",
                "die() { echo \"$*\" >&2; exit 1; }",
                m.group(0),
            ])
            svgo_env = {**os.environ, "TOOLS_STAGE": str(fixture_root)}
            ok = subprocess.run([bash, "-c", svgo_program], capture_output=True, text=True, env=svgo_env)
            if ok.returncode != 0:
                errors.append(f"SVGO validation rejected the correct v3.3.2 manifest: {ok.stderr.strip()}")
            manifest.write_text(json.dumps({"version": "3.3.2", "bin": "./bin/svgo.js"}))
            bad = subprocess.run([bash, "-c", svgo_program], capture_output=True, text=True, env=svgo_env)
            if bad.returncode == 0:
                errors.append("SVGO manifest guard did not reject the old incorrect svgo.js entrypoint")
            else:
                print("[OK] SVGO 3.3.2 manifest guard accepts bin/svgo and rejects bin/svgo.js")
        except (OSError, ValueError) as exc:
            errors.append(f"SVGO path fixture error: {exc}")
elif not bash:
    print("[WARN] bash not present: performed static checks only")

if errors:
    for problem in errors:
        print(f"[BUILD REGRESSION] {problem}", file=sys.stderr)
    sys.exit(1)
print("[OK] v0.8.1 SVGO 3.3.2 entrypoint and Bash compatibility checks passed")
