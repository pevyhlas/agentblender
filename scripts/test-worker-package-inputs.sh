#!/usr/bin/env bash
# Regression proof: both distribution paths exclude unknown/ignored worker files.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT
git -C "$ROOT" archive HEAD | tar -xf - -C "$WORK"
cd "$WORK"
mkdir -p worker/__pycache__ worker/diagnostics
printf 'SYNTHETIC_PACKAGE_CANARY\n' > worker/local-only.py
printf 'SYNTHETIC_PACKAGE_CANARY\n' > worker/__pycache__/cache.pyc
printf 'SYNTHETIC_PACKAGE_CANARY\n' > worker/diagnostics/private.log
dotnet restore agentblender.slnx --locked-mode --nologo
dotnet pack src/Abl.Cli --configuration Release --no-restore --output "$WORK/packages" --nologo
dotnet publish src/Abl.Launcher --configuration Release --no-restore --output "$WORK/launcher" --nologo
python3 - "$WORK" <<'PY'
import pathlib
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
expected = {"worker.py", "blobs.py", "cycles_devices.py", "fingerprint.py", "inspection.py", "sheet.py"}
with zipfile.ZipFile(next((root / "packages").glob("*.nupkg"))) as package:
    worker = [name for name in package.namelist() if "/worker/" in name]
    assert {name.split("/worker/", 1)[1] for name in worker} == expected, worker
    assert all(b"SYNTHETIC_PACKAGE_CANARY" not in package.read(name) for name in package.namelist())
published = root / "launcher" / "worker"
assert {str(path.relative_to(published)) for path in published.rglob("*") if path.is_file()} == expected
print("PASS: CLI package and launcher publish contain exactly the six reviewed worker modules")
PY
