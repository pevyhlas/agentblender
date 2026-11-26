"""Collect exact-head review evidence without making a gate decision."""

import argparse
from datetime import UTC, datetime
from difflib import unified_diff
import fcntl
from fnmatch import fnmatchcase
from hashlib import file_digest, sha256
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import stat
import subprocess
import sys
from tempfile import NamedTemporaryFile, TemporaryDirectory, TemporaryFile


VERSION = 3
SUMMARY_LIMIT = 60
CONFIG = ".prdkit/memory/review-checks.json"
CACHE = ".prdkit/cache/evidence"
SCRIPTS = ".prdkit/scripts/bash"
FILES = ("index.json", "summary.md")
DIFF = ("git", "diff", "--no-ext-diff", "--no-textconv", "--ignore-submodules=none",
        "--no-relative", "--no-color")


class Refusal(Exception):
    pass


def require(condition, reason):
    if not condition:
        raise Refusal(reason)


def digest(data):
    return sha256(data).hexdigest()


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, indent=2, allow_nan=False) + "\n").encode("ascii")


def unique_object(pairs):
    obj = {}
    for key, value in pairs:
        require(key not in obj, f"duplicate JSON key: {key!r}")
        obj[key] = value
    return obj


def one_line(text, limit=220):
    text = " ".join(text.split())
    text = "".join(c if c.isprintable() else ascii(c)[1:-1] for c in text)
    return text if len(text) <= limit else text[:limit - 1] + "…"


def mode(path):
    try:
        return path.lstat().st_mode
    except (FileNotFoundError, NotADirectoryError):
        return 0


def glob_matches(pattern, path):
    # Match slash-separated components. Only an entire ** component crosses directories.
    names = path.split("/")
    positions = {0}
    for part in pattern.split("/"):
        if not positions:
            return False
        if part == "**":
            positions = set(range(min(positions), len(names) + 1))
        else:
            positions = {i + 1 for i in positions if i < len(names) and fnmatchcase(names[i], part)}
    return len(names) in positions


def load_map(root):
    path = root / CONFIG
    if not mode(path):
        return None, {"kind": "absent"}
    require(stat.S_ISREG(mode(path)), f"{CONFIG}: expected a regular JSON file")
    raw = path.read_bytes()
    try:
        rules = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
        require(isinstance(rules, dict), "expected an object mapping globs to check names")
        for pattern, checks in rules.items():
            require(pattern.isprintable() and "\\" not in pattern and all(
                part not in {"", ".", ".."} and ("**" not in part or part == "**")
                for part in pattern.split("/")), f"invalid repository-relative glob {pattern!r}")
            require(isinstance(checks, list) and checks and all(
                isinstance(name, str) and name.strip() and name.isprintable() for name in checks
            ) and len(set(checks)) == len(checks), f"{pattern!r}: expected distinct nonempty check names")
        return {key: sorted(rules[key]) for key in sorted(rules)}, {"kind": "file", "sha256": digest(raw)}
    except (ValueError, Refusal, RecursionError) as exc:
        raise Refusal(f"{CONFIG}: {exc}") from exc


class Collector:
    def __init__(self):
        self.scripts = Path(__file__).parent.resolve()
        self.env = dict(os.environ)
        for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR",
                     "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE",
                     "GIT_GRAFT_FILE", "GIT_SHALLOW_FILE", "GIT_PREFIX", "GIT_CONFIG",
                     "GIT_QUARANTINE_PATH", "GIT_ATTR_SOURCE", "BASH_ENV", "ENV", "CDPATH"):
            self.env.pop(name, None)
        self.env.update(GIT_NO_LAZY_FETCH="1", GIT_NO_REPLACE_OBJECTS="1", GIT_OPTIONAL_LOCKS="0",
                         GIT_ATTR_NOSYSTEM="1", GIT_GRAFT_FILE=os.devnull,
                        GIT_ALLOW_PROTOCOL="", GIT_TERMINAL_PROMPT="0", LC_ALL="C", TZ="UTC",
                        PRDKIT_TODAY=self.env.get("PRDKIT_TODAY") or datetime.now(UTC).date().isoformat())
        self.root = self.scripts
        self.root = Path(os.fsdecode(self.git("rev-parse", "--show-toplevel").removesuffix(b"\n"))).resolve()
        require(self.scripts == self.root / SCRIPTS,
                "run the installed copy under the consumer Git root: .prdkit/scripts/bash/facts.sh")

    def run(self, command, *, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE):
        return subprocess.run(command, cwd=self.root, env=self.env, stdin=stdin,
                              stdout=stdout, stderr=subprocess.PIPE, check=False)

    def git(self, *args, allowed=(0,), **streams):
        proc = self.run(("git", *args), **streams)
        require(proc.returncode in allowed, f"git {shlex.join(args)}: {one_line(self.text(proc.stderr)) or 'failed'}")
        return proc.stdout

    def rev(self, ref):
        return self.git("rev-parse", "--verify", "--end-of-options", f"{ref}^{{commit}}").decode().strip()

    def check_head(self, head):
        require(self.rev("HEAD") == head, "checkout HEAD moved or does not match --head")

    def normalize(self, text):
        return text.replace(f"{self.root}/", "")

    def text(self, raw):
        return self.normalize(raw.decode("utf-8", "backslashreplace")).rstrip("\n")

    def git_path(self, name):
        return self.root / os.fsdecode(self.git("rev-parse", "--git-path", name).removesuffix(b"\n"))

    def capture(self, command, *, argv=None, diagnostics=None):
        diagnostics = diagnostics or self
        try:
            proc = self.run(argv or command)
        except OSError as exc:
            return {"command": command, "exit_status": None, "result": f"could not start: {exc.strerror}"}, None
        return ({"command": command, "exit_status": proc.returncode, "result": f"exit {proc.returncode}",
                 "stderr": self.normalize(diagnostics.text(proc.stderr))}, proc.stdout)

    def fact(self, command):
        fact, raw = self.capture(command)
        return {**fact, "details": self.text(raw) if raw is not None else None}


class Snapshot(Collector):
    """Private Git metadata and raw HEAD blobs; never register a consumer worktree."""

    def __init__(self, source, directory, base, head):
        self.root = directory / "head"
        self.root.mkdir()
        scratch = directory / "tmp"
        scratch.mkdir()
        self.env = {key: source.env[key] for key in (
            "LC_ALL", "TZ", "PRDKIT_TODAY", "GIT_ALLOW_PROTOCOL", "GIT_TERMINAL_PROMPT",
            "GIT_NO_LAZY_FETCH", "GIT_NO_REPLACE_OBJECTS", "GIT_OPTIONAL_LOCKS", "GIT_ATTR_NOSYSTEM",
        )}
        paths = source.env.get("PATH", os.defpath).split(os.pathsep)
        self.env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull, HOME=str(directory),
                        PATH=os.pathsep.join(path for path in paths if os.path.isabs(path)) or os.defpath,
                        TMPDIR=str(scratch))
        object_format = source.git("rev-parse", "--show-object-format").decode().strip()
        self.git("init", "--quiet", "--template=", f"--object-format={object_format}")
        self.shallow = read_optional(source.git_path("shallow"))
        if self.shallow is not None:
            (self.root / ".git/shallow").write_bytes(self.shallow)
        (self.root / ".git/HEAD").write_text(f"{head}\n", encoding="ascii")
        self.freeze_objects(source, scratch, head)
        require(self.run(("git", "merge-base", "--is-ancestor", base, head)).returncode == 0,
                "base is not in the snapshot's available HEAD history")
        self.git("read-tree", head)
        self.materialize(head)

    def freeze_objects(self, source, scratch, head):
        # Use the consumer store only to build a self-contained pack. In particular,
        # doctor's abbreviation lookup must never see unrelated or newly hydrated objects.
        objects = os.fsencode(source.git_path("objects").resolve())
        require(b"\n" not in objects, "Git object store path contains a newline")
        alternate = self.root / ".git/objects/info/alternates"
        alternate.write_bytes(objects + b"\n")
        try:
            with TemporaryFile(dir=scratch) as inventory:
                self.git("rev-list", "--objects", "--no-object-names", "--missing=error", head, stdout=inventory)
                inventory.seek(0)
                # Use a non-thin pack: fresh deltas can only use bases in the explicit inventory.
                self.git("pack-objects", "--quiet", "--no-reuse-delta",
                         ".git/objects/pack/pack", stdin=inventory)
        finally:
            alternate.unlink()

    def normalize(self, text):
        return super().normalize(text).replace(str(self.root), ".").replace(self.env["TMPDIR"], "<scratch>")

    def inputs(self, base, head):
        # These are the external utilities used by the shipped local collectors.
        tools = {}
        for name in ("awk", "basename", "bash", "cat", "cksum", "cmp", "cut", "date", "dd",
                     "dirname", "find", "git", "grep", "head", "mktemp", "od", "readlink",
                     "rm", "sed", "sha256sum", "shasum", "sort", "tail", "tr", "uniq", "wc"):
            path = shutil.which(name, path=self.env.get("PATH"))
            if path:
                with open(path, "rb") as stream:
                    tools[name] = file_digest(stream, "sha256").hexdigest()
            else:
                tools[name] = None
        return {"base_tree": self.git("rev-parse", f"{base}^{{tree}}").decode().strip(),
                "head_tree": self.git("rev-parse", f"{head}^{{tree}}").decode().strip(),
                "collector_sha256": digest(Path(__file__).read_bytes()),
                "shallow_sha256": digest(self.shallow) if self.shallow is not None else None,
                "local_tools_sha256": digest(json_bytes(tools)),
                "doctor_date_sha256": digest(self.env["PRDKIT_TODAY"].encode())}

    def script(self, name, *args):
        return self.fact(["bash", f"{SCRIPTS}/{name}.sh", *args])

    def materialize(self, head):
        blobs, links = [], []
        for entry in self.git("ls-tree", "-rz", head).split(b"\0")[:-1]:
            metadata, name = entry.split(b"\t", 1)
            perms, kind, oid = metadata.split()
            parts = os.fsdecode(name).split("/")
            require(all(part.casefold() not in {"", ".", "..", ".git"} for part in parts),
                    f"unsupported snapshot path: {name!r}")
            path = self.root.joinpath(*parts)
            if perms == b"160000" and kind == b"commit":
                path.mkdir(parents=True)
            else:
                require(kind == b"blob" and perms in {b"100644", b"100755", b"120000"},
                        f"unsupported snapshot entry: {name!r}")
                blobs.append((path, perms, oid))
        # Stream raw blobs rather than checkout filters, EOL conversion or archive attributes.
        # Materialize symlinks last, so no blob write can follow one.
        with TemporaryFile() as requests:
            requests.writelines(oid + b"\n" for _, _, oid in blobs)
            requests.seek(0)
            with subprocess.Popen(["git", "cat-file", "--batch"], cwd=self.root, env=self.env,
                                  stdin=requests, stdout=subprocess.PIPE) as proc:
                try:
                    stream = proc.stdout
                    for path, perms, oid in blobs:
                        header = stream.readline().split()
                        require(len(header) == 3 and header[:2] == [oid, b"blob"] and header[2].isdigit(),
                                f"snapshot blob unavailable: {oid.decode()}")
                        size = int(header[2])
                        path.parent.mkdir(parents=True, exist_ok=True)
                        if perms == b"120000":
                            require(0 < size < 4096, f"unsupported symlink: {path.relative_to(self.root)}")
                            links.append((path, os.fsdecode(stream.read(size))))
                        else:
                            with path.open("xb") as output:
                                while size:
                                    chunk = stream.read(min(size, 1024 * 1024))
                                    require(chunk, "incomplete snapshot blob")
                                    output.write(chunk)
                                    size -= len(chunk)
                            path.chmod(0o755 if perms == b"100755" else 0o644)
                        require(stream.read(1) == b"\n", "incomplete snapshot blob")
                    require(proc.wait() == 0, "could not read snapshot blobs")
                except BaseException:
                    proc.kill()
                    raise
        for path, target in links:
            level = len(path.parent.relative_to(self.root).parts)
            require(not os.path.isabs(target), f"snapshot symlink leaves HEAD: {path.relative_to(self.root)}")
            for part in target.split("/"):
                level += -1 if part == ".." else int(part not in {"", "."})
                require(level >= 0, f"snapshot symlink leaves HEAD: {path.relative_to(self.root)}")
            path.symlink_to(target)
        for path, _ in links:
            try:
                resolved = path.resolve()
            except RuntimeError as exc:
                raise Refusal(f"snapshot symlink cycle: {path.relative_to(self.root)}") from exc
            require(resolved.is_relative_to(self.root) and not resolved.is_relative_to(self.root / ".git"),
                    f"snapshot symlink leaves HEAD: {path.relative_to(self.root)}")


def read_optional(path):
    try:
        return path.read_bytes()
    except FileNotFoundError:
        return None


def local_json(snapshot, command, *, doctor):
    fact, raw = snapshot.capture(command)
    try:
        data = json.loads(raw, object_pairs_hook=unique_object)
        result = data["result"]
        findings = data["findings"]
        advisories = data["advisories"] if doctor else []
        require((result, fact["exit_status"]) in {("pass", 0), ("fail", 1)} and
                all(isinstance(rows, list) and all(isinstance(line, str) for line in rows)
                    for rows in (findings, advisories)) and bool(findings) == (result == "fail"),
                "inconsistent collector result")
        # Check that the full payload is safe to serialize, not just the fields summarized.
        json_bytes(data)
        for rows in (findings, advisories):
            rows[:] = map(snapshot.normalize, rows)
        fact["details"] = data
        fact["result"] = f"{result}: {len(findings)} finding(s)"
        fact["result"] += f", {len(advisories)} advisory item(s)" if doctor else " (default installation checks)"
    except (ValueError, TypeError, AttributeError, KeyError, Refusal, RecursionError):
        fact["result"] += ": no valid collector report"
        fact["details"] = snapshot.text(raw) if raw is not None else None
    return fact


def ci_fact(collector, snapshot, pr, head):
    command = ["bash", f"{SCRIPTS}/pr-state.sh", *([str(pr)] if pr else [])]
    if not pr or not shutil.which("gh", path=collector.env.get("PATH")):
        reason = "no --pr supplied" if not pr else "gh not available on PATH"
        return {"command": command, "exit_status": None, "result": f"not collected: {reason}", "details": None}
    script = snapshot.root / command[1]
    fact, raw = collector.capture(command, argv=["bash", str(script), *command[2:]], diagnostics=snapshot)
    fact["details"] = None
    if fact["exit_status"] != 0:
        reason = one_line(fact.get("stderr", "")) or fact["result"]
    else:
        try:
            data = json.loads(raw, object_pairs_hook=unique_object)
            observed = data["head_sha"]
            require(observed == head, f"PR head {observed!r} does not match the checkout head")
            require(data["number"] == pr, "PR number differs")
            require(isinstance(data["required_checks"], list), "missing required check inventory")
            checks = [{key: check[key] for key in ("name", "status", "conclusion", "link")}
                      for check in data["required_checks"]]
            require(all(isinstance(check[key], str) for check in checks for key in ("name", "status")) and
                    all(check[key] is None or isinstance(check[key], str)
                        for check in checks for key in ("conclusion", "link")), "invalid required check")
            checks.sort(key=lambda check: check["name"])
            require(len({check["name"] for check in checks}) == len(checks), "duplicate required check")
            # Keep only the binding and required-check inventory, not fetched_at or reviews.
            observed = {key: data[key] for key in ("repo", "number", "head_sha", "base_ref", "base_sha")}
            observed["required_checks"] = checks
            json_bytes(observed)
            fact["details"] = observed
            green = sum(check["status"] == "COMPLETED" and check["conclusion"] == "SUCCESS" for check in checks)
            fact["result"] = (f"{green}/{len(checks)} required check(s) green" if checks
                              else "no required checks reported")
            return fact
        except (ValueError, TypeError, KeyError, Refusal, RecursionError) as exc:
            reason = f"unusable pr-state report ({one_line(str(exc))})"
    fact["result"] = f"not collected: {reason}"
    return fact


def collect(collector, snapshot, change_id, base, head, pr):
    inputs = snapshot.inputs(base, head)
    rules, config_stamp = load_map(snapshot.root)
    span = f"{base}..{head}"
    facts = {"doctor": local_json(snapshot, ["bash", f"{SCRIPTS}/doctor.sh", "--json"], doctor=True)}
    bounds = snapshot.script("bounds", change_id)
    if (bounds["exit_status"] == 0 and bounds["details"].startswith(f"Bounds for {change_id} ") and
            bounds["details"].splitlines()[-1].startswith("Mark each in the record's Bounds:")):
        bounds["result"] = "applicable bounds listed below and in index.json"
    else:
        bounds["result"] = f"bounds not collected ({bounds['result']})"
    facts["bounds"] = bounds
    diff = snapshot.fact([*DIFF, "--check", span, "--"])
    diff["result"] = {0: "clean", 2: "whitespace or conflict-marker findings"}.get(
        diff["exit_status"], f"diff check failed ({diff['result']})")
    facts["diff_check"] = diff
    facts["adoption"] = local_json(snapshot, ["bash", f"{SCRIPTS}/check-adoption.sh", "--json"], doctor=False)
    facts["ci"] = ci_fact(collector, snapshot, pr, head)
    facts["path_checks"] = map_paths(snapshot, span, rules, facts["ci"])
    require(snapshot.inputs(base, head) == inputs, "runtime inputs changed while collecting facts; retry")
    return {"change_id": change_id, "key": {"version": VERSION, "base": base, "head": head},
            "inputs": {**inputs, "config": config_stamp, "ci_sha256": digest(json_bytes(facts["ci"]))},
            "facts": facts}


def map_paths(collector, span, rules, ci):
    command = [*DIFF, "--name-only", "-z", "--no-renames", span, "--"]
    proc = collector.run(command)
    paths = []
    complete = proc.returncode == 0 and (not proc.stdout or proc.stdout.endswith(b"\0"))
    if complete:
        paths = sorted(set(os.fsdecode(name) for name in proc.stdout.split(b"\0")[:-1]))
    mapped = {}
    reported = {check["name"]: check for check in (ci["details"] or {}).get("required_checks", [])}
    for path in paths:
        names = sorted({name for pattern, checks in (rules or {}).items()
                        if glob_matches(pattern, path) for name in checks})
        mapped[path] = [{"name": name, "state": ("reported" if name in reported else
                         "not reported as required" if ci["details"] is not None else "not collected"),
                         "status": reported.get(name, {}).get("status"),
                         "conclusion": reported.get(name, {}).get("conclusion")} for name in names]
    configuration = "absent" if rules is None else "present"
    result = (f"configuration {configuration}; {sum(map(bool, mapped.values()))}/{len(paths)} changed paths matched"
              if complete else f"configuration {configuration}; changed paths not collected")
    return {"command": command, "exit_status": proc.returncode, "result": result,
            "stderr": collector.text(proc.stderr), "config": CONFIG, "rules": rules, "paths": mapped}


def summary(pack, index):
    key, facts = pack["key"], pack["facts"]
    lines = [
        f"# Review facts: {pack['change_id']}", "",
        f"Authoritative evidence for base `{key['base']}` and head `{key['head']}` only.",
        "Answers: What did doctor and adoption report? Is the range free of `git diff --check` findings?",
        "Which bounds apply to the diff? Which checks does the path map require, and are required CI checks green at this head?",
        "Use these captured facts; do not rerun the collectors or reread the spec to derive these answers.",
        "Does not answer design fit, correctness, or spec fit, including whether the diff respects the listed bounds. Those are the reviewer's job.",
        "Evidence only — not a gate decision, approval, or merge authorization.", "",
        f"Key: facts v{key['version']} · `{key['base']}..{key['head']}`.",
        f"Full facts and input stamps: [index.json](index.json) (SHA-256 `{digest(index)}`).", "",
    ]
    labels = {"doctor": "Doctor", "bounds": "Bounds", "diff_check": "Diff whitespace",
              "adoption": "Adoption", "ci": "Exact-head CI", "path_checks": "Path → required checks"}
    for name, label in labels.items():
        lines.append(f"- **{label}:** {one_line(facts[name]['result'])}")

    detail = []
    for name in ("bounds", "ci", "path_checks", "doctor", "diff_check", "adoption"):
        label = labels[name]
        fact = facts[name]
        data = fact.get("details")
        if name in {"doctor", "adoption"} and isinstance(data, dict):
            rows = data["findings"] + [f"Advisory: {line}" for line in data.get("advisories", [])]
        elif name == "ci" and data:
            rows = [f"{row['name']}: {row['conclusion'] or row['status']}" for row in data["required_checks"]]
        elif name == "path_checks":
            rows = [f"{path} → " + ", ".join(f"{row['name']}: {row['conclusion'] or row['status'] or row['state']}"
                                            for row in checks)
                    for path, checks in fact["paths"].items() if checks]
        else:
            rows = data.splitlines() if isinstance(data, str) else []
        rows.extend(f"Diagnostic: {line}" for line in fact.get("stderr", "").splitlines())
        detail.extend(f"    {label}: {one_line(line)}" for line in rows if line.strip())
    if detail:
        lines.extend(("", "## Captured details", ""))
        room = SUMMARY_LIMIT - len(lines)
        if len(detail) > room:
            shown = room - 2
            lines.extend(detail[:shown])
            lines.extend(("", f"{len(detail) - shown} more detail line(s): see [index.json](index.json)."))
        else:
            lines.extend(detail)
    # Every short rendering has a full indexed counterpart; clipping per line is explicit.
    text = "\n".join(lines) + "\n"
    require(len(text.splitlines()) <= SUMMARY_LIMIT, "internal error: summary exceeded line limit")
    return text.encode("utf-8")


def cache_dir(collector, change_id, verify):
    directory = collector.root
    rel = Path(CACHE) / change_id / "facts"
    for part in rel.parts:
        directory /= part
        if not mode(directory):
            if verify:
                return None
            directory.mkdir(exist_ok=True)
        require(stat.S_ISDIR(mode(directory)), f"unsafe cache directory: {directory.relative_to(collector.root)}")
    return directory


def compare(directory, new, pack):
    changed = [name for name in FILES if directory is None or not mode(directory / name)
               or (directory / name).read_bytes() != new[name]]
    if not changed:
        return 0
    print("facts drift: " + ", ".join(changed))
    if "index.json" in changed and directory is not None and mode(directory / "index.json"):
        try:
            old = json.loads((directory / "index.json").read_bytes())
            for name in ("key", "change_id", "inputs"):
                if old.get(name) != pack[name]:
                    if name == "key":
                        print(f"- key: {one_line(str(old.get(name)))}\n+ key: {one_line(str(pack[name]))}")
                    else:
                        print(f"  {name} changed")
            for name, fact in pack["facts"].items():
                prior = old.get("facts", {}).get(name, {})
                if prior == fact:
                    continue
                print(f"- {name}: {one_line(str(prior.get('result', '(missing)')))}")
                print(f"+ {name}: {one_line(fact['result'])}")
                if prior.get("result") == fact["result"]:
                    before = json_bytes(prior).decode().splitlines()
                    after = json_bytes(fact).decode().splitlines()
                    diff = unified_diff(before, after, n=0)
                    next(diff, None)  # file labels would repeat index.json
                    next(diff, None)
                    edits = [line for line in diff if line.startswith(("-", "+"))]
                    for line in edits[:4]:
                        print(f"  {one_line(line)}")
                    if len(edits) > 4:
                        print("  … more fact details differ")
        except (ValueError, AttributeError, RecursionError):
            print("  stored index is not a readable facts index")
    return 1


def write_pack(directory, output):
    staged = {}
    try:
        for name, data in output.items():
            with NamedTemporaryFile(dir=directory, prefix=".facts-", delete=False) as tmp:
                staged[name] = Path(tmp.name)
                tmp.write(data)
                tmp.flush()
                os.fsync(tmp.fileno())
        for name in FILES:
            staged[name].replace(directory / name)
    finally:
        for path in staged.values():
            path.unlink(missing_ok=True)


def interrupted(signum, _frame):
    raise Refusal(f"interrupted by signal {signum}")


def main():
    parser = argparse.ArgumentParser(
        prog="facts.sh", allow_abbrev=False, description="Exact-head review facts; never a gate decision.",
        formatter_class=argparse.RawDescriptionHelpFormatter, epilog=(
            f"Output: {CACHE}/<change-id>/facts/{{summary.md,index.json}}\n"
            "Attach both files (id, base and head must match the pack):\n"
            '  bash .prdkit/scripts/bash/build-review-package.sh "$id" "$base" "$head" '
            f'--evidence "{CACHE}/$id/facts/summary.md" --evidence "{CACHE}/$id/facts/index.json"\n'
            "Exit: 0 pack written or verified, 1 verification drift, 2 refusal."))
    parser.add_argument("change_id")
    parser.add_argument("--base", metavar="REV", help="base commit (default: unique merge base with origin/HEAD)")
    parser.add_argument("--head", metavar="REV", help="require checkout HEAD to resolve to REV (default: HEAD)")
    parser.add_argument("--pr", metavar="N", help="collect required checks via pr-state.sh using gh")
    parser.add_argument("--verify", action="store_true", help="recompute and compare; do not write")
    args = parser.parse_args()
    try:
        require(re.fullmatch(r"[0-9]{3,}-[a-z0-9]+(?:-[a-z0-9]+)*", args.change_id), "invalid change id")
        require(args.pr is None or re.fullmatch(r"[1-9][0-9]{0,8}", args.pr), "--pr expects a positive PR number")
        collector = Collector()
        head = collector.rev("HEAD")
        if args.head is not None:
            require(collector.rev(args.head) == head, "checkout HEAD does not match --head")
        record = f"specs/changes/{args.change_id}.md"
        require(collector.git("ls-tree", head, "--", record).startswith((b"100644 ", b"100755 ")),
                f"unknown change id at HEAD: {record}")
        if args.base is None:
            try:
                tip = collector.rev("refs/remotes/origin/HEAD")
            except Refusal as exc:
                raise Refusal("no local origin/HEAD; pass --base with the review range's base commit") from exc
            bases = collector.git("merge-base", "--all", tip, head, allowed=(0, 1)).decode().splitlines()
            require(len(bases) == 1, "no unique merge base with origin/HEAD; pass --base")
            base = bases[0]
        else:
            base = collector.rev(args.base)
            require(collector.run(("git", "merge-base", "--is-ancestor", base, head)).returncode == 0,
                    "--base must be an ancestor of HEAD")
        def check_cache():
            collector.check_head(head)
            for name in FILES:
                require(collector.run(("git", "check-ignore", "-q", "--",
                                       f"{CACHE}/{args.change_id}/facts/{name}")).returncode == 0,
                        "facts cache must be gitignored and not tracked")

        check_cache()
        for sig in (signal.SIGHUP, signal.SIGINT, signal.SIGTERM):
            signal.signal(sig, interrupted)
        with TemporaryDirectory(prefix="prdkit-facts-") as directory:
            snapshot = Snapshot(collector, Path(directory).resolve(), base, head)
            pack = collect(collector, snapshot, args.change_id, base, head, int(args.pr) if args.pr else None)

        index = json_bytes(pack)
        output = {"index.json": index, "summary.md": summary(pack, index)}
        directory = cache_dir(collector, args.change_id, args.verify)
        if directory is None:
            check_cache()
            return compare(None, output, pack)
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            fcntl.flock(fd, fcntl.LOCK_SH if args.verify else fcntl.LOCK_EX)
            check_cache()
            for name in FILES:
                kind = mode(directory / name)
                require(not kind or stat.S_ISREG(kind), f"unsafe facts file: {name}")
            if args.verify:
                return compare(directory, output, pack)
            write_pack(directory, output)
        finally:
            os.close(fd)
        print(directory.relative_to(collector.root))
        return 0
    except (Refusal, OSError, ValueError) as exc:
        print(f"facts.sh: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
