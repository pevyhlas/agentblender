#!/usr/bin/env bash
# Builds the agentblender release bundle (008 FR-014, plan C2) and proves the package
# installs (AC-32). Publishes nothing: tagging and publishing a release is an owner step.
#
# Usage: scripts/release-bundle.sh [--out DIR] [--allow-dirty]
#
#   1. requires the reference SDK (global.json "version"; CI selects exactly it, FR-015);
#   2. packs src/Abl.Cli (Release) into agentblender.<version>.nupkg and checks that it
#      declares the MIT license and the project URL and carries LICENSE, THIRD-PARTY-NOTICES.md
#      and every licenses/*.txt (the notices of the third-party assemblies it redistributes);
#   3. installs it into a temporary --tool-path and, in an empty environment with a
#      temporary HOME and working directory (no checkout, no config), a free daemon.port
#      and a temporary paths.runtime (never 7811 or a live daemon's runtime dir, plan M10), runs
#      `abl --version --json` and `abl doctor --quick --json`: worker.files must pass
#      against the packaged manifest, from the packaged worker/ (blender.* may fail);
#      then, with that worker/ moved aside and a decoy worker/ above the package, worker.files
#      must fail on the packaged path (a Release abl never searches parent directories);
#   4. writes SHA256SUMS and provenance.json next to the package and checks the sums.
#
# The bundle lands in DIR (default artifacts/release-bundle), replacing an earlier one.
# Works with the macOS system bash (3.2) and on Linux; needs dotnet, git, jq and unzip.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd -P)"
PROJECT="$ROOT_DIR/src/Abl.Cli/Abl.Cli.csproj"
PACKAGE_ID="agentblender"
OUT_DIR="$ROOT_DIR/artifacts/release-bundle"
ALLOW_DIRTY=0

die() { printf 'release-bundle: %s\n' "$*" >&2; exit 1; }
usage() { printf '%s\n' "Usage: scripts/release-bundle.sh [--out DIR] [--allow-dirty]" >&2; exit 2; }

while [ $# -gt 0 ]; do
    case "$1" in
        --out) [ $# -ge 2 ] || usage; OUT_DIR="$2"; shift 2 ;;
        --allow-dirty) ALLOW_DIRTY=1; shift ;;
        -h|--help) usage ;;
        *) usage ;;
    esac
done

for command in dotnet git jq unzip; do
    command -v "$command" > /dev/null || die "required command not found: $command"
done

sha256() {
    if command -v sha256sum > /dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

cd "$ROOT_DIR"

# --- 1. the reference SDK -------------------------------------------------------------
REFERENCE_SDK="$(git show HEAD:global.json | jq -r '.sdk.version')"
[ "$(jq -r '.sdk.version' global.json)" = "$REFERENCE_SDK" ] \
    || die "global.json names another SDK than the committed one ($REFERENCE_SDK)"
SDK_VERSION="$(dotnet --version)"
[ "$SDK_VERSION" = "$REFERENCE_SDK" ] \
    || die "a release bundle builds with the reference SDK $REFERENCE_SDK only, but this shell selects $SDK_VERSION (install $REFERENCE_SDK; CI selects it explicitly)"

# --- source state ---------------------------------------------------------------------
SOURCE_COMMIT="$(git rev-parse --verify 'HEAD^{commit}')"
# global.json is excluded: CI rewrites it to select the reference SDK (same version, checked above).
DIRTY="$(git status --porcelain=v1 --untracked-files=normal -- . ':(exclude)global.json')"
SOURCE_DIRTY=false
if [ -n "$DIRTY" ]; then
    [ "$ALLOW_DIRTY" = 1 ] || die "the source tree has uncommitted changes; commit them, or pass --allow-dirty for a bundle marked dirty"
    SOURCE_DIRTY=true
fi

VERSION="$(dotnet msbuild "$PROJECT" --nologo -getProperty:PackageVersion -p:Configuration=Release)"
case "$VERSION" in
    ''|*[!0-9A-Za-z.-]*) die "unexpected package version '$VERSION'" ;;
esac
NUPKG="$PACKAGE_ID.$VERSION.nupkg"

if [ -L "$OUT_DIR" ] || { [ -e "$OUT_DIR" ] && [ ! -d "$OUT_DIR" ]; }; then
    die "refusing an output path that is a symlink or not a directory: $OUT_DIR"
fi
if [ -d "$OUT_DIR" ]; then
    # Only an earlier bundle is replaced; any other content is left alone.
    for existing in "$OUT_DIR"/* "$OUT_DIR"/.[!.]*; do
        [ -e "$existing" ] || continue
        case "$(basename "$existing")" in
            "$PACKAGE_ID".*.nupkg|SHA256SUMS|provenance.json) ;;
            *) die "$OUT_DIR holds more than a release bundle ($(basename "$existing")); pick an empty or new --out" ;;
        esac
    done
fi

WORK="$(mktemp -d)"
RUNTIME_BASE=""
trap 'rm -rf -- "$WORK"; [ -z "$RUNTIME_BASE" ] || rm -rf -- "$RUNTIME_BASE"' EXIT
STAGE="$WORK/bundle"
TOOLS="$WORK/tools"
mkdir "$STAGE" "$TOOLS" "$WORK/home" "$WORK/cwd"
TOOLS="$(cd "$TOOLS" && pwd -P)"

# --- 2. pack --------------------------------------------------------------------------
echo "== pack $PACKAGE_ID $VERSION (source $SOURCE_COMMIT, SDK $SDK_VERSION)"
dotnet restore "$PROJECT" --locked-mode --nologo
dotnet pack "$PROJECT" --configuration Release --no-restore --nologo --output "$STAGE" -p:ContinuousIntegrationBuild=true
[ -f "$STAGE/$NUPKG" ] || die "expected package is missing: $NUPKG"

# The package redistributes Apache-2.0 and MIT assemblies, so it carries their notices
# (THIRD-PARTY-NOTICES.md) and declares its own license and home.
echo "== license metadata and third-party notices in the package"
PACKAGED_FILES="$(unzip -Z1 "$STAGE/$NUPKG")" || die "cannot list the files in $NUPKG"
REQUIRED_NOTICES="LICENSE THIRD-PARTY-NOTICES.md"
for notice in "$ROOT_DIR"/licenses/*.txt; do
    [ -f "$notice" ] || die "licenses/ holds no license texts"
    REQUIRED_NOTICES="$REQUIRED_NOTICES licenses/$(basename "$notice")"
done
for notice in $REQUIRED_NOTICES; do
    printf '%s\n' "$PACKAGED_FILES" | grep -Fqx -- "$notice" || die "$NUPKG lacks $notice"
done
NUSPEC="$(unzip -p "$STAGE/$NUPKG" "$PACKAGE_ID.nuspec")" || die "cannot read $PACKAGE_ID.nuspec from $NUPKG"
for element in '<license type="expression">MIT</license>' '<projectUrl>https://github.com/pevyhlas/agentblender</projectUrl>'; do
    case "$NUSPEC" in
        *"$element"*) ;;
        *) die "$PACKAGE_ID.nuspec lacks $element" ;;
    esac
done

# --- 3. install into a temporary tool path and check it (AC-32) -----------------------
# --source replaces every configured feed: the package must come from this bundle, never from
# a feed that serves the same id and version (nuget.org included).
echo "== install into a temporary --tool-path"
NUGET_PACKAGES="$WORK/nuget" dotnet tool install "$PACKAGE_ID" --tool-path "$TOOLS" --source "$STAGE" --version "$VERSION"

# The shim needs only the runtime: DOTNET_ROOT is where Microsoft.NETCore.App lives.
DOTNET_DIR="$(dotnet --list-runtimes | sed -n 's|^Microsoft\.NETCore\.App 10\.[^ ]* \[\(.*\)/shared/Microsoft\.NETCore\.App\]$|\1|p' | tail -n 1)"
[ -n "$DOTNET_DIR" ] && [ -x "$DOTNET_DIR/dotnet" ] || die "no .NET 10 runtime found by dotnet --list-runtimes"
# Isolation (plan M10): the doctor run owns its port and runtime dir. Defaults would use 7811
# (bind, or a /healthz probe of whatever daemon holds it) and, on macOS where the temporary
# home's state-dir socket path is too long, the shared /tmp/abl-<uid>. ABL_RUNTIME_DIR names
# a dir that does not exist, so runtime.* checks only its path (no bind probe, nothing created).
RUNTIME_DIR="$WORK/run"
if [ "${#RUNTIME_DIR}" -gt 80 ]; then
    # sun_path is 104 bytes on macOS; keep the longest socket path well inside it.
    RUNTIME_BASE="$(mktemp -d /tmp/abl-rb.XXXXXX)"
    RUNTIME_DIR="$RUNTIME_BASE/run"
fi
# A free port: nothing accepts a connection there. Ports 20000-29999 avoid both the default
# and the ephemeral range most hosts use for outgoing connections.
DOCTOR_PORT=""
attempt=0
while [ -z "$DOCTOR_PORT" ] && [ "$attempt" -lt 50 ]; do
    attempt=$((attempt + 1))
    candidate=$((20000 + RANDOM % 10000))
    if ! (exec 3<> "/dev/tcp/127.0.0.1/$candidate") 2> /dev/null; then
        DOCTOR_PORT="$candidate"
    fi
done
[ -n "$DOCTOR_PORT" ] || die "found no free local port for the doctor run"
clean() {
    (cd "$WORK/cwd" && /usr/bin/env -i HOME="$WORK/home" PATH="$DOTNET_DIR:/usr/bin:/bin" DOTNET_ROOT="$DOTNET_DIR" \
        ABL_PORT="$DOCTOR_PORT" ABL_RUNTIME_DIR="$RUNTIME_DIR" \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 LANG=C.UTF-8 "$@")
}

clean "$TOOLS/abl" --version --json > "$WORK/version.json" || die "abl --version --json failed"
INSTALLED_VERSION="$(jq -r '.version' "$WORK/version.json")"
[ "$INSTALLED_VERSION" = "$VERSION" ] || die "the installed abl reports version $INSTALLED_VERSION, not $VERSION"
for field in .supportedBlender.line .supportedBlender.reference; do
    [ "$(jq -r "$field // empty" "$WORK/version.json")" != "" ] || die "abl --version --json lacks $field: $(cat "$WORK/version.json")"
done

echo "== abl doctor --quick --json (installed package, empty environment)"
set +e
clean "$TOOLS/abl" doctor --quick --json > "$WORK/doctor.json"
DOCTOR_EXIT=$?
set -e
# 0: all passed; 1: some check failed (a missing Blender is expected here); anything else is broken.
[ "$DOCTOR_EXIT" -le 1 ] || die "abl doctor exited $DOCTOR_EXIT: $(cat "$WORK/doctor.json")"
jq -r '.checks[] | "\(.status)\t\(.id)\t\(.summary)"' "$WORK/doctor.json"
# The run used the isolated port and runtime dir, not a default.
[ "$(jq -r '.checks[] | select(.id == "runtime.dir") | .detail.path' "$WORK/doctor.json")" = "$RUNTIME_DIR" ] \
    || die "abl doctor did not use the temporary runtime dir $RUNTIME_DIR: $(cat "$WORK/doctor.json")"
case "$(jq -r '.checks[] | select(.id == "daemon.port") | .summary' "$WORK/doctor.json")" in
    "port $DOCTOR_PORT "*) ;;
    *) die "abl doctor did not check the isolated port $DOCTOR_PORT: $(cat "$WORK/doctor.json")" ;;
esac

WORKER_FILES="$(jq -c '.checks[] | select(.id == "worker.files")' "$WORK/doctor.json")"
[ "$(jq -r '.status' <<< "$WORKER_FILES")" = "pass" ] || die "worker.files did not pass: $WORKER_FILES"
[ "$(jq -r '.detail.packaged' <<< "$WORKER_FILES")" = "true" ] || die "worker.files did not check the packaged worker/: $WORKER_FILES"
WORKER_DIR="$(jq -r '.detail.dir' <<< "$WORKER_FILES")"
case "$WORKER_DIR" in
    "$TOOLS"/*) ;;
    *) die "the installed abl resolved worker/ outside its package: $WORKER_DIR" ;;
esac
WORKER_DIGEST="$(jq -r '.detail.digest' <<< "$WORKER_FILES")"
[ "$(jq -r '.detail.manifest' <<< "$WORKER_FILES")" = "$WORKER_DIGEST" ] || die "worker/ differs from its package manifest: $WORKER_FILES"
[ "$(jq -r '.worker.manifestDigest' "$WORK/version.json")" = "$WORKER_DIGEST" ] || die "abl --version --json reports another worker digest"

# A Release (packaged) abl never searches parent directories for worker/ (FR-014, L10). With
# the packaged worker/ moved aside and a worker/worker.py in an ancestor of the package, the
# check must fail on the packaged path, not resolve the ancestor.
echo "== worker.files without the packaged worker/ (no parent-directory search)"
mv -- "$WORKER_DIR" "$WORK/worker-aside"
mkdir "$TOOLS/worker"
: > "$TOOLS/worker/worker.py"
set +e
clean "$TOOLS/abl" doctor --check worker.files --json > "$WORK/walkup.json"
WALKUP_EXIT=$?
set -e
rm -rf -- "$TOOLS/worker"
mv -- "$WORK/worker-aside" "$WORKER_DIR"
[ "$WALKUP_EXIT" -le 1 ] || die "abl doctor --check worker.files exited $WALKUP_EXIT: $(cat "$WORK/walkup.json")"
WALKUP="$(jq -c '.checks[] | select(.id == "worker.files")' "$WORK/walkup.json")"
[ "$(jq -r '.status' <<< "$WALKUP")" = "fail" ] || die "worker.files did not fail without the packaged worker/: $WALKUP"
[ "$(jq -r '.detail.dir' <<< "$WALKUP")" = "$WORKER_DIR" ] || die "the installed abl searched parent directories for worker/: $WALKUP"

# --- 4. SHA256SUMS and provenance.json --------------------------------------------------
(cd "$STAGE" && sha256 "$NUPKG" > SHA256SUMS)
PACKAGE_SHA256="$(cut -d ' ' -f 1 < "$STAGE/SHA256SUMS")"
jq -n \
    --arg id "$PACKAGE_ID" --arg version "$VERSION" --arg file "$NUPKG" --arg sha256 "$PACKAGE_SHA256" \
    --arg commit "$SOURCE_COMMIT" --argjson dirty "$SOURCE_DIRTY" \
    --arg sdk "$SDK_VERSION" --arg reference "$REFERENCE_SDK" \
    --slurpfile info "$WORK/version.json" --arg worker "$WORKER_DIGEST" \
    '{
        format: "agentblender.provenance.v1",
        package: { id: $id, version: $version, file: $file, sha256: $sha256 },
        source: { commit: $commit, dirty: $dirty },
        sdk: { version: $sdk, reference: $reference },
        runtime: { requires: ["Microsoft.NETCore.App 10.0", "Microsoft.AspNetCore.App 10.0"] },
        blender: { supportedLine: $info[0].supportedBlender.line, reference: $info[0].supportedBlender.reference },
        worker: { digest: $worker },
        installCheck: { command: "abl doctor --quick --json", workerFiles: "pass" }
    }' > "$STAGE/provenance.json"
(cd "$STAGE" && sha256 -c SHA256SUMS)

mkdir -p "$OUT_DIR"
rm -f -- "$OUT_DIR"/"$PACKAGE_ID".*.nupkg "$OUT_DIR/SHA256SUMS" "$OUT_DIR/provenance.json"
mv "$STAGE/$NUPKG" "$STAGE/SHA256SUMS" "$STAGE/provenance.json" "$OUT_DIR/"

echo "== release bundle: $OUT_DIR"
ls -l "$OUT_DIR"
echo "Nothing was tagged or published."
