#!/usr/bin/env python3
"""List likely secrets in a change for a reviewer. It reports where a secret-shaped value is; it
never prints the value and never says whether it is a real or acceptable secret (the reviewer
judges).

Rules (fact ids):
  private-key-block          a `-----BEGIN ... PRIVATE KEY-----` block
  github-token               a GitHub token (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_)
  azure-account-key          an Azure storage AccountKey= or a SharedAccessKey= in a connection string
  azure-sas-signature        an Azure SAS `sig=` signature
  aws-access-key-id          an AWS access key id (AKIA/ASIA/ABIA/ACCA + 16)
  aws-secret-access-key      an aws_secret_access_key assignment with a 40-character value
  slack-token                a Slack token (xoxb-, xoxp-, xoxa-, xoxr-, xoxs-) or webhook URL
  jwt                        a JSON Web Token (three base64url parts, header and payload `eyJ`)
  generic-secret-assignment  a password/secret/token/api key name assigned a literal value
  high-entropy-literal       a long random-looking literal in a config file
  sensitive-file             a .env file or a key/keystore file tracked in git

Every fact carries file:line, the rule id and a fingerprint: the first 4 characters of the value
(only when it is at least 20 characters long) and the first 12 hex digits of its sha256. The value
itself is never printed, written or logged.

What is scanned (one of):
  default        the lines a diff range adds: --base (default origin/main) ... --head (default
                 HEAD), from the merge base; contents come from git objects, never the work tree
  --all          every file tracked at --head (or, outside git, every file under --root)
  PATH ...       these files or directories in the working tree, every line
  --diff FILE    the lines a unified diff adds (for a review package without git)

Allowlist: `allow-path:` (path glob) and `allow-fingerprint:` lines in references/local.md (or
--allowlist FILE). Allowlisted facts are counted and listed separately, never dropped silently.

Optional external scanners (off by default): --external gitleaks / --external trufflehog run that
tool when it is on PATH; only its rule, file and line are kept. See references/hooks.md.

Output: a markdown summary (at most --max-lines lines), or --json. Exit 0 when the scan ran
(whatever it found), 2 on a usage error, an unreadable path or a git error.
"""
import argparse
import fnmatch
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile

SCHEMA = 1
SKILL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_ALLOWLIST = os.path.join(SKILL_DIR, "references", "local.md")
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
MAX_BYTES = 2_000_000          # larger files are listed under limits, not read
KEY_BLOCK_MAX_LINES = 200      # how far a private key block is followed to its END line
KEY_BODY_MIN = 40             # base64 characters a block needs to count as a key, not a marker string
PREFIX_MIN = 20                # values shorter than this get no prefix in the fingerprint

# Report order: the most specific (least likely to be noise) first. Ordering only.
RULES = ("private-key-block", "github-token", "azure-account-key", "azure-sas-signature",
         "aws-access-key-id", "aws-secret-access-key", "slack-token", "jwt", "sensitive-file",
         "generic-secret-assignment", "high-entropy-literal")

TOKEN_RULES = [
    ("github-token", re.compile(r"\b(gh[pousr]_[A-Za-z0-9]{36,255})\b")),
    ("github-token", re.compile(r"\b(github_pat_[A-Za-z0-9_]{60,255})\b")),
    ("azure-account-key", re.compile(r"(?i)\b(?:AccountKey|SharedAccessKey)\s*=\s*([A-Za-z0-9+/]{30,}={0,2})")),
    ("azure-sas-signature", re.compile(r"[?&;\s\"']sig=([A-Za-z0-9%+/]{30,}(?:%3D|=){0,2})")),
    ("aws-access-key-id", re.compile(r"\b((?:AKIA|ASIA|ABIA|ACCA)[A-Z0-9]{16})\b")),
    ("aws-secret-access-key", re.compile(
        r"(?i)aws_?secret_?access_?key[\"']?\s*[:=]\s*[\"']?([A-Za-z0-9/+]{40})(?![A-Za-z0-9/+])")),
    ("slack-token", re.compile(r"\b(xox[abprs]-[A-Za-z0-9-]{10,})")),
    ("slack-token", re.compile(r"(https://hooks\.slack\.com/services/T[A-Za-z0-9_]+/B[A-Za-z0-9_]+/[A-Za-z0-9_]+)")),
    ("jwt", re.compile(r"\b(eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})")),
]
KEY_BEGIN = re.compile(r"-----BEGIN ((?:[A-Z0-9]+ )*)PRIVATE KEY(?: BLOCK)?-----")
B64_LINE = re.compile(r"[A-Za-z0-9+/]{16,}={0,2}")
B64_INLINE = re.compile(r"[A-Za-z0-9+/]{40,}={0,2}")
KEY_END = re.compile(r"-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----")

# generic-secret-assignment: a key whose name ends in one of these words, then `=` or `:`.
SECRET_NAME = (r"(?:password|passwd|pwd|secret|client_?secret|secret_?key|api_?key|apikey|access_?key|"
               r"private_?key|auth_?token|access_?token|refresh_?token|bearer_?token|token|credentials?)")
ASSIGN_QUOTED = re.compile(
    r"(?i)(?<![A-Za-z0-9=:/+])([A-Za-z0-9_.:\-]{0,60}?" + SECRET_NAME + r")[\"']?\s*(?::=|=>|[:=])\s*"
    r"(?:@)?([\"'])([^\"'\r\n]{6,}?)\2")
ASSIGN_BARE = re.compile(
    r"(?i)(?<![A-Za-z0-9=:/+])([A-Za-z0-9_.:\-]{0,60}?" + SECRET_NAME + r")[\"']?\s*[:=]\s*([^\s\"';,&<>(){}\[\]]{6,})")
def shown_name(name):
    """The key's last `.` or `:` segment, with any long alphanumeric run masked (it could be a value)."""
    last = re.split(r"[.:]", name)[-1] or name
    return re.sub(r"[A-Za-z0-9]{16,}", "\u2026", last)[-48:]


# Connection-string style inside a string literal: `;Password=xyz;` or `"Password=xyz;`.
ASSIGN_CONNSTR = re.compile(r"(?i)(?:^|[;\"'\s])((?:password|pwd))=([^;\"'\s]{4,})")
REFERENCE = re.compile(r"^(?:\$|\{\{|#\{|%[A-Za-z_]|@Microsoft\.KeyVault|secretref:|keyvaultref:|env:|"
                       r"<[^>]*>$|\[[^\]]*\]$|\*+$)", re.I)
PLACEHOLDER = re.compile(r"(?i)^(?:x{3,}|changeme|change[-_]me|placeholder|redacted|dummy|example|sample|"
                         r"test|password|secret|null|none|true|false|undefined|todo)$|example|placeholder|"
                         r"changeme|your[-_]|replace[-_]?me|fake|^(?:test|dummy|sample|mock)[-_.]")
TEST_PATH = re.compile(r"(?i)(?:^|/)(?:tests?|specs?|__tests__|fixtures?|testdata|e2e|[^/]*\.tests?)(?:/|$)|"
                       r"(?:^|/)test[-_][^/]*$|[._-]tests?\.[a-z]+$")

CONFIG_EXT = (".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg", ".config", ".properties",
              ".env", ".tfvars", ".bicepparam", ".settings", ".plist", ".npmrc", ".pypirc")
SHELL_EXT = (".sh", ".bash", ".zsh")
CODE_EXT = (".cs", ".csx", ".fs", ".vb", ".razor", ".cshtml", ".py", ".js", ".mjs", ".cjs", ".ts",
            ".tsx", ".jsx", ".go", ".java", ".kt", ".rb", ".php", ".rs", ".swift", ".ps1", ".psm1",
            ".sh", ".bash", ".bicep", ".tf", ".sql")
LOCKFILES = ("package-lock.json", "packages.lock.json", "yarn.lock", "pnpm-lock.yaml", "poetry.lock",
             "Cargo.lock", "go.sum", "composer.lock", "Gemfile.lock", "Pipfile.lock", "uv.lock")
# high-entropy-literal: the whole value of a `key: value`, `key = value` or `"key": "value"` entry.
CONFIG_VALUE = re.compile(r"[:=]\s*[\"']?([A-Za-z0-9+/=_\-]{20,})[\"']?(?=\s*(?:[,;}\]#]|$))")
WORDY = re.compile(r"(?:[A-Za-z]+[0-9]*|[0-9]+)(?:[-_](?:[A-Za-z]+[0-9]*|[0-9]+))+")
PUBLIC_KEY_PREFIXES = ("MFkwEwYHKoZIzj0CAQ", "MHYwEAYHKoZIzj0CAQ", "MIGbMBAGByqGSM49AgE", "MIIBIjANBgkqhkiG9w0BAQEF",
                       "MIICIjANBgkqhkiG9w0BAQEF", "MCowBQYDK2Vw", "ssh-ed25519", "ssh-rsa")
CONFIG_JSON = re.compile(r"^(?:appsettings.*|local\.settings|.*\.?settings|secrets|.*\.parameters|config.*|.*\.config)\.json$")
DIGEST_CONTEXT = re.compile(r"(?i)(?:sha(?:1|256|384|512)[:\-]|@sha256:|integrity|contenthash|checksum|"
                            r"digest|etag|thumbprint|hash|commit|revision|\bsha\b)")
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")

SENSITIVE_NAMES = (".env", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".pgpass", ".netrc",
                   "credentials", ".git-credentials")
SENSITIVE_EXT = (".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".kdbx", ".tfstate",
                 ".publishsettings", ".snk")
EXAMPLE_SUFFIX = (".example", ".sample", ".template", ".dist", ".tmpl")


class UsageError(Exception):
    pass


# ------------------------------------------------------------------ fingerprints

def fingerprint(value, prefix=True):
    """A stable, redacted handle for a value: `abcd…<sha256[:12]>`, or `…<sha256[:12]>` when the value
    is shorter than PREFIX_MIN characters (a prefix would give away too much of a short value)."""
    h = hashlib.sha256(value.encode("utf-8", "surrogateescape")).hexdigest()[:12]
    return (value[:4] if prefix and len(value) >= PREFIX_MIN else "") + "…" + h


def fp_hash(fp):
    m = re.search(r"([0-9a-f]{12})\s*$", fp or "")
    return m.group(1) if m else None


def entropy(s):
    n = len(s)
    return -sum(c / n * math.log2(c / n) for c in (s.count(ch) for ch in set(s)))


# ------------------------------------------------------------------ allowlist

def load_allowlist(path):
    """-> {paths: [glob], fingerprints: {hash12: raw}} or None when the file is missing. Only the hashes
    are compared or echoed."""
    if not path or not os.path.isfile(path):
        return None
    out = dict(paths=[], fingerprints={})
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = re.match(r"^\s*[-*]?\s*allow-(path|fingerprint):\s*(\S+)", line)
            if not m:
                continue
            val = m.group(2).strip("`")
            if m.group(1) == "path":
                out["paths"].append(val)
            elif fp_hash(val):
                out["fingerprints"][fp_hash(val)] = val
    return out


def allowed(allow, path, fp):
    if not allow:
        return None
    for g in allow["paths"]:
        if fnmatch.fnmatchcase(path, g) or (g.endswith("/**") and path.startswith(g[:-2])):
            return f"path {g}"
    if fp_hash(fp) in allow["fingerprints"]:
        return "fingerprint"
    return None


# ------------------------------------------------------------------ scanning

def file_kind(path):
    base = os.path.basename(path).lower()
    if base in (n.lower() for n in LOCKFILES):
        return "lock"
    if base.startswith(".env") or CONFIG_JSON.match(base) or base.endswith(CONFIG_EXT) \
            or base == "dockerfile" or base.endswith(".dockerfile"):
        return "config"
    if base.endswith(SHELL_EXT):
        return "shell"
    if base.endswith(CODE_EXT):
        return "code"
    return "other"


def sensitive_name(path):
    base = os.path.basename(path).lower()
    if base.endswith(EXAMPLE_SUFFIX):
        return None
    if base == ".env" or base.startswith(".env."):
        return ".env file"
    if base in SENSITIVE_NAMES:
        return "credential file name"
    for ext in SENSITIVE_EXT:
        if base.endswith(ext):
            return f"{ext} file"
    return None


class Scan:
    def __init__(self, allow):
        self.allow = allow
        self.facts, self.allowlisted, self.limits = [], [], []

    def add(self, rule, path, line, value, detail, prefix=True):
        fp = fingerprint(value, prefix)
        known = PUBLIC_VALUES.get(fp_hash(fp))
        f = dict(rule=rule, file=path, line=line, fingerprint=fp, detail=detail + (f"; matches {known}" if known else ""))
        if TEST_PATH.search(path):
            f["test_path"] = True
        why = allowed(self.allow, path, fp)
        if why:
            f["allowed_by"] = why
            self.allowlisted.append(f)
        else:
            self.facts.append(f)

    def scan_file(self, path, lines, scope, status=None, data=None):
        """lines: [(lineno, text)] in order, or None when the content was not read; scope: the line
        numbers to report (the added lines), or None for every line."""
        why = sensitive_name(path)
        if why and status in ("A", "C", "tracked", "present"):
            what = {"A": "added in this range", "C": "copied in this range", "tracked": "tracked",
                    "present": "present"}[status]
            self.add("sensitive-file", path, 1, (data or path.encode()).decode("latin-1"), f"{why}, {what}",
                     prefix=False)
        if lines is None:
            return
        kind = file_kind(path)
        in_scope = (lambda n: True) if scope is None else (lambda n: n in scope)
        in_block = set()
        for i, (n, text) in enumerate(lines):
            m = KEY_BEGIN.search(text)
            if not m:
                continue
            body, j, prev = [], i + 1, n
            while j < len(lines) and lines[j][0] == prev + 1 and j - i <= KEY_BLOCK_MAX_LINES \
                    and not KEY_END.search(lines[j][1]):
                body.append(lines[j][1].strip())
                prev = lines[j][0]
                j += 1
            complete = j < len(lines) and lines[j][0] == prev + 1 and KEY_END.search(lines[j][1])
            clean = [re.sub(r"\\[nr]", "", ln).strip().strip("\"',+;`").strip() for ln in body]
            if not complete:                           # without an END line, the block is the base64 run
                run = 0
                while run < len(clean) and B64_LINE.fullmatch(clean[run]):
                    run += 1
                clean = clean[:run]
            block = range(n, (lines[j][0] if complete else n + len(clean)) + 1)
            in_block.update(block)
            b64 = "".join(x for x in clean if B64_LINE.fullmatch(x))
            inline = B64_INLINE.search(re.sub(r"\\[nr]", "", text[m.end():]))
            value = b64 if len(b64) >= KEY_BODY_MIN else (inline.group(0) if inline else "")
            if not value:
                continue                               # a marker string, not a key
            if any(in_scope(k) for k in block):
                kind_name = (m.group(1) or "").strip() or "unnamed"
                self.add("private-key-block", path, n, value,
                         f"{kind_name} private key" + ("" if complete else ", END line not seen"))
        for n, text in lines:
            if not in_scope(n) or n in in_block:
                continue
            spans = []
            for rule, rx in TOKEN_RULES:
                for t in rx.finditer(text):
                    s, e = t.span(1)
                    if any(a <= s < b for a, b in spans):
                        continue
                    spans.append((s, e))
                    self.add(rule, path, n, t.group(1), RULE_DETAIL[rule])
            self.assignments(path, n, text, kind, spans)
            if kind == "config":
                self.entropy(path, n, text, spans)

    def assignments(self, path, n, text, kind, spans):
        hits = []
        for m in ASSIGN_QUOTED.finditer(text):
            hits.append((m.group(1), m.group(3), m.start(3), m.end(3)))
        for m in ASSIGN_CONNSTR.finditer(text):
            hits.append((m.group(1), m.group(2), m.start(2), m.end(2)))
        if kind in ("config", "shell"):
            for m in ASSIGN_BARE.finditer(text):
                hits.append((m.group(1), m.group(2), m.start(2), m.end(2)))
        seen = set()
        for name, value, s, e in sorted(hits, key=lambda h: h[2]):
            value = value.strip()
            if (s, e) in seen or any(a <= s < b or s <= a < e for a, b in spans) or REFERENCE.match(value):
                continue
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*\(.*", value) or re.match(r"^(?:https?|file)://[^@]*$", value):
                continue                               # a call or a plain URL without credentials
            if kind == "code" and re.search(r"\s", value):
                continue                               # prose or a format string, not a credential
            seen.add((s, e))
            spans.append((s, e))
            hint = ", value looks like a placeholder" if PLACEHOLDER.search(value) else ""
            self.add("generic-secret-assignment", path, n, value, f"`{shown_name(name)}` is assigned a literal{hint}")

    def entropy(self, path, n, text, spans):
        for m in CONFIG_VALUE.finditer(text):
            tok, s, e = m.group(1), m.start(1), m.end(1)
            if any(a <= s < b or s <= a < e for a, b in spans) or GUID.match(tok) or tok[0] == "/" \
                    or WORDY.fullmatch(tok):
                continue                               # a path or a hyphenated name, not a key
            if DIGEST_CONTEXT.search(text[max(0, s - 40):s]) or "://" in text[max(0, s - 100):s]:
                continue
            is_hex = re.fullmatch(r"[0-9a-fA-F]+", tok) is not None
            bits = entropy(tok)
            if is_hex:
                if len(tok) < 32 or bits < 3.5:
                    continue
            elif not (re.search(r"[0-9]", tok) and re.search(r"[a-z]", tok) and re.search(r"[A-Z]", tok)) \
                    or bits < 4.2:
                continue
            spans.append((s, e))
            note = ""
            if tok.startswith(PUBLIC_KEY_PREFIXES):
                note = "; starts like a public key encoding"
            elif is_hex and len(tok) in (40, 64):
                note = f"; {len(tok)} hex digits is also the length of a git or sha256 id"
            self.add("high-entropy-literal", path, n, tok,
                     f"{len(tok)}-character {'hex' if is_hex else 'mixed'} value, entropy {bits:.1f} bits/char{note}")


# sha256 of credentials that are published on purpose, so a reviewer can tell them apart.
PUBLIC_VALUES = {
    "0011cc25eb43": "the publicly documented Azurite and storage-emulator development key",
}

RULE_DETAIL = {
    "github-token": "GitHub token prefix",
    "azure-account-key": "Azure AccountKey/SharedAccessKey value",
    "azure-sas-signature": "Azure SAS signature",
    "aws-access-key-id": "AWS access key id",
    "aws-secret-access-key": "AWS secret access key assignment",
    "slack-token": "Slack token or webhook",
    "jwt": "JSON Web Token",
}


def text_lines(data):
    """-> [(lineno, text)] or None for binary content."""
    if b"\0" in data[:8192]:
        return None
    return list(enumerate(data.decode("utf-8", "replace").splitlines(), 1))


# ------------------------------------------------------------------ git

def git(root, *args, data=None, text=True):
    env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", GIT_TERMINAL_PROMPT="0")
    try:
        r = subprocess.run(["git", "-C", root, "-c", "core.quotePath=false", *args], input=data,
                           capture_output=True, text=text, timeout=600, env=env)
    except (OSError, subprocess.SubprocessError) as e:
        raise UsageError(f"git {args[0]} could not run: {e}")
    return r


def git_ok(root, *args, **kw):
    r = git(root, *args, **kw)
    if r.returncode:
        err = r.stderr if isinstance(r.stderr, str) else r.stderr.decode("utf-8", "replace")
        raise UsageError(f"git {' '.join(args[:3])} failed: {err.strip().splitlines()[-1] if err.strip() else r.returncode}")
    return r.stdout


def is_git(root):
    r = git(root, "rev-parse", "--is-inside-work-tree")
    return r.returncode == 0 and r.stdout.strip() == "true"


def resolve(root, rev):
    if rev == EMPTY_TREE:
        return EMPTY_TREE
    out = git(root, "rev-parse", "--verify", "--quiet", rev + "^{commit}")
    if out.returncode:
        raise UsageError(f"not a commit in {root}: {rev} (fetch it, or pass --base/--head)")
    return out.stdout.strip()


def ls_tree(root, commit, paths=None):
    """-> {path: (blob, size)} for regular files (no symlinks or submodules)."""
    out = git_ok(root, "ls-tree", "-r", "-l", "-z", "--full-tree", commit, *(["--"] + paths if paths else []))
    res = {}
    for ent in out.split("\0"):
        if not ent:
            continue
        meta, path = ent.split("\t", 1)
        mode, typ, sha, size = meta.split()
        if typ == "blob" and mode in ("100644", "100755"):
            res[path] = (sha, int(size) if size.isdigit() else 0)
    return res


def cat_blobs(root, shas):
    """-> {sha: bytes} via one `git cat-file --batch`."""
    if not shas:
        return {}
    r = git(root, "cat-file", "--batch", data=("\n".join(shas) + "\n").encode(), text=False)
    if r.returncode:
        raise UsageError("git cat-file failed")
    out, i, res = r.stdout, 0, {}
    while i < len(out):
        nl = out.index(b"\n", i)
        head = out[i:nl].decode().split()
        if len(head) < 3 or head[1] == "missing":
            i = nl + 1
            continue
        size = int(head[2])
        res[head[0]] = out[nl + 1:nl + 1 + size]
        i = nl + 1 + size + 1
    return res


def git_blob_id(data):
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


# ------------------------------------------------------------------ unified diff

HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def unquote_path(p):
    p = p.strip()
    if p.startswith('"') and p.endswith('"'):
        p = p[1:-1].encode("latin-1", "backslashreplace").decode("unicode_escape").encode("latin-1").decode("utf-8", "replace")
    return p


def parse_diff(text):
    """-> [dict(path, old_path, status, binary, added={n: text}, context={n: text}, removed={n: text})]."""
    files, cur, old_n, new_n, in_hunk = [], None, 0, 0, False
    for raw in text.split("\n"):
        if raw.startswith("diff --git "):
            cur = dict(path=None, old_path=None, status="M", binary=False, added={}, context={}, removed={})
            files.append(cur)
            in_hunk = False
            continue
        if cur is None:
            continue
        if in_hunk:                                    # body lines; never read as headers
            if raw.startswith("@@"):
                m = HUNK.match(raw)
                if m:
                    old_n, new_n = int(m.group(1)), int(m.group(3))
            elif raw.startswith("+"):
                cur["added"][new_n] = raw[1:]
                new_n += 1
            elif raw.startswith("-"):
                cur["removed"][old_n] = raw[1:]
                old_n += 1
            elif raw.startswith(" "):
                cur["context"][new_n] = raw[1:]
                old_n += 1
                new_n += 1
            continue
        if raw.startswith("new file mode"):
            cur["status"] = "A"
        elif raw.startswith("deleted file mode"):
            cur["status"] = "D"
        elif raw.startswith("rename from "):
            cur["status"], cur["old_path"] = "R", unquote_path(raw[12:])
        elif raw.startswith("rename to "):
            cur["path"] = unquote_path(raw[10:])
        elif raw.startswith("copy to "):
            cur["status"], cur["path"] = "C", unquote_path(raw[8:])
        elif raw.startswith("Binary files ") or raw.startswith("GIT binary patch"):
            cur["binary"] = True
            m = re.match(r"^Binary files (?:a/)?(.*?) and (?:b/)?(.*) differ$", raw)
            if m and cur["path"] is None and m.group(2) != "/dev/null":
                cur["path"] = unquote_path(m.group(2))
        elif raw.startswith("--- "):
            p = unquote_path(raw[4:])
            if p != "/dev/null":
                cur["old_path"] = cur["old_path"] or (p[2:] if p.startswith("a/") else p)
            elif cur["status"] == "M":
                cur["status"] = "A"
        elif raw.startswith("+++ "):
            p = unquote_path(raw[4:])
            if p == "/dev/null":
                cur["status"] = "D"
            else:
                cur["path"] = p[2:] if p.startswith("b/") else p
        elif raw.startswith("@@"):
            m = HUNK.match(raw)
            if m:
                old_n, new_n = int(m.group(1)), int(m.group(3))
                in_hunk = True
    for f in files:
        if f["path"] is None:
            f["path"] = f["old_path"]
        if f["old_path"] is None and f["status"] != "A":
            f["old_path"] = f["path"]
    return [f for f in files if f["path"]]


# ------------------------------------------------------------------ sources

def scan_range(scan, root, base, head):
    head_c = resolve(root, head)
    base_c = resolve(root, base)
    mb = None
    if base_c != EMPTY_TREE:
        r = git(root, "merge-base", base_c, head_c)
        mb = r.stdout.strip() if r.returncode == 0 else None
    frm = mb or base_c
    diff = git_ok(root, "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-U0", "-M",
                  "--diff-filter=ACMR", frm, head_c)
    changed = [f for f in parse_diff(diff) if f["status"] != "D"]
    tree = ls_tree(root, head_c, [f["path"] for f in changed]) if changed else {}
    want = [tree[f["path"]][0] for f in changed if f["path"] in tree and tree[f["path"]][1] <= MAX_BYTES]
    blobs = cat_blobs(root, sorted(set(want)))
    files = []
    for f in changed:
        blob, size = tree.get(f["path"], (None, 0))
        files.append(dict(path=f["path"], status=f["status"], blob=blob))
        if blob is None:
            scan.limits.append(f"{f['path']}: not a regular file at head (symlink or submodule); not read")
            continue
        if size > MAX_BYTES:
            scan.limits.append(f"{f['path']}: larger than {MAX_BYTES} bytes; not read")
            scan.scan_file(f["path"], None, None, f["status"])
            continue
        data = blobs.get(blob, b"")
        lines = None if f["binary"] else text_lines(data)
        if lines is None and not sensitive_name(f["path"]):
            scan.limits.append(f"{f['path']}: binary; not read")
        scan.scan_file(f["path"], lines, set(f["added"]), f["status"], data)
    rev = dict(mode="range", base=base, base_commit=base_c, merge_base=mb, head=head, head_commit=head_c,
               diff_from=frm)
    return rev, files


def scan_all_git(scan, root, head):
    head_c = resolve(root, head)
    tree = ls_tree(root, head_c)
    small = {p: v for p, v in tree.items() if v[1] <= MAX_BYTES}
    for p, (sha, size) in tree.items():
        if size > MAX_BYTES:
            scan.limits.append(f"{p}: larger than {MAX_BYTES} bytes; not read")
    blobs = {}
    shas = sorted({v[0] for v in small.values()})
    for i in range(0, len(shas), 2000):
        blobs.update(cat_blobs(root, shas[i:i + 2000]))
    for p in sorted(tree):
        sha, size = tree[p]
        data = blobs.get(sha) if size <= MAX_BYTES else None
        lines = text_lines(data) if data is not None else None
        if data is not None and lines is None and not sensitive_name(p):
            scan.limits.append(f"{p}: binary; not read")
        scan.scan_file(p, lines, None, "tracked", data)
    return dict(mode="all", head=head, head_commit=head_c), \
        [dict(path=p, blob=v[0]) for p, v in sorted(tree.items())]


SKIP_DIRS = {".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", ".vs", ".idea"}


def walk(paths):
    out = []
    for p in paths:
        if os.path.isdir(p):
            for d, dirs, names in os.walk(p):
                dirs[:] = sorted(x for x in dirs if x not in SKIP_DIRS)
                out += [os.path.join(d, n) for n in sorted(names)]
        elif os.path.isfile(p):
            out.append(p)
        else:
            raise UsageError(f"no such file or directory: {p}")
    return list(dict.fromkeys(out))


def scan_paths(scan, root, paths, status):
    files = []
    for f in walk(paths):
        rel = os.path.relpath(f, root).replace(os.sep, "/")
        if os.path.islink(f):
            scan.limits.append(f"{rel}: symlink; not read")
            continue
        try:
            size = os.path.getsize(f)
            data = open(f, "rb").read() if size <= MAX_BYTES else None
        except OSError as e:
            scan.limits.append(f"{rel}: unreadable ({e.strerror})")
            continue
        if data is None:
            scan.limits.append(f"{rel}: larger than {MAX_BYTES} bytes; not read")
            scan.scan_file(rel, None, None, status)
            files.append(dict(path=rel, blob=None))
            continue
        files.append(dict(path=rel, blob=git_blob_id(data)))
        scan.scan_file(rel, text_lines(data), None, status, data)
    return files


def worktree_revision(root, rels):
    """-> {commit, changed, status_ok} for the work tree at root, or None outside git. `changed` lists
    scanned files (relative to root) that differ from the commit: modified, staged or untracked."""
    head = git(root, "rev-parse", "HEAD", "--show-toplevel")
    if head.returncode:
        return None
    commit, top = head.stdout.splitlines()[:2]
    top = os.path.realpath(top)
    st = git(root, "status", "--porcelain", "-z", "--untracked-files=all", "--", *rels) if rels else None
    ls = git(root, "ls-files", "-z", "--full-name", "--", *rels) if rels else None
    ok = (st is None or not st.returncode) and (ls is None or not ls.returncode)
    changed = set()
    if ok and ls is not None:
        tracked = {os.path.relpath(os.path.join(top, p), root).replace(os.sep, "/")
                   for p in ls.stdout.split("\0") if p}
        changed |= {r for r in rels if r not in tracked}
    if ok and st is not None:
        ents, i = st.stdout.split("\0"), 0
        while i < len(ents):
            e = ents[i]
            if len(e) > 3:
                changed.add(os.path.relpath(os.path.join(top, e[3:]), root).replace(os.sep, "/"))
                if e[0] in "RC":
                    i += 1
            i += 1
    return dict(commit=commit, changed=sorted(changed), status_ok=ok)


def scan_diff_file(scan, path):
    try:
        with open(path, "rb") as fh:
            raw = fh.read()
    except OSError as e:
        raise UsageError(f"cannot read --diff {path}: {e.strerror}")
    files = []
    for f in parse_diff(raw.decode("utf-8", "replace")):
        if f["status"] == "D":
            continue
        files.append(dict(path=f["path"], status=f["status"], blob=None))
        lines = sorted({**f["context"], **f["added"]}.items())
        if f["binary"]:
            scan.limits.append(f"{f['path']}: binary in the diff; not read")
            lines = None
        scan.scan_file(f["path"], lines, set(f["added"]), f["status"])
    return dict(mode="diff", diff=path, diff_sha256=hashlib.sha256(raw).hexdigest()), files


# ------------------------------------------------------------------ external scanners

def run_external(tool, root, rev):
    """Run an optional scanner; keep rule, file and line only (never its match or secret fields)."""
    exe = shutil.which(tool)
    if not exe:
        return dict(tool=tool, ran=False, reason="not on PATH")
    with tempfile.TemporaryDirectory() as tmp:
        rep = os.path.join(tmp, "report.json")
        rng = rev.get("mode") == "range"
        if tool == "gitleaks":
            cmd = [exe, "detect", "--source", root, "--redact", "--no-banner", "--exit-code", "0",
                   "--report-format", "json", "--report-path", rep]
            cmd += ["--log-opts", f"{rev['diff_from']}..{rev['head_commit']}"] if rng else ["--no-git"]
        else:
            cmd = [exe, "git", f"file://{root}", "--since-commit", rev["diff_from"], "--json", "--no-update",
                   "--no-verification"] if rng else [exe, "filesystem", root, "--json", "--no-update", "--no-verification"]
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
        except (OSError, subprocess.TimeoutExpired) as e:
            return dict(tool=tool, ran=False, reason=type(e).__name__)
        raw = open(rep, encoding="utf-8", errors="replace").read() if tool == "gitleaks" and os.path.exists(rep) \
            else r.stdout
    return dict(tool=tool, ran=True, exit=r.returncode, findings=parse_external(tool, raw))


def parse_external(tool, raw):
    out = []
    if tool == "gitleaks":
        try:
            items = json.loads(raw or "[]")
        except ValueError:
            return None
        for it in items if isinstance(items, list) else []:
            if isinstance(it, dict):
                out.append(dict(rule=str(it.get("RuleID")), file=it.get("File"), line=it.get("StartLine"),
                                commit=it.get("Commit") or None))
    else:
        for line in (raw or "").splitlines():
            try:
                it = json.loads(line)
            except ValueError:
                continue
            if not isinstance(it, dict):
                continue
            meta = (it.get("SourceMetadata") or {}).get("Data") or {}
            src = next(iter(meta.values()), {}) if isinstance(meta, dict) and meta else {}
            out.append(dict(rule=str(it.get("DetectorName")), file=src.get("file"), line=src.get("line")))
    return out


# ------------------------------------------------------------------ output

def report(scan, rev, files, allow_src, external):
    counts = {r: 0 for r in RULES}
    for f in scan.facts:
        counts[f["rule"]] += 1
    if rev.get("mode") == "all":
        hit = {f["file"] for f in scan.facts + scan.allowlisted}
        listed = [f for f in files if f["path"] in hit]
    else:
        listed = files
    key = lambda f: (RULES.index(f["rule"]), f["file"], f["line"])
    return dict(tool="secrets-scan", schema=SCHEMA, revision=rev, files_scanned=len(files), files=listed,
                allowlist=allow_src, counts=counts, facts=sorted(scan.facts, key=key),
                allowlisted=sorted(scan.allowlisted, key=key), limits=scan.limits, external=external)


def markdown(rep, max_lines):
    rev = rep["revision"]
    nf = len(rep["facts"])
    out = [f"# secrets-scan facts ({rep['files_scanned']} files, {nf} fact{'' if nf == 1 else 's'}"
           + (f", {len(rep['allowlisted'])} allowlisted" if rep["allowlisted"] else "") + ")", "",
           "Facts only, values never shown; the reviewer judges what they mean for this change.", ""]
    if rev["mode"] == "range":
        out.append(f"Revision: lines added from `{rev['diff_from']}`"
                   + (" (merge base)" if rev.get("merge_base") else "") + f" to `{rev['head_commit']}`"
                   + "; file blob ids are in --json")
    elif rev["mode"] == "all":
        out.append(f"Revision: every file tracked at `{rev['head_commit']}`" if rev.get("head_commit")
                   else "Revision: every file under the root (not a git checkout)")
    elif rev["mode"] == "diff":
        out.append(f"Revision: lines added by diff `{rev['diff']}` (sha256 `{rev['diff_sha256'][:12]}`)")
    else:
        wt = rev.get("worktree")
        if wt is None:
            out.append("Revision: not a git checkout; file blob ids are in --json")
        elif wt["changed"]:
            out.append(f"Revision: `{wt['commit']}`, but these scanned files differ from it: "
                       + ", ".join(f"`{c}`" for c in wt["changed"][:5])
                       + (f" and {len(wt['changed']) - 5} more" if len(wt["changed"]) > 5 else ""))
        else:
            out.append(f"Revision: `{wt['commit']}`" + (", scanned files unchanged from it" if wt["status_ok"]
                       else ", working tree state unknown") + "; file blob ids are in --json")
    a = rep["allowlist"]
    out.append(f"Allowlist: {a['source']} ({len(a['paths'])} paths, {len(a['fingerprints'])} fingerprints)"
               if a else "Allowlist: none (no references/local.md)")
    out += ["", "| rule | count |", "|---|---|"]
    out += [f"| {r} | {n} |" for r, n in rep["counts"].items() if n] or ["| (none) | 0 |"]
    tail = []
    if rep["allowlisted"]:
        tail += ["", f"Allowlisted: {len(rep['allowlisted'])} (in --json, with the entry that matched)"]
    if rep["limits"]:
        shown = rep["limits"][:8]
        tail += ["", "Limits:"] + [f"- {l}" for l in shown]
        if len(rep["limits"]) > len(shown):
            tail.append(f"- ... {len(rep['limits']) - len(shown)} more limits in --json")
    for ext in rep["external"]:
        tail.append(f"External {ext['tool']}: " + (f"{len(ext['findings'])} findings, exit {ext['exit']}"
                    if ext.get("findings") is not None else ext.get("reason", "output not readable")))
    ordered = sorted(rep["facts"], key=lambda f: bool(f.get("test_path")))   # stable: test paths listed last
    lines = [f"- {f['rule']} `{f['file']}:{f['line']}` `{f['fingerprint']}`: {f['detail']}"
             + (" (test path)" if f.get("test_path") else "") for f in ordered]
    budget = max_lines - len(out) - len(tail) - 2
    if lines:
        if len(lines) > budget:
            keep = max(budget - 1, 0)
            lines = lines[:keep] + [f"- ... {len(rep['facts']) - keep} more in --json"]
        out += ["", "Facts:"] + lines
    out += tail
    if len(out) > max_lines:
        out = out[:max_lines - 1] + ["... truncated; the full report is in --json"]
    return "\n".join(out) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Secret-shaped values in a change, as redacted facts for a reviewer (no verdicts, no values).",
        epilog="Default: the lines added between --base and --head. Prints a markdown summary; --json prints "
               "the full report. Exit 0 when the scan ran, 2 on a usage error, an unreadable path or a git error.")
    ap.add_argument("paths", nargs="*", help="scan these files or directories in the working tree instead of a range")
    ap.add_argument("--root", default=".", help="repository root; paths are reported relative to it")
    ap.add_argument("--base", default="origin/main", help="range start (default origin/main; the merge base with --head is used)")
    ap.add_argument("--head", default="HEAD", help="range end, or the tree for --all (default HEAD)")
    ap.add_argument("--all", action="store_true", help="scan every file tracked at --head (outside git: every file under --root)")
    ap.add_argument("--diff", help="scan the lines this unified diff file adds (no git needed)")
    ap.add_argument("--allowlist", help="allowlist file (default references/local.md in the skill)")
    ap.add_argument("--external", action="append", choices=("gitleaks", "trufflehog"), default=[],
                    help="also run this scanner when it is on PATH (off by default; values are never kept)")
    ap.add_argument("--json", action="store_true", help="print the full JSON report instead of markdown")
    ap.add_argument("--out", help="also write the markdown summary to this file (review evidence)")
    ap.add_argument("--json-out", help="also write the JSON report to this file (review evidence)")
    ap.add_argument("--max-lines", type=int, default=60, help="markdown summary line cap (default 60)")
    a = ap.parse_args(argv)
    root = os.path.realpath(a.root)
    if not os.path.isdir(root):
        print(f"secrets-scan: --root is not a directory: {a.root}", file=sys.stderr)
        return 2
    if sum(map(bool, (a.paths, a.all, a.diff))) > 1:
        print("secrets-scan: pass at most one of PATH..., --all and --diff", file=sys.stderr)
        return 2
    if a.allowlist and not os.path.isfile(a.allowlist):
        print(f"secrets-scan: allowlist file not found: {a.allowlist}", file=sys.stderr)
        return 2
    allow_path = a.allowlist or DEFAULT_ALLOWLIST
    allow = load_allowlist(allow_path)
    scan = Scan(allow)
    try:
        if a.diff:
            rev, files = scan_diff_file(scan, a.diff)
        elif a.paths:
            paths = [p if os.path.isabs(p) else os.path.join(root, p) for p in a.paths]
            files = scan_paths(scan, root, paths, "present")
            rev = dict(mode="paths", worktree=worktree_revision(root, [f["path"] for f in files]))
        elif a.all:
            if is_git(root):
                rev, files = scan_all_git(scan, root, a.head)
            else:
                files = scan_paths(scan, root, [root], "present")
                rev = dict(mode="all", head=None, head_commit=None)
        else:
            if not is_git(root):
                raise UsageError(f"{a.root} is not a git checkout; pass --all, --diff FILE or paths")
            rev, files = scan_range(scan, root, a.base, a.head)
    except UsageError as e:
        print(f"secrets-scan: {e}", file=sys.stderr)
        return 2
    external = [run_external(t, root, rev) for t in a.external]
    shown = allow_path
    if os.path.abspath(allow_path).startswith(SKILL_DIR + os.sep):
        shown = os.path.relpath(os.path.abspath(allow_path), SKILL_DIR).replace(os.sep, "/")
    allow_src = dict(source=shown, paths=allow["paths"], fingerprints=sorted(allow["fingerprints"])) \
        if allow else None
    rep = report(scan, rev, files, allow_src, external)
    md = markdown(rep, max(a.max_lines, 12))
    for path in (a.out, a.json_out):   # the evidence directory may not exist yet
        if path and os.path.dirname(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as fh:
            fh.write(md)
    if a.json_out:
        with open(a.json_out, "w", encoding="utf-8") as fh:
            fh.write(json.dumps(rep, indent=2, ensure_ascii=False) + "\n")
    sys.stdout.write(json.dumps(rep, indent=2, ensure_ascii=False) + "\n" if a.json else md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
