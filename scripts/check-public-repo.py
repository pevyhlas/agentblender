#!/usr/bin/env python3
"""Developer/CI guard, standard library only; not a comprehensive PII/secret scanner.

Scan tracked working-tree text, never ignored files or private Git refs. Report only
path, line and rule (never the matching value). Binary artwork needs manual review.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.parse import unquote


RULES = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
    "github-token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{20,255})\b"),
    "aws-access-id": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "api-key": re.compile(r"\bsk-(?:proj-|ant-api\d+-)?[A-Za-z0-9_-]{32,}\b"),
}
HOME = re.compile(r"(?<![\w$}./-])(?:/(?:home|Users)/|[A-Za-z]:[\\/]Users[\\/])([\w.-]+)")
# Synthetic fixtures only; no person-specific exemptions or blanket test/notices exclusions.
EXAMPLE_USERS = {"user", "username", "alice", "bob", "a", "u", "test", "testuser", "tester", "someone", "other", "me", "example", "runner", "private", "private-user", "shared"}
GENERATED = re.compile(r"(?:^|/)(?:bin|obj|TestResults|coverage-results|__pycache__)(?:/|$)|(?:^|/)\.env(?:$|\.(?!example$|sample$|template$))|\.(?:pfx|p12|key|pyc|trx|log)$", re.I)
LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^\s)]+)\)")


def text_findings(text):
    findings = []
    for number, line in enumerate(text.splitlines(), 1):
        for name, pattern in RULES.items():
            if pattern.search(line):
                findings.append((number, name))
        if any(match.group(1).lower() not in EXAMPLE_USERS for match in HOME.finditer(line)):
            findings.append((number, "personal-home-path"))
    return findings


def link_findings(root, path, text):
    """Keep the public entry pages navigable; full Markdown parsing is out of scope."""
    findings = []
    for number, line in enumerate(text.splitlines(), 1):
        for match in LINK.finditer(line):
            target = match.group(1)
            if "://" in target or target.startswith("mailto:"):
                continue
            name, _, fragment = target.partition("#")
            destination = (path.parent / unquote(name)).resolve() if name else path
            if not destination.is_relative_to(root) or not destination.exists():
                findings.append((number, "broken-local-link"))
            elif fragment and destination.suffix == ".md" and destination.is_file():
                headings = re.findall(r"^#{1,6}\s+(.*)", destination.read_text(encoding="utf-8"), re.M)
                slugs = {re.sub(r"[^\w\- ]", "", h.lower()).replace(" ", "-") for h in headings}
                if unquote(fragment) not in slugs:
                    findings.append((number, "broken-heading-link"))
    return findings


def self_test():
    cases = [
        ("-----BEGIN " + "OPENSSH PRIVATE KEY-----", "private-key"),
        ("ghp_" + "a" * 36, "github-token"),
        ("github_pat_" + "a" * 30, "github-token"),
        ("AKIA" + "A" * 16, "aws-access-id"),
        ("sk-proj-" + "a" * 40, "api-key"),
        ("/home/" + "private-person/file", "personal-home-path"),
        ("C:\\Users\\" + "private-person\\file", "personal-home-path"),
    ]
    for text, rule in cases:
        assert (1, rule) in text_findings(text), rule
    assert not text_findings("/home/user/file /Users/alice/file C:\\Users\\test\\file")
    assert not text_findings('$BUILD_ROOT/home/extensions /Users/Shared/cache')
    assert not text_findings("Copyright (c) 2026 Contributors; noreply@example.com; /home/<user>/")
    assert text_findings("safe\n" + cases[0][0]) == [(2, "private-key")]
    for name in ["worker/private.log", "src/obj/paths.json", ".env", ".env.local", "cert.pfx"]:
        assert GENERATED.search(name), name
    for name in [".env.example", "LICENSE", "docs/worker-protocol.md", "worker/worker.py"]:
        assert not GENERATED.search(name), name
    integration_test()
    print("PASS: public-repository guard text, file, link and isolated-Git regression tests")


def integration_test():
    with tempfile.TemporaryDirectory(prefix="public-repo-test-") as temporary:
        root = (Path(temporary) / "repo").resolve()
        root.mkdir()
        subprocess.run(["git", "init", "-q", str(root)], check=True)

        def git_add():
            subprocess.run(["git", "add", "."], cwd=root, check=True)

        def run_guard():
            return subprocess.run([sys.executable, str(Path(__file__).resolve())], cwd=root, capture_output=True, text=True)

        def snapshot():
            return {str(p.relative_to(root)): (str(p.readlink()) if p.is_symlink() else p.read_bytes())
                    for p in root.rglob("*") if ".git" not in p.relative_to(root).parts and (p.is_symlink() or p.is_file())}

        (root / ".gitignore").write_text("ignored.txt\n.env\n", encoding="utf-8")
        (root / "page name.md").write_text("# Present heading\n", encoding="utf-8")
        readme = root / "README.md"
        readme.write_text("[good](page%20name.md#present%2Dheading)\n", encoding="utf-8")
        git_add()
        before = snapshot()
        assert run_guard().returncode == 0
        assert snapshot() == before
        links = "[missing](absent.md)\n[escape](../outside.md)\n[heading](page%20name.md#missing)\n"
        assert link_findings(root, readme, links) == [(1, "broken-local-link"), (2, "broken-local-link"), (3, "broken-heading-link")]

        canary = "ghp_" + "a" * 36
        (root / "credential.txt").write_text(canary, encoding="utf-8")
        (root / "private.log").write_text("synthetic diagnostic", encoding="utf-8")
        (root / "binary.bin").write_bytes(b"\0" + canary.encode())
        (root / "invalid.bin").write_bytes(b"\xff\xfe")
        (root / "gone.txt").write_text("removed after staging", encoding="utf-8")
        outside = Path(temporary) / "outside.txt"
        outside.write_text(canary, encoding="utf-8")
        (root / "link.txt").symlink_to(outside)
        (root / "ignored.txt").write_text(canary, encoding="utf-8")
        (root / ".env").write_text(canary, encoding="utf-8")
        git_add()
        (root / "gone.txt").unlink()
        before = snapshot()
        result = run_guard()
        assert result.returncode == 1
        assert set(result.stdout.splitlines()) == {
            "credential.txt:1: github-token", "private.log:0: generated-or-sensitive-file",
            "link.txt:0: tracked-symlink-needs-review", "gone.txt:0: unreadable-tracked-file",
        }
        assert canary not in result.stdout + result.stderr
        assert snapshot() == before


def main():
    if sys.argv[1:] == ["--self-test"]:
        self_test()
        return 0
    if sys.argv[1:]:
        print("Usage: python3 scripts/check-public-repo.py [--self-test]", file=sys.stderr)
        return 2
    root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()).resolve()
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode("utf-8").split("\0")
    findings = []
    for name in filter(None, paths):
        path = root / name
        if GENERATED.search(name):
            findings.append((name, 0, "generated-or-sensitive-file"))
        if path.is_symlink():
            findings.append((name, 0, "tracked-symlink-needs-review"))
            continue
        try:
            data = path.read_bytes()
        except OSError:
            findings.append((name, 0, "unreadable-tracked-file"))
            continue
        if b"\0" in data:
            continue
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            continue
        findings.extend((name, line, rule) for line, rule in text_findings(text))
        if name in {"README.md", "REFERENCE.md", "docs/public-readiness.md"}:
            findings.extend((name, line, rule) for line, rule in link_findings(root, path, text))
    for name, line, rule in findings:
        print(f"{name}:{line}: {rule}")
    if findings:
        print(f"FAIL: {len(findings)} public-repository finding(s); values withheld", file=sys.stderr)
        return 1
    print("PASS: tracked text credential/path patterns, file hygiene and public entry links")
    print("Limited regression guard only; review binaries, other PII, history and artifacts separately.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
