#!/usr/bin/env python3
"""List what a change does to authorization in an ASP.NET Core code base, as facts for a reviewer.
It reports which endpoints were added, changed or removed and what authorization each one visibly
has; it never says whether that is right (the reviewer judges), and it never guesses: what it
cannot see is reported as `unknown` with the reason.

Facts (ids):
  endpoint-added / endpoint-changed / endpoint-removed
      a minimal-API endpoint (Map{Get,Post,Put,Delete,Patch,Methods,Fallback,...}), a controller
      action, or a Razor page or component with @page; with its method, route and coverage
  auth-removed           a removed line that required authorization (RequireAuthorization,
                         [Authorize], AuthorizeFilter, AuthorizeView/RouteView, UseAuthorization)
  anonymous-added        an added line that allows anonymous access (AllowAnonymous, AllowAnonymousTo*)
  policy-changed         an added or removed line that defines or edits a policy or a requirement
  cors-changed           an added or removed CORS line (code, or a Cors key in appsettings)
  authn-scheme-changed   an added or removed authentication scheme, default scheme or token
                         validation line (code, or an authentication key in appsettings)
  context (head)         fallback policy, global AuthorizeFilter, authorization on MapControllers,
                         MapRazorPages or MapRazorComponents, AuthorizeRouteView, page conventions

Coverage of an endpoint, from what is visible at that revision:
  anonymous(level)   AllowAnonymous at some level (it wins over any authorization metadata)
  authorized(level)  RequireAuthorization / [Authorize] / a local auth convention at a level:
                     endpoint, handler, group, controller, base class, page, imports, mount, global filter
  fallback           no metadata, and the tree sets a fallback policy
  none-visible       no authorization metadata found
  unknown(reason)    something that decides it is not visible here (a group passed in from a
                     caller that is not found, a base class or handler in another file, ...)

What is compared (one of):
  default        the change between --base (default origin/main; the merge base is used) and --head
                 (default HEAD), from git objects; other files of the base and head trees are read to
                 follow callers, base classes, _Imports.razor and the global context
  --all          every endpoint at --head, as if added (an inventory)
  --diff FILE    a unified diff; the head is the tree under --root and the base is rebuilt from it

Local conventions: `auth-convention:`, `anonymous-convention:` and `handler-check:` lines in
references/local.md (or --local FILE). Output: markdown (at most --max-lines lines) or --json.
Exit 0 when the facts were computed (whatever they are), 2 on a usage error or a git error.
"""
import argparse
import bisect
import hashlib
import json
import os
import re
import subprocess
import sys

SCHEMA = 1
SKILL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_LOCAL = os.path.join(SKILL_DIR, "references", "local.md")
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
MAX_BYTES = 2_000_000
CALLER_DEPTH = 3
MAX_SITES = 40
SOURCE_EXT = (".cs", ".razor", ".cshtml")
APPSETTINGS = re.compile(r"(?:^|/)appsettings[^/]*\.json$", re.I)
TEST_PATH = re.compile(r"(?i)(?:^|/)(?:tests?|specs?|__tests__|fixtures?|testdata|e2e|[^/]*\.tests?)(?:/|$)|"
                       r"(?:^|/)(?:test_[^/]*|[^/]*_test\.[a-z]+|[^/]*\.test\.[a-z]+)$")
SKIP_DIRS = {".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", ".vs", ".idea"}

FACT_IDS = ("auth-removed", "anonymous-added", "endpoint-added", "endpoint-changed", "endpoint-removed",
            "policy-changed", "cors-changed", "authn-scheme-changed")

ENDPOINT_CALLS = {
    "MapGet": "GET", "MapPost": "POST", "MapPut": "PUT", "MapDelete": "DELETE", "MapPatch": "PATCH",
    "MapMethods": None, "MapFallback": "FALLBACK", "MapHub": "HUB", "MapHealthChecks": "HEALTH",
    "MapIdentityApi": "IDENTITY-API", "MapGrpcService": "GRPC", "MapControllers": "MOUNT",
    "MapControllerRoute": "MOUNT", "MapDefaultControllerRoute": "MOUNT", "MapRazorPages": "MOUNT",
    "MapRazorComponents": "MOUNT", "MapBlazorHub": "MOUNT", "MapFallbackToFile": "FALLBACK",
    "MapFallbackToPage": "FALLBACK", "MapFallbackToController": "FALLBACK",
}
HANDLER_INDEX = {"MapGet": 1, "MapPost": 1, "MapPut": 1, "MapDelete": 1, "MapPatch": 1, "MapMethods": 2}
MAP_RE = re.compile(r"\.\s*(" + "|".join(sorted(list(ENDPOINT_CALLS) + ["MapGroup"], key=len, reverse=True))
                    + r")\b\s*(?:<[^<>(){};]*>)?\s*\(")
BASE_TYPES = {"Controller", "ControllerBase", "ComponentBase", "LayoutComponentBase", "OwningComponentBase",
              "PageModel", "Hub", "object", "Object"}
HTTP_ATTRS = {"HttpGet": "GET", "HttpPost": "POST", "HttpPut": "PUT", "HttpDelete": "DELETE",
              "HttpPatch": "PATCH", "HttpHead": "HEAD", "HttpOptions": "OPTIONS", "AcceptVerbs": None, "Route": None}
HANDLER_CHECKS = ("AuthorizeAsync", "IsAuthenticated", "IsInRole", "HasClaim")

MODS = r"(?:public|private|internal|protected|static|async|override|virtual|sealed|partial|extern|new|unsafe|abstract|readonly)"
METHOD_RE = re.compile(r"(?m)^[ \t]*(?:\[[^\n]*?\][ \t]*)*((?:" + MODS + r"\s+)+)(?:[^\n;{}=()]*?\s+)?(\w+)[ \t]*(?:<[^\n<>(){}]*>)?[ \t]*\(")
CLASS_RE = re.compile(r"(?m)^[ \t]*((?:(?:public|internal|private|protected|static|sealed|abstract|partial|file|unsafe|new)\s+)*)"
                      r"(?:class|record(?:\s+class)?)\s+(\w+)")
BINDING_RE = re.compile(r"\b(?:var|RouteGroupBuilder|IEndpointRouteBuilder|IEndpointConventionBuilder|WebApplication|"
                        r"IApplicationBuilder|[A-Z]\w*Builder)\s+(\w+)\s*=(?!=)\s*")
CHAR_LIT = re.compile(r"'(?:\\.|[^\\'\n]){1,8}'")

LINE_TOKENS = {
    "auth-removed": [
        ("RequireAuthorization", r"\bRequireAuthorization\s*\("),
        ("[Authorize]", r"[\[,]\s*(?:[\w.]*\.)?Authorize(?:Attribute)?\s*[\](,]"),
        ("AuthorizeFilter", r"\bAuthorizeFilter\b"),
        ("AuthorizeRouteView", r"\bAuthorizeRouteView\b"),
        ("AuthorizeView", r"<\s*AuthorizeView\b"),
        ("page convention", r"\b(?:AuthorizeFolder|AuthorizePage|AuthorizeAreaFolder|AuthorizeAreaPage)\s*\("),
        ("UseAuthorization", r"\bUseAuthorization\s*\("),
    ],
    "anonymous-added": [
        ("AllowAnonymous", r"\bAllowAnonymous(?:Attribute)?\b(?!To)"),
        ("AllowAnonymousTo", r"\bAllowAnonymousTo(?:Area)?(?:Folder|Page)\s*\("),
    ],
    "policy-changed": [(t, r"\b" + t + r"\b") for t in (
        "AddPolicy", "AddAuthorizationBuilder", "DefaultPolicy", "FallbackPolicy", "SetDefaultPolicy",
        "SetFallbackPolicy", "RequireRole", "RequireClaim", "RequireAssertion", "RequireAuthenticatedUser",
        "RequireUserName", "AddRequirements", "IAuthorizationHandler", "IAuthorizationRequirement",
        "IAuthorizationPolicyProvider", "InvokeHandlersAfterFailure")]
        + [("AddAuthorization", r"\bAddAuthorization\s*\("), ("AuthorizationHandler", r"\bAuthorizationHandler\s*<")],
    "cors-changed": [(t, r"\b" + t + r"\b") for t in (
        "AddCors", "UseCors", "RequireCors", "EnableCors", "DisableCors", "WithOrigins", "AllowAnyOrigin",
        "SetIsOriginAllowed", "SetIsOriginAllowedToAllowWildcardSubdomains", "AllowCredentials",
        "AllowAnyHeader", "AllowAnyMethod", "WithExposedHeaders", "AddDefaultPolicy")]
        + [("Access-Control-Allow", r"Access-Control-Allow-")],
    "authn-scheme-changed": [(t, r"\b" + t + r"\b") for t in (
        "AddAuthentication", "UseAuthentication", "AddJwtBearer", "AddCookie", "AddOpenIdConnect",
        "AddMicrosoftAccount", "AddGoogle", "AddNegotiate", "AddCertificate", "AddPolicyScheme", "AddBearerToken",
        "AddIdentityCookies", "AddIdentityApiEndpoints", "DefaultScheme", "DefaultChallengeScheme",
        "DefaultAuthenticateScheme", "DefaultForbidScheme", "DefaultSignInScheme", "AuthenticationSchemes",
        "TokenValidationParameters", "ValidateIssuer", "ValidateAudience", "ValidateLifetime",
        "ValidateIssuerSigningKey", "ValidIssuer", "ValidIssuers", "ValidAudience", "ValidAudiences",
        "RequireHttpsMetadata", "RequireSignedTokens", "RequireExpirationTime", "ForwardDefaultSelector")]
        + [("AddMicrosoftIdentityWeb", r"\bAddMicrosoftIdentityWeb\w*"), ("AddScheme", r"\bAddScheme\s*<")],
}
CONFIG_TOKENS = {
    "cors-changed": [("Cors key", r"\"(?:Cors|AllowedOrigins|CorsOrigins|Origins)\"\s*:")],
    "authn-scheme-changed": [("authentication key",
                              r"\"(?:Authentication|AzureAd|AzureAdB2C|Jwt|JwtBearer|Authority|Audience|ValidIssuer|"
                              r"ValidAudience|Instance|Schemes)\"\s*:")],
}
CONTEXT_WORDS = ("FallbackPolicy", "AuthorizeFilter", "AllowAnonymousFilter", "MapControllers", "MapControllerRoute",
                 "MapDefaultControllerRoute", "MapRazorPages", "MapRazorComponents", "MapBlazorHub",
                 "AuthorizeFolder", "AuthorizePage", "AuthorizeAreaFolder", "AuthorizeAreaPage", "AuthorizeRouteView")


class UsageError(Exception):
    pass


# ------------------------------------------------------------------ git and trees

def git(root, *args, data=None):
    env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", GIT_TERMINAL_PROMPT="0")
    try:
        return subprocess.run(["git", "-C", root, "-c", "core.quotePath=false", *args], input=data,
                              capture_output=True, timeout=600, env=env)
    except (OSError, subprocess.SubprocessError) as e:
        raise UsageError(f"git {args[0]} could not run: {e}")


def git_ok(root, *args, **kw):
    r = git(root, *args, **kw)
    if r.returncode:
        err = r.stderr.decode("utf-8", "replace").strip()
        raise UsageError(f"git {' '.join(args[:3])} failed: {err.splitlines()[-1] if err else r.returncode}")
    return r.stdout


def is_git(root):
    r = git(root, "rev-parse", "--is-inside-work-tree")
    return r.returncode == 0 and r.stdout.decode().strip() == "true"


def resolve(root, rev):
    r = git(root, "rev-parse", "--verify", "--quiet", rev + "^{commit}")
    if r.returncode:
        raise UsageError(f"not a commit in {root}: {rev} (fetch it, or pass --base/--head)")
    return r.stdout.decode().strip()


def decode(data):
    if data is None or b"\0" in data[:8192]:
        return None
    return data.decode("utf-8", "replace")


class GitTree:
    def __init__(self, root, commit):
        self.root, self.commit, self._ls, self._cache = root, commit, None, {}

    def files(self):
        if self._ls is None:
            self._ls = {}
            if self.commit != EMPTY_TREE:
                out = git_ok(self.root, "ls-tree", "-r", "-l", "-z", "--full-tree", self.commit).decode("utf-8", "replace")
                for ent in out.split("\0"):
                    if ent:
                        meta, path = ent.split("\t", 1)
                        mode, typ, sha, size = meta.split()
                        if typ == "blob" and mode in ("100644", "100755"):
                            self._ls[path] = (sha, int(size) if size.isdigit() else 0)
        return self._ls

    def blob(self, path):
        return self.files().get(path, (None, 0))[0]

    def read_many(self, paths):
        want = [p for p in paths if p not in self._cache]
        shas = {}
        for p in want:
            sha, size = self.files().get(p, (None, 0))
            if sha is None or size > MAX_BYTES:
                self._cache[p] = None
            else:
                shas.setdefault(sha, []).append(p)
        if shas:
            r = git(self.root, "cat-file", "--batch", data=("\n".join(shas) + "\n").encode())
            out, i = r.stdout, 0
            while i < len(out):
                nl = out.index(b"\n", i)
                head = out[i:nl].decode().split()
                if len(head) < 3 or head[1] == "missing":
                    i = nl + 1
                    continue
                size = int(head[2])
                for p in shas.get(head[0], []):
                    self._cache[p] = decode(out[nl + 1:nl + 1 + size])
                i = nl + 1 + size + 1
        return {p: self._cache.get(p) for p in paths}

    def read(self, path):
        return self.read_many([path])[path]

    def grep(self, words, exts):
        """-> sorted paths whose content holds any of the words (fixed strings)."""
        if self.commit == EMPTY_TREE:
            return []
        args = ["grep", "-l", "-I", "-F"]
        for w in words:
            args += ["-e", w]
        args += [self.commit, "--"] + ["*" + e for e in exts]
        r = git(self.root, *args)
        pre = self.commit + ":"
        return sorted(l[len(pre):] if l.startswith(pre) else l
                      for l in r.stdout.decode("utf-8", "replace").splitlines() if l)


class FsTree:
    def __init__(self, root):
        self.root, self._files, self._cache = root, None, {}

    def files(self):
        if self._files is None:
            self._files = {}
            for d, dirs, names in os.walk(self.root):
                dirs[:] = sorted(x for x in dirs if x not in SKIP_DIRS)
                for n in sorted(names):
                    p = os.path.join(d, n)
                    if os.path.isfile(p) and not os.path.islink(p):
                        self._files[os.path.relpath(p, self.root).replace(os.sep, "/")] = (None, os.path.getsize(p))
        return self._files

    def blob(self, path):
        data = self._raw(path)
        return None if data is None else hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()

    def _raw(self, path):
        p = os.path.join(self.root, path)
        if not os.path.isfile(p) or os.path.islink(p) or os.path.getsize(p) > MAX_BYTES:
            return None
        with open(p, "rb") as fh:
            return fh.read()

    def read(self, path):
        if path not in self._cache:
            self._cache[path] = decode(self._raw(path))
        return self._cache[path]

    def read_many(self, paths):
        return {p: self.read(p) for p in paths}

    def grep(self, words, exts):
        out = []
        for p in self.files():
            if p.endswith(exts):
                t = self.read(p)
                if t and any(w in t for w in words):
                    out.append(p)
        return out


# ------------------------------------------------------------------ unified diff

HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def unquote_path(p):
    p = p.strip()
    if p.startswith('"') and p.endswith('"'):
        p = p[1:-1].encode("latin-1", "backslashreplace").decode("unicode_escape").encode("latin-1").decode("utf-8", "replace")
    return p


def header_path(raw):
    """The path of a ---/+++ line; a plain `diff -u` appends a tab and a timestamp."""
    p = raw[4:].rstrip("\r")
    if not p.startswith('"') and "\t" in p:
        p = p.split("\t", 1)[0]
    return unquote_path(p)


def parse_diff(text):
    """-> [dict(path, old_path, status, binary, added={n: text}, context={n: text}, removed={n: text})].
    Reads git diffs and plain unified diffs (`diff -u`, starting at `--- `). A hunk ends when its line
    counts are used up; header lines are read only outside a hunk."""
    files, cur, old_n, new_n = [], None, 0, 0
    old_left = new_left = 0

    def start():
        f = dict(path=None, old_path=None, status="M", binary=False, added={}, context={}, removed={}, hunks=0)
        files.append(f)
        return f
    for raw in text.split("\n"):
        if cur is not None and (old_left > 0 or new_left > 0):
            if raw.startswith("\\"):
                continue
            if raw.startswith("+"):
                cur["added"][new_n] = raw[1:]
                new_n, new_left = new_n + 1, new_left - 1
                continue
            if raw.startswith("-"):
                cur["removed"][old_n] = raw[1:]
                old_n, old_left = old_n + 1, old_left - 1
                continue
            if raw.startswith(" ") or raw == "":
                cur["context"][new_n] = raw[1:]
                old_n, new_n, old_left, new_left = old_n + 1, new_n + 1, old_left - 1, new_left - 1
                continue
            old_left = new_left = 0
        if raw.startswith("\\"):
            continue
        if raw.startswith("diff --git "):
            cur = start()
            continue
        if raw.startswith("--- ") and (cur is None or cur["hunks"] or cur["old_path"] is not None and not cur["status"] == "R"):
            cur = start()
        if cur is None:
            continue
        if raw.startswith("@@"):
            m = HUNK.match(raw)
            if m:
                old_n, new_n = int(m.group(1)), int(m.group(3))
                old_left = int(m.group(2)) if m.group(2) is not None else 1
                new_left = int(m.group(4)) if m.group(4) is not None else 1
                cur["hunks"] += 1
            continue
        if cur["hunks"]:
            continue
        if raw.startswith("new file mode"):
            cur["status"] = "A"
        elif raw.startswith("deleted file mode"):
            cur["status"] = "D"
        elif raw.startswith("rename from "):
            cur["status"], cur["old_path"] = "R", unquote_path(raw[12:].rstrip("\r"))
        elif raw.startswith("rename to "):
            cur["path"] = unquote_path(raw[10:].rstrip("\r"))
        elif raw.startswith("copy to "):
            cur["status"], cur["path"] = "C", unquote_path(raw[8:].rstrip("\r"))
        elif raw.startswith("Binary files ") or raw.startswith("GIT binary patch"):
            cur["binary"] = True
        elif raw.startswith("--- "):
            p = header_path(raw)
            if p != "/dev/null":
                cur["old_path"] = cur["old_path"] or (p[2:] if p.startswith("a/") else p)
            elif cur["status"] == "M":
                cur["status"] = "A"
        elif raw.startswith("+++ "):
            p = header_path(raw)
            if p == "/dev/null":
                cur["status"] = "D"
            else:
                cur["path"] = p[2:] if p.startswith("b/") else p
    for f in files:
        if f["path"] is None:
            f["path"] = f["old_path"]
        if f["old_path"] is None and f["status"] != "A":
            f["old_path"] = f["path"]
    return [f for f in files if f["path"]]


# ------------------------------------------------------------------ C# source model

def clean(s):
    """-> (code, literals): comments and string/char contents blanked to spaces (newlines and offsets
    kept); literals maps a string's opening-quote offset to (end, value)."""
    out, lits, i, n = list(s), {}, 0, len(s)

    def blank(a, b):
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "
    while i < n:
        c = s[i]
        if c == "/" and s.startswith("//", i):
            j = s.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif c == "/" and s.startswith("/*", i):
            j = s.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
        elif c == "'":
            m = CHAR_LIT.match(s, i)
            if m:
                blank(i + 1, m.end() - 1)
                i = m.end()
            else:
                i += 1
        elif c == '"':
            k = i
            while k > 0 and s[k - 1] in "@$":
                k -= 1
            verbatim = "@" in s[k:i]
            q = 0
            while i + q < n and s[i + q] == '"':
                q += 1
            if q >= 3:
                end = s.find('"' * q, i + q)
                end = n if end < 0 else end
                lits[i] = (end + q, s[i + q:end])
                blank(i + q, end)
                i = end + q
                continue
            j, buf = i + 1, []
            while j < n:
                ch = s[j]
                if verbatim:
                    if ch == '"':
                        if j + 1 < n and s[j + 1] == '"':
                            buf.append('"')
                            j += 2
                            continue
                        break
                else:
                    if ch == "\\" and j + 1 < n:
                        buf.append(s[j:j + 2])
                        j += 2
                        continue
                    if ch in '"\n':
                        break
                buf.append(ch)
                j += 1
            lits[i] = (j + 1, "".join(buf))
            blank(i + 1, j)
            i = j + 1
        else:
            i += 1
    return "".join(out), lits


def clean_markup(s):
    """Razor `@* *@` and HTML `<!-- -->` comments blanked to spaces (newlines and offsets kept)."""
    def blank(m):
        return re.sub(r"[^\n]", " ", m.group(0))
    s = re.sub(r"@\*.*?(?:\*@|\Z)", blank, s, flags=re.S)
    s = re.sub(r"<!--.*?(?:-->|\Z)", blank, s, flags=re.S)
    for ob in code_blocks(s):
        body, _ = clean(s[ob:])
        end = match_fwd(body, 0)
        end = len(body) if end < 0 else end + 1
        s = s[:ob] + body[:end] + s[ob + end:]
    return s


def code_blocks(s):
    """-> offsets of the `{` of each `@code { }` / `@functions { }` block in Razor markup."""
    return [m.end() - 1 for m in re.finditer(r"(?m)^[ \t]*@(?:code|functions)\s*\{", s)]


VOID_TAGS = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}


def markup_depth(s, pos):
    """How many elements are open at pos in Razor markup (`@code` bodies skipped)."""
    skip = []
    for ob in code_blocks(s):
        end = match_fwd(s, ob)
        skip.append((ob, end if end > 0 else len(s)))
    depth = 0
    for m in re.finditer(r"<(/?)([A-Za-z][\w.:-]*)[^<>]*?(/?)>", s[:pos]):
        if any(a <= m.start() <= b for a, b in skip) or m.group(3) or m.group(2).lower() in VOID_TAGS:
            continue
        depth = max(0, depth - 1) if m.group(1) else depth + 1
    return depth


def match_fwd(code, i):
    depth = 0
    for j in range(i, len(code)):
        ch = code[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
            if depth == 0:
                return j
    return -1


def match_back(code, j):
    depth = 0
    for k in range(j, -1, -1):
        ch = code[k]
        if ch in ")]}":
            depth += 1
        elif ch in "([{":
            depth -= 1
            if depth == 0:
                return k
    return -1


def split_args(code, a, b):
    """-> [(start, end)] of the top-level comma-separated parts of code[a:b]."""
    parts, depth, start = [], 0, a
    for j in range(a, b):
        ch = code[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "," and depth == 0:
            parts.append((start, j))
            start = j + 1
    if code[start:b].strip():
        parts.append((start, b))
    return parts


def squash(text, limit=80):
    t = re.sub(r"\s+", " ", text).strip()
    return t if len(t) <= limit else t[:limit - 3] + "..."


class Src:
    def __init__(self, path, text):
        self.path, self.orig = path, text
        if path.endswith(".cs"):
            self.code, self.lits = clean(text)
        elif path.endswith((".razor", ".cshtml")):
            self.code, self.lits = clean_markup(text), {}
        else:
            self.code, self.lits = text, {}
        self.nl = [i for i, c in enumerate(text) if c == "\n"]
        self._blocks = self._methods = self._classes = self._bindings = None

    def line(self, pos):
        return bisect.bisect_left(self.nl, pos) + 1

    def literal(self, a, b):
        """-> the string value when code[a:b] is one string literal, else None."""
        seg = self.code[a:b]
        p = a + len(seg) - len(seg.lstrip())
        while p < b and self.code[p] in "@$":
            p += 1
        if p in self.lits:
            end, val = self.lits[p]
            if not self.code[end:b].strip():
                return val
        return None

    def blocks(self):
        if self._blocks is None:
            stack, pairs = [], []
            for i, ch in enumerate(self.code):
                if ch == "{":
                    stack.append(i)
                elif ch == "}" and stack:
                    pairs.append((stack.pop(), i))
            self._blocks = sorted(pairs)
        return self._blocks

    def block_of(self, pos):
        best = (0, len(self.code))
        for a, b in self.blocks():
            if a > pos:
                break
            if a < pos <= b and b - a < best[1] - best[0]:
                best = (a, b)
        return best

    def methods(self):
        if self._methods is None:
            self._methods = []
            code = self.code
            for m in METHOD_RE.finditer(code):
                name = m.group(2)
                if name in ("if", "for", "foreach", "while", "switch", "using", "lock", "catch", "return", "nameof"):
                    continue
                op = m.end() - 1
                cp = match_fwd(code, op)
                if cp < 0:
                    continue
                k = cp + 1
                rest = code[k:k + 400]
                wm = re.match(r"\s*(?:where\b[^{;=]*)?", rest)
                k += wm.end() if wm else 0
                if code.startswith("=>", k):
                    end = k
                    depth = 0
                    while end < len(code):
                        ch = code[end]
                        if ch in "([{":
                            depth += 1
                        elif ch in ")]}":
                            depth -= 1
                        elif ch == ";" and depth == 0:
                            break
                        end += 1
                    body = (k, end)
                elif k < len(code) and code[k] == "{":
                    e = match_fwd(code, k)
                    if e < 0:
                        continue
                    body = (k, e)
                else:
                    continue
                params = []
                for i, (a, b) in enumerate(split_args(code, op + 1, cp)):
                    t = re.sub(r"\[[^\]]*\]", " ", code[a:b])
                    t = t.split("=")[0].strip()
                    words = re.findall(r"[\w.<>\[\],?]+", t)
                    if words:
                        params.append(dict(name=words[-1], this=words[0] == "this", type=" ".join(words[:-1])))
                self._methods.append(dict(name=name, name_pos=m.start(2), start=m.start(1),
                                          mods=m.group(1).split(), params=params, body=body,
                                          is_extension=bool(params) and params[0]["this"]))
        return self._methods

    def method_at(self, pos):
        best = None
        for m in self.methods():
            a, b = m["body"]
            if a <= pos <= b and (best is None or b - a < best["body"][1] - best["body"][0]):
                best = m
        return best

    def classes(self):
        if self._classes is None:
            self._classes = []
            code = self.code
            for m in CLASS_RE.finditer(code):
                k = m.end()
                g = re.match(r"\s*<[^{};]*?>", code[k:])
                k += g.end() if g else 0
                ob = code.find("{", k)
                semi = code.find(";", k)
                if ob < 0 or (0 <= semi < ob and "(" not in code[k:semi]):
                    continue
                head = code[k:ob]
                head = re.sub(r"\([^)]*\)", "", head)           # primary constructor
                head = head.split(" where ")[0]
                base = base_q = None
                if ":" in head:
                    first = head.split(":", 1)[1].split(",")[0].strip()
                    base_q = re.sub(r"<.*", "", first).replace("global::", "").strip() or None
                    base = base_q.split(".")[-1].strip() if base_q else None
                end = match_fwd(code, ob)
                start = m.start(1) if m.group(1) else m.start(0) + len(m.group(0)) - len(m.group(0).lstrip())
                self._classes.append(dict(name=m.group(2), mods=m.group(1).split(), base=base, base_q=base_q,
                                          start=start, name_pos=m.start(2), body=(ob, end if end > 0 else len(code)),
                                          attrs=attrs_before(self, start)))
            for c in self._classes:
                outer = [o["name"] for o in self._classes if o["body"][0] < c["start"] < o["body"][1]]
                c["ns"] = self.namespace_at(c["start"])
                c["full"] = ".".join([n for n in [c["ns"]] if n] + outer + [c["name"]])
        return self._classes

    def class_at(self, pos):
        """-> the innermost class whose body contains pos, or None."""
        best = None
        for c in self.classes():
            if c["body"][0] < pos < c["body"][1] and (best is None or c["body"][0] > best["body"][0]):
                best = c
        return best

    def namespace_at(self, pos):
        """-> the namespace (dotted) that encloses pos: a file-scoped `namespace X;` or `namespace X { }` blocks."""
        code, parts = self.code, []
        for m in re.finditer(r"(?m)^[ \t]*namespace\s+([\w.]+)\s*([;{])", code):
            if m.group(2) == ";":
                if m.start() < pos:
                    parts.append(m.group(1))
                continue
            ob = m.end() - 1
            end = match_fwd(code, ob)
            if ob < pos and (end < 0 or pos < end):
                parts.append(m.group(1))
        return ".".join(parts)

    def bindings(self):
        if self._bindings is None:
            self._bindings = []
            code = self.code
            for m in BINDING_RE.finditer(code):
                a = m.end()
                depth, e = 0, a
                while e < len(code):
                    ch = code[e]
                    if ch in "([{":
                        depth += 1
                    elif ch in ")]}":
                        if depth == 0:
                            break
                        depth -= 1
                    elif ch == ";" and depth == 0:
                        break
                    e += 1
                self._bindings.append(dict(name=m.group(1), pos=m.start(1), expr=(a, e), block=self.block_of(m.start())))
        return self._bindings

    def binding_at(self, name, pos):
        best = None
        for b in self.bindings():
            if b["name"] == name and b["pos"] < pos and b["block"][0] <= pos <= b["block"][1]:
                if best is None or b["pos"] > best["pos"]:
                    best = b
        return best


def attrs_before(src, pos):
    """-> [(name, args_text)] of the attribute lists right before a declaration."""
    code, j, res, pre = src.code, pos - 1, [], []
    while True:
        while j >= 0 and code[j].isspace():
            j -= 1
        ls = code.rfind("\n", 0, j + 1) + 1
        d = re.match(r"[ \t]*#\s*(if|elif|else|endif)\b.*", code[ls:j + 1])
        if j >= 0 and d:
            pre.append(src.line(ls))
            j = ls - 1
            continue
        if j >= 0 and re.match(r"[ \t]*#\s*(?:region|endregion|pragma|nullable)\b", code[ls:j + 1]):
            j = ls - 1
            continue
        if j < 0 or code[j] != "]":
            break
        o = match_back(code, j)
        if o < 0:
            break
        res = parse_attr_list(src, o + 1, j) + res
        j = o - 1
    if pre and res:
        res.append(("#preprocessor", f"{src.path}:{min(pre)}"))
    return res


def parse_attr_list(src, a, b):
    out = []
    for s, e in split_args(src.code, a, b):
        t = src.code[s:e]
        m = re.match(r"\s*(?:\w+\s*:(?!:))?\s*(?:global\s*::\s*)?([\w.]+)\s*(\()?", t)
        if not m:
            continue
        name = m.group(1).split(".")[-1]
        if name.endswith("Attribute"):
            name = name[:-9]
        args = ""
        if m.group(2):
            op = s + m.end() - 1
            cp = match_fwd(src.code, op)
            args = src.orig[op + 1:cp] if cp > 0 else ""
        out.append((name, args))
    return out


def attr_route(args):
    m = re.search(r"\"((?:[^\"\\]|\\.)*)\"", args)
    return m.group(1) if m else None


# ------------------------------------------------------------------ chains and receivers

# a fallback policy right-hand side read as a policy: a new builder or policy, or another options policy
FALLBACK_SET = re.compile(r"new\s+(?:AuthorizationPolicyBuilder|AuthorizationPolicy)\b[^?:]*|\w+\s*\.\s*DefaultPolicy", re.S)


def expr_text(code, i):
    """The expression starting at i, up to a ';', ',' or closing bracket at depth 0 -> stripped text."""
    depth, j = 0, i
    while j < len(code):
        ch = code[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                break
            depth -= 1
        elif ch in ";," and depth == 0:
            break
        j += 1
    return code[i:j].strip()


REACH_WORDS = re.compile(r"\b(?:FallbackPolicy|SetFallbackPolicy|AuthorizeFilter|AllowAnonymousFilter|MapControllers|"
                         r"MapControllerRoute|MapDefaultControllerRoute|MapRazorPages|MapRazorComponents|MapBlazorHub)\b")
LOCAL_FN = re.compile(r"(?s)^(?:\[[^\]]*\]\s*)*(?:" + MODS + r"\s+)*(?!(?:return|throw|yield|await|else|new|case|goto|using|"
                      r"var)\b)([^=;{}]*[\w>\]?)])\s+(\w+)\s*(?:<[^<>(){};=]*>)?\s*\(([^(){};]*)\)"
                      r"\s*(?:where\b[^{};=]*)?(=>.*)?$")
NOT_FN = {"if", "for", "foreach", "while", "switch", "using", "lock", "catch", "return", "nameof", "typeof", "sizeof",
          "fixed", "checked", "unchecked", "when", "default", "new", "await", "throw"}


def declared_at(code, q):
    """True when the name at q is declared there (`T Name(` after a statement boundary), not called."""
    hs = max(code.rfind(c, 0, q) for c in ";{}") + 1
    head = re.sub(r"\[[^\]]*\]", " ", code[hs:q]).strip()
    words = head.split()
    return bool(words) and re.fullmatch(r"[\w.<>\[\]?,() ]*[\w>\]?)]", head) is not None \
        and words[-1] not in ("return", "await", "new", "throw", "else", "yield", "in", "is", "as", "case", "goto",
                              "using", "var", "out", "ref", "nameof", "typeof") \
        and not re.search(r"\b(?:if|while|for|foreach|switch|lock|using|else|do|return|await|throw|new|case|yield|"
                          r"goto)\b", head) and ("(" not in head or head.startswith("(") or re.match(MODS, head))


def local_function_at(code, pos):
    """A function declared without modifiers (a local function, or a method with default access) whose
    body holds pos -> dict(name, name_pos), else None. Walks the enclosing blocks outward and reads the
    head before each `{`; also the head of an expression-bodied one (`T F(...) => ...;`)."""
    def head_of(end):
        hs = max(code.rfind(c, 0, end) for c in ";{}") + 1
        return hs, code[hs:end]
    hs, head = head_of(pos)
    m = LOCAL_FN.match(head.strip())
    if m and m.group(4) and m.group(2) not in NOT_FN:
        return dict(name=m.group(2), name_pos=hs + len(head) - len(head.lstrip()) + m.start(2))
    j, depth = pos - 1, 0
    while j >= 0:
        ch = code[j]
        if ch == "}":
            depth += 1
        elif ch == "{":
            if depth:
                depth -= 1
            else:
                hs, head = head_of(j)
                if re.search(r"\b(?:class|record|struct|interface|namespace)\b", head):
                    return None
                m = LOCAL_FN.match(head.strip())
                if m and not m.group(4) and m.group(2) not in NOT_FN:
                    return dict(name=m.group(2), name_pos=hs + len(head) - len(head.lstrip()) + m.start(2))
        j -= 1
    return None


HOST_WORDS = ("CreateBuilder", "CreateSlimBuilder", "CreateEmptyBuilder", "CreateDefaultBuilder",
              "CreateApplicationBuilder", "WebApplication.Create", "new WebHostBuilder", "new HostBuilder")
HOST_BUILD = re.compile(r"\b(?:WebApplication|Host|WebHost|FunctionsApplication)\s*\.\s*"
                        r"(?:Create(?:Slim|Empty|Default|Application|EmptyApplication)?Builder|Create)\s*\("
                        r"|\bnew\s+(?:WebHostBuilder|HostBuilder)\s*\(")
CLASS_USE_BEFORE = re.compile(r"(?:(?:\bUseStartup|\bConfigureOptions)\s*(?:<\s*|\(\s*typeof\s*\(\s*)|\bnew\s+)"
                              r"(?:global::)?(?:\w+\s*\.\s*)*$")
GENERIC_ADD = re.compile(r"\b(Add\w*|TryAdd\w*)\s*<((?:[^<>();{}]|<(?:[^<>();{}]|<[^<>();{}]*>)*>)*)$")


def static_call_to(s2, q, owner, src):
    """True when the call at q can reach the static method of class `owner`: `Owner.Name(`, an
    unqualified call inside that class, or an unqualified call in a file with `using static ...Owner;`."""
    before = s2.code[max(0, q - 200):q]
    if re.search(r"\.\s*$", before):
        return bool(re.search(r"(?<![\w])" + re.escape(owner["name"]) + r"\s*\.\s*$", before))
    if s2 is src and owner["body"][0] <= q <= owner["body"][1]:
        return True
    return bool(re.search(r"\busing\s+static\s+(?:[\w.]+\.)?" + re.escape(owner["name"]) + r"\s*;", s2.code))


def class_use_at(code, q):
    """True when the class name at q is a use that runs its Configure methods: UseStartup<T>,
    ConfigureOptions<T>, new T(...), or a DI registration of T as an options setup."""
    before = code[max(0, q - 200):q]
    if CLASS_USE_BEFORE.search(before):
        return True
    g = GENERIC_ADD.search(before)
    return bool(g and "ConfigureOptions" in g.group(2) + code[q:q + 80].split(">")[0])


def conditional_at(code, pos):
    """True when the statement at pos runs under a condition: inside `#if`, an enclosing `if`/`else`/
    `switch`/loop/`catch` block, a braceless `if (...)`/`else` statement, or after an earlier `if` in the
    same block that may leave it (`return` or `goto`, not inside a lambda or a nested block). Blocks are walked outward;
    other earlier statements in a block are skipped."""
    pre = re.findall(r"(?m)^[ \t]*#\s*(if|endif)\b", code[:pos])
    if pre.count("if") > pre.count("endif"):
        return True
    cond_head = re.compile(r"\b(?:if|while|for|foreach|switch|catch)\s*$")
    leaves = re.compile(r"\b(?:return|goto)\b")

    def leaves_block(a, b):
        """`return`/`goto` in code[a:b] outside nested blocks, calls and lambdas."""
        depth, k = 0, a
        for m in re.finditer(r"[(\[{]|[)\]}]|\b(?:return|goto)\b", code[a:b]):
            tok = m.group(0)
            if tok in "([{":
                depth += 1
            elif tok in ")]}":
                depth -= 1
            elif depth == 0:
                return True
        return False
    branch = re.compile(r"\s*(?:else\b|if\s*\(|case\b|default\s*:)")

    def block_head(k):
        """`if`/`else` when the block opening at code[k] == '{' is the body of one, else None."""
        k -= 1
        while k >= 0 and code[k].isspace():
            k -= 1
        if code[max(0, k - 3):k + 1] == "else" and (k < 4 or not code[k - 4].isalnum()):
            return "else"
        if k >= 0 and code[k] == ")":
            o = match_back(code, k)
            if o > 0 and cond_head.search(code[max(0, o - 12):o]):
                return "if"
        return None

    # own: the segment holds the statement at pos or the head of a block or call that encloses it
    depth, j, stmt_end, own = 0, pos - 1, pos, True
    while True:
        ch = code[j] if j >= 0 else None
        if ch is not None and depth:
            if ch in ")]}":
                depth += 1
            elif ch in "([{":
                depth -= 1
            j -= 1
            continue
        if ch in (")", "]"):
            depth = 1
            j -= 1
            continue
        if ch is None or ch in ";{}(":
            if stmt_end is not None and branch.match(code[j + 1:stmt_end]):
                if own or leaves.search(code[j + 1:stmt_end]):
                    return True
            if ch is None:
                return False
            if ch == "{" and block_head(j):
                return True
            stmt_end = j
            own = ch in "{("
            if ch == "}":
                o = match_back(code, j)
                if o > 0 and block_head(o) and leaves_block(o + 1, j):
                    return True
                depth, stmt_end = 1, None
        j -= 1


MVC_OPTIONS_CALL = re.compile(r"\b(?:AddControllers|AddControllersWithViews|AddMvc|AddMvcCore|AddRazorPages|"
                              r"(?:Post)?Configure\s*<\s*MvcOptions\s*>)\s*$")


def mvc_global_filter(code, pos):
    """True when `new ...Filter` at pos is the argument of `x.Filters.Add(` directly in an
    AddControllers/AddMvc/Configure<MvcOptions> options lambda, with no condition in between."""
    if not re.search(r"\.\s*Filters\s*\.\s*Add\s*\(\s*$", code[max(0, pos - 80):pos]):
        return False
    depth, j = 0, pos - 1
    first = True
    while j >= 0:
        ch = code[j]
        if ch in ")]}":
            depth += 1
        elif ch in "([{":
            if depth:
                depth -= 1
            elif ch == "(":
                if first:
                    first = False      # the Filters.Add( paren itself
                elif MVC_OPTIONS_CALL.search(code[max(0, j - 60):j]):
                    between = code[j:pos]
                    return not re.search(r"\b(?:if|else|switch|foreach|for|while)\b|\?[^.?\[]", between)
                else:
                    return False
            elif ch == "{":
                pass                   # the options lambda body; keep going out
            else:
                return False
        j -= 1
    return False


def chain_forward(src, i):
    """i points at '.' (or '?.') before a call name -> ([(name, pos, (a, b) or None)], end)."""
    code, calls = src.code, []
    pat = re.compile(r"\s*!?\s*\??\.\s*(\w+)")
    while True:
        m = pat.match(code, i)
        if not m:
            break
        name, j = m.group(1), m.end()
        g = re.compile(r"\s*<[^<>(){};=]*(?:<[^<>(){};=]*>[^<>(){};=]*)*>").match(code, j)
        if g and re.match(r"\s*\(", code[g.end():]):
            j = g.end()
        p = re.compile(r"\s*\(").match(code, j)
        if not p:
            calls.append((name, m.start(1), None))
            i = j
            continue
        close = match_fwd(code, p.end() - 1)
        if close < 0:
            break
        calls.append((name, m.start(1), (p.end(), close)))
        i = close + 1
    return calls, i


# conventions on a mount that cannot allow anonymous access
# framework conventions that only describe the endpoint (docs, naming, content types, rendering); any other
# convention name, including your own With*/Produces*, is listed under other_conventions
FRAMEWORK_META = frozenset((
    "WithName", "WithDisplayName", "WithGroupName", "WithDescription", "WithSummary", "WithTags", "WithOpenApi",
    "WithOrder", "WithRequestTimeout", "WithHttpLogging", "WithFormOptions", "WithFormMappingOptions",
    "WithStaticAssets", "WithApiVersionSet", "WithMetadata", "Produces", "ProducesProblem",
    "ProducesValidationProblem", "Accepts", "DisableAntiforgery", "ExcludeFromDescription", "CacheOutput",
    "AddInteractiveServerRenderMode", "AddInteractiveWebAssemblyRenderMode", "AddAdditionalAssemblies"))
MOUNT_INERT = ("RequireCors", "RequireRateLimiting", "DisableRateLimiting", "RequireHost", "DisableRequestTimeout",
               "DisableHttpMetrics")


def stmt_before(code, k):
    """Index of the last non-space character at or before k, skipping preprocessor lines (#region ...)."""
    while True:
        while k >= 0 and code[k].isspace():
            k -= 1
        if k < 0:
            return k
        line_start = code.rfind("\n", 0, k + 1) + 1
        if code[line_start:k + 1].lstrip().startswith("#"):
            k = line_start - 1
            continue
        return k


def receiver_start(code, d):
    """d is the index of the '.' before a call -> start index of the receiver expression or None."""
    j = d - 1
    while True:
        while j >= 0 and code[j].isspace():
            j -= 1
        if j >= 0 and code[j] in "?!":
            j -= 1
            while j >= 0 and code[j].isspace():
                j -= 1
        if j >= 0 and code[j] == ")":
            o = match_back(code, j)
            if o < 0:
                return None
            j = o - 1
            while j >= 0 and code[j].isspace():
                j -= 1
            if j >= 0 and code[j] == ">":
                depth = 0
                while j >= 0:
                    if code[j] == ">":
                        depth += 1
                    elif code[j] == "<":
                        depth -= 1
                        if depth == 0:
                            break
                    j -= 1
                j -= 1
                while j >= 0 and code[j].isspace():
                    j -= 1
            if j < 0 or not (code[j].isalnum() or code[j] == "_"):
                return None
        if j >= 0 and (code[j].isalnum() or code[j] == "_"):
            k = j
            while k >= 0 and (code[k].isalnum() or code[k] == "_"):
                k -= 1
            start = k + 1
            t = k
            while t >= 0 and code[t].isspace():
                t -= 1
            if t >= 0 and code[t] == "." and not (t > 0 and code[t - 1] == "."):
                j = t - 1
                continue
            return start
        return None


def where(src, pos):
    return f"{src.path}:{src.line(pos)}"


class Cov:
    """Visible authorization for one endpoint or receiver."""

    def __init__(self):
        self.items, self.unknown, self.prefix, self.other, self.checks, self.via = [], [], [], [], [], []
        # unknown and overrides: something unread that could allow anonymous access; with one of them an
        # endpoint is never `authorized`. notes: something unread that could only add authorization.
        self.overrides, self.notes = [], []
        # class-level [Route] templates found by class_attrs: own, partial parts, then bases
        self.routes = []
        # the files where the endpoint's receiver chain starts (the app that serves it)
        self.roots = []
        # ShortCircuit() calls on the chain or its groups: authorization middleware never runs
        self.short_circuit = []

    def add(self, kind, level, at, detail=None):
        self.items.append(dict(kind=kind, level=level, at=at, detail=detail))

    def merge(self, other):
        self.items += other.items
        self.unknown += other.unknown
        self.overrides += other.overrides
        self.notes += other.notes
        self.short_circuit += other.short_circuit
        self.roots += [r for r in other.roots if r not in self.roots]
        self.prefix = other.prefix + self.prefix if other.prefix is not None and self.prefix is not None else None
        self.other += other.other
        self.checks += other.checks
        self.via += other.via

    def state(self, fallback=None):
        anon = [i for i in self.items if i["kind"] == "anonymous"]
        auth = [i for i in self.items if i["kind"] == "authorize"]
        blocking = list(dict.fromkeys(self.overrides + self.unknown))
        if anon:
            st, src, also = "anonymous", anon, blocking + self.notes
        elif auth and blocking:
            return dict(state="unknown", reasons=blocking[:4],
                        seen=[dict(kind=i["kind"], level=i["level"], at=i["at"]) for i in auth])
        elif auth:
            st, src, also = "authorized", auth, self.notes
        elif blocking or self.notes:
            return dict(state="unknown", reasons=(blocking + self.notes)[:4])
        elif fallback:
            return dict(state="fallback", at=fallback)
        else:
            return dict(state="none-visible")
        out = dict(state=st, levels=[dict(level=i["level"], at=i["at"], **({"detail": i["detail"]} if i["detail"] else {}))
                                     for i in src])
        if also:
            out["also_unknown"] = also[:4]
        return out


class Analyzer:
    def __init__(self, local):
        self.local = local
        self.srcs = {}
        self.ctx_cache = {}

    def src(self, tree, path, text=None):
        key = (id(tree), path) if text is None else (id(tree), path, hashlib.sha1(text.encode()).hexdigest())
        if key not in self.srcs:
            if text is None:
                text = tree.read(path) if tree is not None else None
            self.srcs[key] = Src(path, text) if text is not None else None
        return self.srcs[key]

    # .................................................. conventions on a call chain

    def classify(self, src, cov, calls, level):
        for name, pos, args in calls:
            at = where(src, pos)
            text = src.orig[args[0]:args[1]] if args else ""
            ctext = src.code[args[0]:args[1]] if args else ""
            if name == "RequireAuthorization":
                cov.add("authorize", level, at, squash(text, 60) or None)
            elif name == "AllowAnonymous":
                cov.add("anonymous", level, at)
            elif name in ("WithMetadata", "Add", "Finally") and re.search(r"\bAllowAnonymous(?:Attribute)?\b", ctext):
                cov.add("anonymous", level, at, name)
            elif name == "WithMetadata" and re.search(r"\bnew\s+(?:[\w.]*\.)?Authorize(?:Attribute)?\b", ctext):
                cov.add("authorize", level, at, "WithMetadata")
            elif name in ("WithMetadata", "Add", "Finally") and self.own_anonymous_in(ctext):
                cov.add("anonymous", level, at, f"{name} with {self.own_anonymous_in(ctext)}")
            elif name in ("Add", "Finally"):
                # Add(b => ...) / Finally(b => ...) edit endpoint metadata directly; what they add is not read
                cov.overrides.append(f"`{name}(...)` at {at} changes endpoint metadata; what it adds is not read")
            elif name in self.local["auth"]:
                cov.add("authorize", level, at, f"local convention {name}")
            elif name in self.local["anon"]:
                cov.add("anonymous", level, at, f"local convention {name}")
            elif level == "group" and name.startswith("Map") and name not in ENDPOINT_CALLS \
                    and name not in ("MapGroup", "MapToApiVersion"):
                cov.unknown.append(f"group returned by `{name}(...)` at {at}; its conventions and route prefix "
                                   "are not read")
                cov.prefix = None
            elif name == "MapGroup":
                if cov.prefix is not None and args:
                    lit = src.literal(*args)
                    cov.prefix.append(lit if lit is not None else "{" + squash(text, 30) + "}")
            elif name == "ShortCircuit":
                cov.short_circuit.append(at)
            elif not name.startswith("Map") and name not in FRAMEWORK_META:
                if name not in cov.other:
                    cov.other.append(name)

    # .................................................. receivers

    def resolve_expr(self, tree, src, a, b, depth, seen):
        """Coverage contributed by the receiver expression code[a:b] (group conventions, prefix)."""
        cov = Cov()
        code = src.code
        seg = code[a:b]
        lead = len(seg) - len(seg.lstrip())
        a += lead
        nm = re.match(r"\s*(?:\w+\s*:)?\s*", code[a:b])          # a named argument
        if nm and ":" in nm.group(0):
            a += nm.end()
        m = re.match(r"(?:this\s*\.\s*)?(\w+)", code[a:b])
        if not m:
            cov.unknown.append(f"receiver `{squash(src.orig[a:b], 40)}` at {where(src, a)} is not a name or a call chain")
            return cov
        root, k = m.group(1), a + m.end()
        root_call = re.match(r"\s*(?:<[^<>(){};]*>)?\s*\(", code[k:b])
        calls, _ = chain_forward(src, k if not root_call else match_fwd(code, k + root_call.end() - 1) + 1)
        calls = [c for c in calls if c[1] < b]
        if any(c[0] in ("Build", "CreateBuilder", "Create") for c in calls) or root in ("WebApplication",):
            after = [c for c in calls if c[1] > max(p for n, p, _ in calls if n in ("Build", "CreateBuilder", "Create"))] \
                if calls else []
            self.classify(src, cov, after, "group")
            return cov
        if root_call:
            cov.unknown.append(f"group built by `{root}(...)` at {where(src, a)}; its conventions are not read")
            self.classify(src, cov, calls, "group")
            return cov
        key = (src.path, root, a)
        if key in seen:
            cov.unknown.append(f"`{root}` refers to itself at {where(src, a)}")
            return cov
        seen = seen | {key}
        bind = src.binding_at(root, a)
        if bind:
            again = next((rm.start() for rm in re.finditer(r"(?<![\w.])" + re.escape(root) + r"\s*(?:\?\?)?=(?![=>])", code)
                          if bind["expr"][1] <= rm.start() and bind["block"][0] <= rm.start() <= bind["block"][1]), None)
            if again is not None and again < b:
                cov.unknown.append(f"`{root}` is assigned again at {where(src, again)}; "
                                   "which group it holds here is not read")
                cov.prefix = None
                return cov
            inner = self.resolve_expr(tree, src, bind["expr"][0], bind["expr"][1], depth, seen)
            if re.search(r"\.\s*Map\w*\s*\(", code[bind["expr"][0]:bind["expr"][1]]):
                self.scan_uses(src, inner, root, bind["expr"][1], again if again is not None else bind["block"][1],
                               bind["block"], "group", lambda q: src.binding_at(root, q) is bind)
            inner_self = Cov()
            self.classify(src, inner_self, calls, "group")
            inner.items += inner_self.items
            inner.other += inner_self.other
            inner.unknown += inner_self.unknown
            inner.short_circuit += inner_self.short_circuit
            if inner_self.prefix is None:
                inner.prefix = None
            if inner.prefix is not None:
                inner.prefix += inner_self.prefix
            return inner
        if re.search(r"\bUseEndpoints\s*\(\s*" + re.escape(root) + r"\s*=>", code):
            self.classify(src, cov, calls, "group")
            return cov
        meth = src.method_at(a)
        if meth:
            idx = next((i for i, p in enumerate(meth["params"]) if p["name"] == root), None)
            if idx is not None:
                body = meth["body"]
                ptype = re.sub(r"^this\s+", "", meth["params"][idx].get("type", ""))
                route_only = re.search(r"\b(?:IEndpointRouteBuilder|WebApplication)\??$", ptype)
                again = next((rm.start() for rm in re.finditer(r"(?<![\w.])" + re.escape(root) + r"\s*(?:\?\?)?=(?![=>])",
                                                                code[body[0]:body[1]])), None)
                if again is not None:
                    cov.unknown.append(f"parameter `{root}` is assigned at {where(src, body[0] + again)}; "
                                       "which group it holds here is not read")
                    cov.prefix = None
                    return cov
                if meth["name"] in ("Configure", "Main"):
                    self.classify(src, cov, calls, "group")
                    if not route_only:
                        self.scan_uses(src, cov, root, body[0], body[1], src.block_of(body[0] + 1), "group")
                    return cov
                caller = self.resolve_param(tree, src, meth, idx, depth, seen)
                self.classify(src, caller, calls, "group")
                if not route_only:
                    self.scan_uses(src, caller, root, body[0], body[1], src.block_of(body[0] + 1), "group")
                return caller
        lam = None
        for lm in re.finditer(r"(?:\b" + re.escape(root) + r"|\([^()]*\b" + re.escape(root) + r"\b[^()]*\))\s*=>", code[:a]):
            lam = lm
        if lam and src.block_of(lam.start()) == src.block_of(a):
            cov.unknown.append(f"`{root}` at {where(src, a)} is a lambda parameter; what the callback is given is not read")
        else:
            cov.unknown.append(f"`{root}` at {where(src, a)} is not a local or a parameter in this file")
        cov.prefix = None
        return cov

    def caller_chains(self, src, meth, what):
        """`what` is returned from method `meth` -> reasons for each call site that chains conventions
        onto the result (the caller can add AllowAnonymous to what is returned)."""
        tree = getattr(self, "_tree", None)
        if meth is None or tree is None:
            return []
        key = ("callers", id(tree), src.path, meth["name_pos"])
        if key in self.ctx_cache:
            return self.ctx_cache[key]
        out = []
        for p in tree.grep([meth["name"]], (".cs",)):
            s2 = src if p == src.path else self.src(tree, p)
            for m in re.finditer(r"\b" + re.escape(meth["name"]) + r"\s*(?:<[^<>(){};]*>)?\s*\(", s2.code if s2 else ""):
                if any(d["name_pos"] == m.start() for d in s2.methods()):
                    continue
                cp = match_fwd(s2.code, m.end() - 1)
                if cp < 0:
                    continue
                after, _ = chain_forward(s2, cp + 1)
                tmp = Cov()
                self.classify(s2, tmp, after, "endpoint")
                if tmp.items or tmp.other:
                    out.append(f"{meth['name']} returns `{what}` and its call at {where(s2, m.start())} is followed by "
                               f"`{'`, `'.join(c[0] for c in after[:3])}`")
        out = out[:3]
        self.ctx_cache[key] = out
        return out

    def extension_touches(self, call, ext_depth=0):
        """For a user extension `call`, -> a reason when its body adds conventions to (or passes on) the
        builder it is given, else None. Declarations are looked up in the tree being read."""
        tree = getattr(self, "_tree", None)
        if tree is None or ext_depth >= 2:
            return None
        key = ("ext", id(tree), call)
        if key in self.ctx_cache:
            return self.ctx_cache[key]
        self.ctx_cache[key] = None
        found = None
        for p in tree.grep([call], (".cs",)):
            s2 = self.src(tree, p)
            for d in (s2.methods() if s2 else []):
                if d["name"] == call and d.get("is_extension") and d["params"]:
                    tmp = Cov()
                    body = d["body"]
                    ptype = d["params"][0].get("type", "")
                    if re.search(r"\b(?:IEndpointRouteBuilder|WebApplication)\??$", re.sub(r"^this\s+", "", ptype)):
                        continue
                    self.scan_uses(s2, tmp, d["params"][0]["name"], body[0], body[1], s2.block_of(body[0] + 1), "group",
                                   ext_depth=ext_depth + 1)
                    if tmp.items or tmp.overrides:
                        found = (f"{call} ({where(s2, d['name_pos'])}) adds conventions to the builder it is given or "
                                 "passes it on; what reaches this endpoint is not read")
                        break
            if found:
                break
        self.ctx_cache[key] = found
        return found

    def scan_uses(self, src, cov, name, start, end, block, level, same=None, ext_depth=0):
        """Every other use of the variable `name` in code[start:end]. A statement `name.Convention(...)` in
        `block` adds its conventions; in a nested block it is unknown. Mapping on it (`name.Map...`,
        `Use...`, `Run`) is ignored. Any other use (an argument, an alias, a condition without braces)
        is unknown: what is done with it there is not read."""
        code = src.code
        for m in re.finditer(r"(?<![\w.@])" + re.escape(name) + r"\b", code[start:end]):
            q = start + m.start()
            if same is not None and not same(q):
                continue
            nxt = re.match(r"\s*[?!]?\s*\.\s*(\w+)", code[q + len(name):])
            if nxt and nxt.group(1).startswith(("Map", "Use", "Run")):
                call = nxt.group(1)
                if call.startswith("Map") and call != "MapGroup" and call not in ENDPOINT_CALLS:
                    mcalls, _ = chain_forward(src, q + len(name))
                    tmp = Cov()
                    self.classify(src, tmp, mcalls[1:], level)
                    if tmp.items or tmp.other:
                        cov.overrides.append(f"`{name}.{call}(...)` at {where(src, q)} is followed by "
                                             f"`{'`, `'.join(c[0] for c in mcalls[1:4])}`; which applies to what {call} returns")
                    note = self.extension_touches(call, ext_depth)
                    if note:
                        cov.overrides.append(f"`{name}.{call}(...)` at {where(src, q)}: {note}")
                continue
            k = q - 1
            while k >= 0 and code[k].isspace():
                k -= 1
            at_stmt = k < 0 or code[k] in ";{}"
            if re.search(r"\breturn\s*$", code[max(0, q - 12):q]) and re.match(r"\s*;", code[q + len(name):]):
                cov.overrides += self.caller_chains(src, src.method_at(q), name)
                continue
            if nxt and at_stmt:
                mcalls, _ = chain_forward(src, q + len(name))
                tmp = Cov()
                self.classify(src, tmp, mcalls, level)
                if not tmp.items and not tmp.other and not tmp.short_circuit and not tmp.overrides:
                    continue
                if src.block_of(q) != block:
                    cov.overrides.append(f"`{name}.{mcalls[0][0] if mcalls else '?'}` at {where(src, q)} is inside a "
                                         "nested block; whether it runs is not read")
                else:
                    cov.items += tmp.items
                    cov.short_circuit += tmp.short_circuit
                    cov.overrides += tmp.overrides
                    cov.other += [o for o in tmp.other if o not in cov.other]
                continue
            if re.match(r"\s*(?:\?\?)?=(?![=>])", code[q + len(name):]):
                continue
            cov.overrides.append(f"`{name}` is used at {where(src, q)} (passed on, aliased or under a condition); "
                                 "what is done with it there is not read")

    def resolve_param(self, tree, src, meth, idx, depth, seen):
        cov = Cov()
        pname, mname = meth["params"][idx]["name"], meth["name"]
        if depth >= CALLER_DEPTH or tree is None:
            cov.unknown.append(f"`{pname}` is a parameter of {mname} ({where(src, meth['name_pos'])}); "
                               f"callers {'deeper than ' + str(CALLER_DEPTH) + ' levels' if tree else 'not readable'}")
            cov.prefix = None
            return cov
        sites = []
        decl_test = bool(TEST_PATH.search(src.path))
        for path in tree.grep([mname], (".cs",)):
            if TEST_PATH.search(path) and not decl_test:
                continue
            s2 = src if path == src.path else self.src(tree, path)
            if s2 is None:
                continue
            for m in re.finditer(r"\b" + re.escape(mname) + r"\s*(?:<[^<>(){};]*>)?\s*\(", s2.code):
                if any(d["name_pos"] == m.start() for d in s2.methods()):
                    continue
                op = m.end() - 1
                cp = match_fwd(s2.code, op)
                if cp < 0:
                    continue
                args = split_args(s2.code, op + 1, cp)
                t = m.start() - 1
                while t >= 0 and s2.code[t].isspace():
                    t -= 1
                spans = list(args)
                if t >= 0 and s2.code[t] == "." and meth["is_extension"]:
                    rs = receiver_start(s2.code, t)
                    if rs is not None and len(args) < len(meth["params"]):
                        spans = [(rs, t)] + args
                if idx < len(spans):
                    sites.append((s2, spans[idx], cp))
        if not sites:
            cov.unknown.append(f"`{pname}` is a parameter of {mname}; no call site found")
            cov.prefix = None
            return cov
        if len(sites) > MAX_SITES:
            cov.unknown.append(f"`{pname}` is a parameter of {mname}; it has {len(sites)} call sites, more than "
                               f"the {MAX_SITES} this script reads")
            cov.prefix = None
            return cov
        results = []
        for s2, (a, b), cp in sites:
            r = self.resolve_expr(tree, s2, a, b, depth + 1, seen)
            after, _ = chain_forward(s2, cp + 1)
            tmp = Cov()
            self.classify(s2, tmp, after, "group")
            if tmp.items or tmp.other:
                recv = f"{squash(s2.orig[a:b], 40)}." if meth["is_extension"] and idx == 0 else ""
                r.overrides.append(f"`{recv}{mname}(...)` at {where(s2, a)} is followed by "
                                   f"`{'`, `'.join(c[0] for c in after[:3])}`; which applies to what {mname} returns")
            r.via.append(where(s2, a))
            results.append((r, s2, a))
        sigs = {json.dumps([r.state().get("state"), sorted(i["kind"] for i in r.items), r.prefix]) for r, _, _ in results}
        if len(sigs) == 1:
            r = results[0][0]
            r.roots = []
            for x, s2, _ in results:
                for root in (x.roots or [s2.path]):
                    if root not in r.roots:
                        r.roots.append(root)
            if len(sites) > 1:
                r.via.append(f"{len(sites)} call sites agree")
                details = {json.dumps(sorted((i["kind"], i["detail"] or "") for i in x.items)) for x, _, _ in results}
                if len(details) > 1:
                    r.notes.append(f"`{pname}` is a parameter of {mname}; its {len(sites)} call sites agree on the "
                                     "state but differ in policy or roles ("
                                     + ", ".join(f"{where(s2, a)}" for _, s2, a in results[:3]) + ")")
            return r
        cov.unknown.append(f"`{pname}` is a parameter of {mname}; its {len(sites)} call sites differ ("
                           + ", ".join(f"{where(s2, a)} {r.state()['state']}" for r, s2, a in results[:3]) + ")")
        cov.prefix = None
        return cov

    # .................................................. handlers

    def handler(self, src, cov, a, b):
        code = src.code
        text = code[a:b].strip()
        lead = a + len(code[a:b]) - len(code[a:b].lstrip())
        if text.startswith("["):
            j = lead
            attrs = []
            while j < b and code[j] == "[":
                e = match_fwd(code, j)
                if e < 0:
                    break
                attrs += parse_attr_list(src, j + 1, e)
                j = e + 1
                while j < b and code[j].isspace():
                    j += 1
            self.attr_items(cov, attrs, "handler", where(src, lead))
        if "=>" in text:
            self.body_checks(src, cov, a, b)
            return None
        m = re.fullmatch(r"(?:([\w.]+)\.)?(\w+)", text)
        if not m:
            return None
        qual, name = m.group(1), m.group(2)
        here = src.class_at(a)
        decl = []
        for d in src.methods():
            if d["name"] != name:
                continue
            owner = src.class_at(d["start"])
            if qual is not None:
                if owner is not None and (owner["full"] == qual or owner["full"].endswith("." + qual)
                                          or qual in ("this", "base") and owner is here):
                    decl.append(d)
            elif owner is here:
                decl.append(d)
        if len(decl) == 1:
            d = decl[0]
            self.attr_items(cov, attrs_before(src, d["start"]), "handler", where(src, d["name_pos"]))
            self.body_checks(src, cov, *d["body"])
            return None
        if len(decl) > 1:
            return (f"handler `{text}` has {len(decl)} declarations in this file ("
                    + ", ".join(where(src, d["name_pos"]) for d in decl[:3]) + "); which one is not read")
        return f"handler `{text}` is not declared in this file; its attributes are not read"

    def body_checks(self, src, cov, a, b):
        words = HANDLER_CHECKS + tuple(self.local["checks"])
        for m in re.finditer(r"\b(" + "|".join(map(re.escape, words)) + r")\b", src.code[a:b]):
            c = f"{m.group(1)} at line {src.line(a + m.start())}"
            if c not in cov.checks:
                cov.checks.append(c)

    def attr_items(self, cov, attrs, level, at):
        for name, args in attrs:
            if name == "#preprocessor":
                cov.overrides.append(f"a `#if` line sits among the attributes at {args}; which ones are compiled "
                                     "is not read")
            elif name == "Authorize" or name in self.local["auth"]:
                cov.add("authorize", level, at, squash(args, 60) or None)
                own = self.own_authorize(getattr(self, "_tree", None)) if name == "Authorize" else None
                if own:
                    cov.overrides.append(f"the tree declares its own `AuthorizeAttribute` at {own}; which one "
                                         f"`[Authorize]` at {at} is, is not read")
            elif name == "AllowAnonymous" or name in self.local["anon"]:
                cov.add("anonymous", level, at)
            elif name in self.own_anonymous(getattr(self, "_tree", None)):
                cov.add("anonymous", level, at, f"{name}Attribute at {self.own_anonymous(self._tree)[name]}")

    def own_anonymous_in(self, text):
        """-> 'NameAttribute at file:line' when text constructs an in-tree anonymous attribute, or None."""
        own = self.own_anonymous(getattr(self, "_tree", None))
        for m in re.finditer(r"\bnew\s+(?:[\w.]*\.)?(\w+?)(?:Attribute)?\s*[({]", text):
            if m.group(1) in own:
                return f"{m.group(1)}Attribute at {own[m.group(1)]}"
        return None

    def own_anonymous(self, tree):
        """-> {name: 'file:line'} of attribute classes in the tree that derive from AllowAnonymousAttribute or
        implement IAllowAnonymous (attribute name without the Attribute suffix)."""
        key = ("own-anonymous", id(tree))
        if key not in self.ctx_cache:
            found = {}
            for p in tree.grep(["AllowAnonymousAttribute", "IAllowAnonymous"], (".cs",)) if tree is not None else []:
                s2 = self.src(tree, p)
                for m in re.finditer(r"\bclass\s+(\w+?)(?:Attribute)?\b\s*(?:<[^>]*>)?\s*:([^{;]*)", s2.code if s2 else ""):
                    if re.search(r"\b(?:AllowAnonymousAttribute|IAllowAnonymous)\b", m.group(2)):
                        found.setdefault(m.group(1), where(s2, m.start()))
            self.ctx_cache[key] = found
        return self.ctx_cache[key]

    # .................................................. context

    def context(self, tree):
        key = id(tree)
        if key in self.ctx_cache:
            return self.ctx_cache[key]
        items = []
        if tree is not None:
            paths = tree.grep(list(CONTEXT_WORDS), (".cs", ".razor"))
            texts = tree.read_many(paths)
            for p in paths:
                if texts.get(p) is None:
                    continue
                src = self.src(tree, p, texts[p])
                test = bool(TEST_PATH.search(p))
                code = src.code
                if p.endswith(".razor"):
                    for m in re.finditer(r"<\s*AuthorizeRouteView\b", code):
                        items.append(dict(kind="authorize-route-view", file=p, line=src.line(m.start()), test_path=test))
                    continue
                for m in re.finditer(r"\bFallbackPolicy\s*=(?!=)|\bSetFallbackPolicy\s*\(", code):
                    rhs = expr_text(code, m.end())
                    kind = ("fallback-conditional" if conditional_at(code, m.start()) else
                            "fallback-null" if re.fullmatch(r"null|default(?:\s*\(\s*AuthorizationPolicy\s*\??\s*\))?", rhs)
                            else "fallback-policy" if FALLBACK_SET.fullmatch(rhs) else "fallback-unclear")
                    items.append(dict(kind=kind, file=p, line=src.line(m.start()), test_path=test))
                for m in re.finditer(r"\bnew\s+(?:global\s*::\s*)?(?:(?:\w+\s*\.\s*)*)(AuthorizeFilter|AllowAnonymousFilter)\b", code):
                    g = mvc_global_filter(code, m.start())
                    kind = ("global-" if g else "other-") + ("authorize" if m.group(1) == "AuthorizeFilter" else "anonymous")
                    items.append(dict(kind=kind + "-filter", file=p, line=src.line(m.start()), test_path=test))
                for m in re.finditer(r"\bAdd(?:Service)?\s*(?:<\s*(?:global\s*::\s*)?(?:[\w.]*\.)?(AuthorizeFilter|AllowAnonymousFilter)\s*>"
                                     r"|\(\s*typeof\s*\(\s*(?:global\s*::\s*)?(?:[\w.]*\.)?(AuthorizeFilter|AllowAnonymousFilter)\s*\))", code):
                    kind = "other-" + ("authorize" if "AuthorizeFilter" in (m.group(1), m.group(2)) else "anonymous")
                    items.append(dict(kind=kind + "-filter", file=p, line=src.line(m.start()), test_path=test))
                for m in re.finditer(r"\b(?:AuthorizeFolder|AuthorizePage|AuthorizeAreaFolder|AuthorizeAreaPage|"
                                     r"AllowAnonymousToFolder|AllowAnonymousToPage)\s*\(", code):
                    items.append(dict(kind="razor-pages-convention", file=p, line=src.line(m.start()), test_path=test))
                for m in re.finditer(r"\.\s*(MapControllers|MapControllerRoute|MapDefaultControllerRoute|MapRazorPages|"
                                     r"MapRazorComponents|MapBlazorHub)\b", code):
                    calls, end = chain_forward(src, m.start())
                    cov = Cov()
                    self.classify(src, cov, calls[1:], "mount")
                    kinds = sorted({i["kind"] for i in cov.items})
                    unclear = self.mount_unclear(src, m.start(), end, cov)
                    if unclear:
                        items.append(dict(kind="mount-unclear", mount=m.group(1), file=p, line=src.line(m.start()),
                                          test_path=test, effect=kinds, unclear=unclear))
                        continue
                    if not kinds and m.group(1) in ("MapControllerRoute", "MapDefaultControllerRoute"):
                        items.append(dict(kind="mount-none", mount=m.group(1), file=p, line=src.line(m.start()),
                                          test_path=test, effect=[]))
                    if kinds:
                        items.append(dict(kind="mount-" + "+".join("authorization" if k == "authorize" else "anonymous"
                                                                   for k in kinds),
                                          mount=m.group(1), file=p, line=src.line(m.start()), test_path=test,
                                          effect=kinds))
        for c in items:
            if c["file"].endswith(".cs"):
                why = self.reach(tree, self.src(tree, c["file"]), c["line"])
                if why:
                    c["reach"] = why
                    if c["kind"] in ("fallback-policy", "fallback-null"):
                        c["kind"] = "fallback-conditional"
        self.ctx_cache[key] = items
        return items

    @staticmethod
    def mount_unclear(src, dot, end, cov):
        """A mount whose conventions are not all in its own statement -> reason, or None. ShortCircuit()
        skips authorization; a value kept in a variable, passed, or returned can get conventions elsewhere."""
        code, at = src.code, where(src, dot)
        if cov.short_circuit:
            return (f"`ShortCircuit()` at {cov.short_circuit[0]} runs these endpoints straight from routing, so the "
                    "authorization middleware and the fallback policy are skipped")
        if cov.unknown or cov.overrides:
            return (cov.unknown + cov.overrides)[0]
        other = [o for o in cov.other if o not in MOUNT_INERT]
        if other:
            return f"`{'`, `'.join(other[:3])}` on the mount at {at} is not read"
        rs = receiver_start(code, dot)
        k = stmt_before(code, (rs if rs is not None else dot) - 1)
        before = code[max(0, k - 1):k + 1]
        after = re.match(r"\s*(.)", code[end:])
        after = after.group(1) if after else ";"
        if (k < 0 or code[k] in ";{}") and after == ";":
            return None
        if before.endswith("=>") and after == ")":
            # a lambda argument such as UseEndpoints(e => e.MapControllers()): the value is discarded
            return None
        if k >= 0 and (code[k] == ")" or re.search(r"\belse$", code[max(0, k - 4):k + 1])):
            return f"the mount at {at} runs under a condition (a braceless `if`/`else`); which one serves is not read"
        return (f"the value of the mount at {at} is kept, passed or returned; conventions added to it "
                "elsewhere are not read")

    def host_builder(self, tree, src, test, project=False):
        """-> 'file:line' of a host or app builder in the tree (or only in `project`), or None."""
        for p in tree.grep(list(HOST_WORDS), (".cs",)) if tree is not None else []:
            if TEST_PATH.search(p) and not test:
                continue
            if project is not False and self.project_of(tree, p) != project:
                continue
            s2 = src if p == src.path else self.src(tree, p)
            m = HOST_BUILD.search(s2.code if s2 else "")
            if m:
                return where(s2, m.start())
        return None

    def reach(self, tree, src, line, depth=0, at_pos=None):
        """Whether code at `line` runs on every start: None when it is in top-level statements, `Main`,
        `ConfigureServices` or `Configure`, or in a method every caller of which calls it unconditionally
        (three levels up); else the reason it may not run."""
        code = src.code
        pos = 0
        for _ in range(line - 1):
            pos = code.find("\n", pos) + 1
        eol = code.find("\n", pos)
        eol = len(code) if eol < 0 else eol
        hit = REACH_WORDS.search(code, pos, eol)
        pos = hit.start() if hit else pos + len(code[pos:eol]) - len(code[pos:eol].lstrip())
        if at_pos is not None:
            pos = at_pos
        meth = src.method_at(pos)
        lf = local_function_at(code, pos)
        if lf and (meth is None or lf["name_pos"] > meth["name_pos"]):
            meth = lf
        if meth is None:
            cls = next((c for c in src.classes() if c["body"][0] <= pos <= c["body"][1]), None)
            if cls is not None:
                return (f"it is set at {where(src, pos)} in class {cls['name']}, in a member this script does not "
                        "read as a method")
            return None
        if meth["name"] == "Main" and "static" in meth.get("mods", ()):
            return None
        cls = None
        if meth["name"] in ("Configure", "ConfigureServices", "PostConfigure", "ConfigureContainer") \
                and "static" not in meth.get("mods", ()):
            # run by whoever uses the class (UseStartup<T>, ConfigureOptions<T>, a call): follow the class
            cls = min((c for c in src.classes() if c["body"][0] <= meth["name_pos"] <= c["body"][1]),
                      key=lambda c: c["body"][1] - c["body"][0], default=None)
        key = ("reach", id(tree), src.path, meth["name_pos"])
        if key in self.ctx_cache:
            return self.ctx_cache[key]
        self.ctx_cache[key] = f"it is set in {meth['name']} ({where(src, meth['name_pos'])}), called in a loop of calls"
        name, why, sites = meth["name"], None, []
        test = bool(TEST_PATH.search(src.path))
        look = cls["name"] if cls else name
        # a use in another project runs in that project's app, not in this one, when this project is an app
        mine = self.project_of(tree, src.path) if tree is not None else None
        app = mine is not None and self.host_builder(tree, src, test, mine) is not None
        owner = min((c for c in src.classes() if c["body"][0] <= meth["name_pos"] <= c["body"][1]),
                    key=lambda c: c["body"][1] - c["body"][0], default=None)
        qualified = (not cls and owner is not None and "static" in meth.get("mods", ())
                     and not meth.get("is_extension"))
        elsewhere = []
        for p in (tree.grep([look], (".cs",)) if tree is not None and depth < CALLER_DEPTH else []):
            if TEST_PATH.search(p) and not test:
                continue
            s2 = src if p == src.path else self.src(tree, p)
            if s2 is None:
                continue
            if app:
                pp = self.project_of(tree, p)
                if pp is not None and pp != mine:
                    if re.search(r"(?<![\w])" + re.escape(look) + r"\b", s2.code):
                        elsewhere.append(pp)
                    continue
            if cls:
                for m in re.finditer(r"(?<![\w])" + re.escape(look) + r"\b", s2.code):
                    if class_use_at(s2.code, m.start()):
                        sites.append((s2, m.start()))
                continue
            for m in re.finditer(r"(?<![\w])" + re.escape(name) + r"\s*(?:<[^<>(){};]*>)?\s*\(", s2.code):
                if any(d["name_pos"] == m.start() for d in s2.methods()) or declared_at(s2.code, m.start()):
                    continue
                if qualified and not static_call_to(s2, m.start(), owner, src):
                    continue
                sites.append((s2, m.start()))
        if not sites and elsewhere:
            why = (f"it is set in {name} ({where(src, meth['name_pos'])}), whose only uses by name are in another "
                   f"project ({sorted(set(elsewhere))[0]}), which runs its own app")
            self.ctx_cache[key] = why
            return why
        at = f"{name} ({where(src, meth['name_pos'])})" if not cls else \
            f"{name} of class {cls['name']} ({where(src, meth['name_pos'])}), which runs where the class is used"
        host = None
        if cls and not sites and {"Configure", "ConfigureServices"} <= {
                d["name"] for d in src.methods() if cls["body"][0] <= d["name_pos"] <= cls["body"][1]}:
            # a Startup class nobody uses is the app only when nothing else in the tree builds a host
            host = self.host_builder(tree, src, test)
            if host is None:
                self.ctx_cache[key] = None
                return None
        if cls and not sites:
            why = f"it is set in {name} of class {cls['name']} ({where(src, meth['name_pos'])}), and the class is not used in the tree"
            if host:
                why += f"; the app is built at {host}"
            self.ctx_cache[key] = why
            return why
        if depth >= CALLER_DEPTH:
            why = f"it is set in {at}, whose callers are more than {CALLER_DEPTH} levels up"
        elif not sites:
            why = f"it is set in {at}, which is not called in the tree (a method passed by name is not followed)"
        else:
            for s2, q in sites:
                if conditional_at(s2.code, q):
                    why = f"it is set in {at}, called under a condition at {where(s2, q)}"
                    break
                up = self.reach(tree, s2, s2.line(q), depth + 1, q)
                if up:
                    why = up
                    break
        self.ctx_cache[key] = why
        return why

    def projects(self, tree):
        key = ("projects", id(tree))
        if key not in self.ctx_cache:
            self.ctx_cache[key] = {os.path.dirname(p) for p in (tree.files() if tree is not None else ())
                                   if p.endswith(".csproj")}
        return self.ctx_cache[key]

    def project_of(self, tree, path):
        """-> the directory of the nearest enclosing .csproj, or None."""
        dirs, d = self.projects(tree), os.path.dirname(path)
        while True:
            if d in dirs:
                return d
            if not d:
                return None
            d = os.path.dirname(d)

    def scoped(self, tree, kinds, test, path, via, pred=None, foreign=False):
        """Context items of these kinds that apply to an endpoint in `path` (reached through the `via`
        call sites) -> (items, reasons). An item applies when it is in the same file, a call site's file,
        or the same project (nearest .csproj). One where either side has no project is a reason, never
        applied; one in another project is skipped (that is another app), or a reason with `foreign`
        (controllers are found by assembly, so a host project may serve them)."""
        files = {path} | {v.rsplit(":", 1)[0] for v in via if ":" in v}
        mine = {self.project_of(tree, f) for f in files} - {None}
        hits, reasons = [], []
        for c in self.context(tree):
            if c["kind"] not in kinds or not (test or not c["test_path"]) or (pred and not pred(c)):
                continue
            cp = self.project_of(tree, c["file"])
            if c["file"] in files or cp is not None and cp in mine:
                hits.append(c)
            elif cp is not None and mine and not foreign:
                continue
            else:
                reasons.append(f"{c.get('mount') or c['kind']} at {c['file']}:{c['line']} is in "
                               f"{'project ' + cp if cp else 'no project'}, this endpoint in "
                               f"{'project ' + ', '.join(sorted(mine)) if mine else 'no project'}; "
                               "whether it applies is not read")
        return hits, ([] if hits else reasons)

    def own_authorize(self, tree):
        """-> 'file:line' of a class named AuthorizeAttribute declared in the tree, or None."""
        key = ("own-authorize", id(tree))
        if key not in self.ctx_cache:
            self.ctx_cache[key] = None
            for p in tree.grep(["AuthorizeAttribute"], (".cs",)) if tree is not None else []:
                if TEST_PATH.search(p):
                    continue
                s2 = self.src(tree, p)
                m = re.search(r"\bclass\s+AuthorizeAttribute\b", s2.code if s2 else "")
                if m:
                    self.ctx_cache[key] = where(s2, m.start())
                    break
        return self.ctx_cache[key]

    def finish(self, tree, cov, test, path):
        if cov.short_circuit:
            # ShortCircuit runs the endpoint straight from routing: authorization middleware is skipped
            cov.unknown.append(f"`ShortCircuit()` at {cov.short_circuit[0]} runs the endpoint straight from routing, "
                               "so the authorization middleware and the fallback policy are skipped (with "
                               "authorization metadata, the request fails when it is routed)")
            return cov.state()
        if cov.items or cov.unknown or cov.overrides or cov.notes:
            return cov.state()
        kinds = ("fallback-policy", "fallback-unclear", "fallback-null", "fallback-conditional")
        per_root = []
        for root in (cov.roots or [path]):
            found, reasons = self.scoped(tree, kinds, test, root, ())
            by = {k: [c for c in found if c["kind"] == k] for k in kinds}
            at = lambda c: f"{c['file']}:{c['line']}"  # noqa: E731
            if by["fallback-unclear"]:
                return self.unknown_fallback(cov, [f"the fallback policy at {at(c)} is set to something this script "
                                                   "does not read as a policy or as null" for c in by["fallback-unclear"]])
            if by["fallback-conditional"]:
                return self.unknown_fallback(cov, [f"the fallback policy at {at(c)} may not be set: "
                                                   + c.get("reach", "it is set under a condition")
                                                   for c in by["fallback-conditional"]])
            if by["fallback-policy"] and by["fallback-null"]:
                return self.unknown_fallback(cov, [f"the fallback policy is set at {at(by['fallback-policy'][0])} and "
                                                   f"reset to null at {at(by['fallback-null'][0])}; which one wins is not read"])
            pol = [r for r in reasons if r.startswith("fallback-policy")]
            per_root.append((root, by["fallback-policy"][0] if by["fallback-policy"] else None, pol))
        hit = [x for x in per_root if x[1]]
        if hit and len(hit) < len(per_root):
            miss = [x[0] for x in per_root if not x[1]]
            return self.unknown_fallback(cov, [f"the fallback policy at {hit[0][1]['file']}:{hit[0][1]['line']} applies "
                                               f"to the app started from {hit[0][0]}, not to the one from {miss[0]}; "
                                               "this endpoint is mapped in both"])
        if hit:
            gaps = [g for x in hit for g in self.no_authz_middleware(tree, test, x[0])]
            if gaps:
                return self.unknown_fallback(cov, gaps)
            return cov.state(f"{hit[0][1]['file']}:{hit[0][1]['line']}")
        pol = [r for x in per_root for r in x[2]]
        if pol:
            return self.unknown_fallback(cov, pol[:2])
        return cov.state()

    def no_authz_middleware(self, tree, test, root):
        """The fallback policy is applied by the authorization middleware, which must run before
        UseEndpoints -> reasons when that is not seen. Checked in the app's project, in shared projects with
        no host of their own, and in every project when the endpoint's own project has no host."""
        out = []
        mine = self.project_of(tree, root)
        rs0 = self.src(tree, root)
        library = mine is None or rs0 is None or not self.host_builder(tree, rs0, test, mine)
        for p in tree.grep(["UseEndpoints", "UseMvc"], (".cs",)) if tree is not None else []:
            if TEST_PATH.search(p) and not test:
                continue
            pp = self.project_of(tree, p)
            if (p != root and pp != mine and not library
                    and (pp is None or self.host_builder(tree, self.src(tree, p), test, pp))):
                continue
            s2 = self.src(tree, p)
            code = s2.code if s2 else ""
            for m in re.finditer(r"\bUseMvc(?:WithDefaultRoute)?\s*\(", code):
                out.append(f"`{m.group(0).rstrip('( ')}` at {where(s2, m.start())} routes without endpoint routing; "
                           "the fallback policy is applied only by the authorization middleware with endpoint routing")
            minimal = re.search(r"\bWebApplication\s*\.\s*Create", code)
            for m in re.finditer(r"\bUseEndpoints\s*\(", code):
                meth = s2.method_at(m.start())
                a = meth["body"][0] if meth else 0
                if minimal and not re.search(r"\bUseAuthorization\s*\(", code):
                    # WebApplication adds the middleware itself, unless the app calls UseAuthorization() itself;
                    # a call elsewhere in the project (a helper of your own) is not followed
                    elsewhere = self.authz_elsewhere(tree, test, p, pp)
                    if elsewhere:
                        out.append(f"`UseAuthorization()` is called at {elsewhere}, outside the file with `UseEndpoints` "
                                   f"at {where(s2, m.start())}; whether it runs before `UseEndpoints` is not read, and "
                                   "without that middleware the fallback policy is not applied")
                    continue
                seen, ok = False, False
                for u in re.finditer(r"\.\s*UseAuthorization\s*\(", code[a:m.start()]):
                    q = a + u.start()
                    seen = True
                    rs = receiver_start(code, q)
                    k = stmt_before(code, (rs if rs is not None else q) - 1)
                    if ((k < a or code[k] in ";{}") and s2.block_of(q) == s2.block_of(m.start())
                            and not conditional_at(code, q)):
                        ok = True
                        break
                if not ok:
                    out.append((f"`UseAuthorization()` before `UseEndpoints` at {where(s2, m.start())} runs only under "
                                "a condition, `#if` or in a branch pipeline" if seen else
                                f"no `UseAuthorization()` statement is seen before `UseEndpoints` at "
                                f"{where(s2, m.start())}") + "; without that middleware the fallback policy is not applied")
        return out[:2]

    def authz_elsewhere(self, tree, test, path, project):
        """file:line of a UseAuthorization() call in another file of the same project, or None."""
        for q in tree.grep(["UseAuthorization"], (".cs",)) if tree is not None else []:
            if q == path or (TEST_PATH.search(q) and not test) or self.project_of(tree, q) != project:
                continue
            sq = self.src(tree, q)
            u = re.search(r"\bUseAuthorization\s*\(", sq.code) if sq else None
            if u:
                return where(sq, u.start())
        return None

    def unknown_fallback(self, cov, reasons):
        cov.unknown += reasons[:2]
        return cov.state()

    def conventional_routes(self, tree, cov, test, path):
        """Conventions on MapControllerRoute/MapDefaultControllerRoute reach only conventionally routed
        actions; when the routes in reach differ, which one serves an action is not read."""
        mounts = ("MapControllerRoute", "MapDefaultControllerRoute")
        hits, reasons = self.scoped(tree, [c for c in {x["kind"] for x in self.context(tree)} if c.startswith("mount-")],
                                    test, path, (), lambda c: c.get("mount") in mounts, foreign=True)
        cov.unknown += [f"{c['mount']} at {c['file']}:{c['line']} may not run: {c['reach']}"
                        for c in hits if c.get("reach")][:2]
        hits = [c for c in hits if not c.get("reach")]
        cov.unknown += [f"{c['mount']} at {c['file']}:{c['line']}: {c['unclear']}" for c in hits if c.get("unclear")][:2]
        hits = [c for c in hits if not c.get("unclear")]
        effects = {tuple(c["effect"]) for c in hits}
        if len(effects) > 1:
            cov.overrides.append("conventional routes differ in authorization ("
                                 + ", ".join(f"{c['file']}:{c['line']} {'+'.join(c['effect']) or 'none'}"
                                             for c in hits[:3]) + "); which one serves this action is not read")
        elif hits:
            for k in hits[0]["effect"]:
                cov.add(k, "mount", f"{hits[0]['file']}:{hits[0]['line']}", hits[0]["mount"])
        cov.unknown += [r for r in reasons if not r.startswith("mount-none")][:2]

    def mount_items(self, tree, cov, mounts, test, path):
        hits, reasons = self.scoped(tree, [c for c in {x["kind"] for x in self.context(tree)}
                                           if c.startswith("mount-") and c != "mount-none"],
                                    test, path, cov.via, lambda c: c.get("mount") in mounts,
                                    foreign="MapControllers" in mounts)
        for c in hits:
            if c.get("reach"):
                cov.unknown.append(f"{c['mount']} at {c['file']}:{c['line']} may not run: {c['reach']}")
                continue
            if c.get("unclear"):
                cov.unknown.append(f"{c['mount']} at {c['file']}:{c['line']}: {c['unclear']}")
                continue
            for k in c["effect"]:
                cov.add(k, "mount", f"{c['file']}:{c['line']}", c["mount"])
        cov.unknown += reasons[:2]

    # .................................................. endpoints of a file

    def endpoints(self, tree, src):
        if src is None:
            return []
        self._tree = tree
        if src.path.endswith(".cs"):
            return self.minimal(tree, src) + self.controllers(tree, src)
        if src.path.endswith((".razor", ".cshtml")):
            return self.pages(tree, src)
        return []

    def minimal(self, tree, src):
        out, code = [], src.code
        test = bool(TEST_PATH.search(src.path))
        for m in MAP_RE.finditer(code):
            name = m.group(1)
            if name == "MapGroup" or ENDPOINT_CALLS[name] == "MOUNT":
                continue
            dot = m.start()
            rs = receiver_start(code, dot)
            calls, end = chain_forward(src, dot)
            if not calls or calls[0][2] is None:
                continue
            args = split_args(code, *calls[0][2])
            cov = Cov()
            if rs is None:
                cov.unknown.append(f"receiver at {where(src, dot)} is not a name or a call chain")
                cov.prefix = None
            else:
                cov.merge(self.resolve_expr(tree, src, rs, dot, 0, frozenset()))
            if not cov.roots:
                cov.roots = [src.path]
            own = Cov()
            own.prefix = None
            self.classify(src, own, calls[1:], "endpoint")
            cov.items += own.items
            cov.short_circuit += own.short_circuit
            cov.overrides += own.overrides
            cov.other += [o for o in own.other if o not in cov.other]
            if rs is not None and (re.search(r"(?m)^[ \t]*#\s*(?:if|elif|else|endif)\b", code[rs:end])
                                   or re.match(r"\s*#\s*(?:if|elif|else|endif)\b", code[end:])):
                cov.overrides.append(f"a `#if` line sits in the chain at {where(src, rs)}; which conventions are "
                                     "compiled is not read")
            if rs is not None and re.search(r"(?:\breturn|=>)\s*$", code[max(0, rs - 12):rs]) \
                    and re.match(r"\s*;", code[end:]):
                meth = src.method_at(dot)
                arrow = re.search(r"=>\s*$", code[max(0, rs - 12):rs])
                if meth is not None and (not arrow or code[meth["body"][0]:rs].strip() == "=>"):
                    cov.overrides += self.caller_chains(src, meth, f"{name}(...)")
            if rs is not None:
                for bd in src.bindings():
                    if bd["expr"][0] <= rs and bd["expr"][1] >= end and not code[end:bd["expr"][1]].strip():
                        again = next((rm.start() for rm in re.finditer(
                            r"(?<![\w.])" + re.escape(bd["name"]) + r"\s*(?:\?\?)?=(?![=>])", code[bd["expr"][1]:bd["block"][1]])),
                            None)
                        stop = bd["expr"][1] + again if again is not None else bd["block"][1]
                        self.scan_uses(src, cov, bd["name"], bd["expr"][1], stop, bd["block"], "endpoint",
                                       lambda q, bd=bd: src.binding_at(bd["name"], q) is bd)
            method = ENDPOINT_CALLS[name]
            route = None
            if name in HANDLER_INDEX or name in ("MapHub", "MapHealthChecks", "MapFallbackToFile", "MapFallbackToPage",
                                                 "MapControllerRoute", "MapIdentityApi"):
                if name == "MapControllerRoute" and len(args) > 1:
                    lit = src.literal(*args[1])
                    route = lit if lit is not None else "{" + squash(src.orig[args[1][0]:args[1][1]], 30) + "}"
                elif args and not (name == "MapFallbackToFile" and len(args) == 1):
                    lit = src.literal(*args[0])
                    route = lit if lit is not None else "{" + squash(src.orig[args[0][0]:args[0][1]], 30) + "}"
            if name == "MapMethods" and len(args) > 1:
                method = ",".join(sorted(set(re.findall(r"\b(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b",
                                                        src.orig[args[1][0]:args[1][1]].upper())))) or "?"
            if name == "MapControllers" or name.startswith(("MapRazor", "MapBlazor", "MapDefaultController")):
                route = f"({name})"
            hidx = HANDLER_INDEX.get(name, 0 if name == "MapFallback" and len(args) == 1 else
                                     1 if name == "MapFallback" else None)
            handler_note = None
            if hidx is not None and hidx < len(args):
                handler_note = self.handler(src, cov, *args[hidx])
            if handler_note:
                cov.unknown.append(handler_note)
            if name in ("MapHub", "MapGrpcService"):
                self.service_class(tree, src, cov, m, name)
            full = compose(cov.prefix, route)
            out.append(dict(kind="minimal", call=name, method=method, route=full, file=src.path,
                            line=src.line(m.start(1)), span=(src.line(rs if rs is not None else dot), src.line(end)),
                            coverage=self.finish(tree, cov, test, src.path),
                            other_conventions=cov.other[:6], handler_checks=cov.checks[:4], via=cov.via[:3],
                            test_path=test))
        return out

    def derived_classes(self, tree, src, cls):
        """-> [(src, class)] classes whose base is `cls` (by name, same file or the tree)."""
        out, paths = [], {src.path}
        if tree is not None:
            paths |= set(tree.grep([cls["name"]], (".cs",)))
        for p in sorted(paths):
            s2 = src if p == src.path else self.src(tree, p)
            for c in (s2.classes() if s2 else []):
                if c is not cls and c["base"] == cls["name"] and (c.get("base_q") or c["base"]) in (cls["name"], cls["full"]) \
                        or c is not cls and c.get("base_q") and cls["full"].endswith("." + c["base_q"]):
                    out.append((s2, c))
        return out

    def service_class(self, tree, src, cov, m, name):
        """MapHub<T>/MapGrpcService<T>: the endpoint carries T's attributes (and, for gRPC, each
        service method's), so they are read like a controller's."""
        kind = "hub" if name == "MapHub" else "service"
        g = re.match(r"\s*<\s*(?:global::)?([\w.]+)\s*>", src.code[m.end(1):])
        if not g:
            cov.unknown.append(f"the {kind} class of `{name}` at {where(src, m.start(1))} is not read")
            return
        q = g.group(1)
        found = self.find_classes(tree, src, q.split(".")[-1])
        if "." in q:
            found = [(s2, c) for s2, c in found if c["full"] == q or c["full"].endswith("." + q)] or found
        if len({c["full"] for _, c in found}) != 1:
            cov.unknown.append(f"{kind} class `{q}` of `{name}` at {where(src, m.start(1))} is "
                               + ("not found" if not found else f"declared {len(found)} times")
                               + "; its attributes are not read")
            return
        for s2, c in found:
            if len(found) > 1 and "partial" not in c["mods"]:
                continue
            self.class_attrs(tree, s2, c, cov, 0, f"{kind} class")
            if kind != "service":
                continue
            code, (a, b) = s2.code, c["body"]
            done = set()
            for o in re.finditer(r"\[", code[a + 1:b]):
                o = a + 1 + o.start()
                k = stmt_before(code, o - 1)
                if k < a or code[k] not in ";{}]" or s2.block_of(o)[0] != a:
                    continue                    # not a member attribute of this class body
                d = o                           # the member declaration after the attribute lists
                while d < b:
                    if code[d].isspace():
                        d += 1
                    elif code[d] == "[":
                        e = match_fwd(code, d)
                        if e < 0:
                            break
                        d = e + 1
                    elif code[d] == "#":
                        d = code.find("\n", d) % (b + 1) or b
                    else:
                        break
                if d in done or re.match(r"(?:\w+\s+)*(?:class|struct|record|interface|enum|delegate)\b", code[d:b]):
                    continue                    # seen, or a nested type's attributes
                done.add(d)
                self.attr_items(cov, [x for x in attrs_before(s2, d)
                                      if x[0] != "Authorize" and x[0] not in self.local["auth"]],
                                "service method", where(s2, o))

    def find_classes(self, tree, src, name):
        """-> [(src, class)] declarations of `name`: this file's, else the tree's."""
        found = [(src, c) for c in src.classes() if c["name"] == name]
        if not found and tree is not None:
            for p in tree.grep(["class " + name], (".cs",)):
                s2 = self.src(tree, p)
                found += [(s2, c) for c in (s2.classes() if s2 else []) if c["name"] == name]
        return found

    def class_attrs(self, tree, src, cls, cov, depth, level):
        self.attr_items(cov, cls["attrs"], level, where(src, cls["name_pos"]))
        cov.routes += [route_or_unread(a) for n, a in cls["attrs"] if n == "Route"]
        base = cls["base"]
        if "partial" in cls["mods"]:
            if tree is None:
                cov.unknown.append(f"other parts of partial class {cls['name']} are not readable")
            else:
                mine = self.project_of(tree, src.path)
                parts = []
                for p in tree.grep(["class " + cls["name"]], (".cs",)):
                    s2 = src if p == src.path else self.src(tree, p)
                    for c in (s2.classes() if s2 else []):
                        if c["full"] == cls["full"] and "partial" in c["mods"] and not (s2 is src and c is cls) \
                                and self.project_of(tree, p) == mine:
                            parts.append((s2, c))
                for s2, c in parts:
                    self.attr_items(cov, c["attrs"], level + " (partial)", where(s2, c["name_pos"]))
                    cov.routes += [route_or_unread(a) for n, a in c["attrs"] if n == "Route"]
                    base = base or c["base"]
        if not base or base in BASE_TYPES or base.startswith("I") and base[1:2].isupper() or depth >= CALLER_DEPTH:
            return
        found = self.find_classes(tree, src, base)
        if not found and re.search(r"\b(\w+)\.\1Base$", cls.get("base_q") or ""):
            return                  # a gRPC base generated from a .proto file (Service.ServiceBase)
        if not found:
            cov.unknown.append(f"base class `{base}` of {cls['name']} is not found; its attributes are not read")
            return
        q = cls.get("base_q") or base
        if "." in q:
            found = [(s, c) for s, c in found if c["full"] == q or c["full"].endswith("." + q)] or found
        fulls = {c["full"] for _, c in found}
        if len(fulls) > 1 and "." not in q:
            near = [(s, c) for s, c in found if c["ns"] == cls["ns"]]
            if len({c["full"] for _, c in near}) == 1:
                found, fulls = near, {near[0][1]["full"]}
        if len(fulls) > 1 or len(found) > 1 and not all("partial" in c["mods"] for _, c in found):
            cov.unknown.append(f"base class `{q}` of {cls['name']} is declared {len(found)} times ("
                               + ", ".join(where(s, c["name_pos"]) for s, c in found[:3]) + "); which one is not read")
            return
        self.class_attrs(tree, found[0][0], found[0][1], cov, depth + 1, f"base {base}")

    def controllers(self, tree, src):
        out = []
        test = bool(TEST_PATH.search(src.path))
        for cls in src.classes():
            names = {n for n, _ in cls["attrs"]}
            body = src.code[cls["body"][0]:cls["body"][1]]
            mvc = "Microsoft.AspNetCore.Mvc" in src.orig or re.search(r"\[\s*(?:Http(?:Get|Post|Put|Delete|Patch)|Route)\b", body)
            is_ctrl = ("ApiController" in names or "Controller" in names or cls["base"] in ("Controller", "ControllerBase")
                       or bool(mvc) and (cls["name"].endswith("Controller") or (cls["base"] or "").endswith("Controller")))
            if not is_ctrl or "NonController" in names or "abstract" in cls["mods"] or "static" in cls["mods"]:
                continue
            base_cov = Cov()
            self.class_attrs(tree, src, cls, base_cov, 0, "controller")
            for s2, c in self.derived_classes(tree, src, cls):
                tmp = Cov()
                self.attr_items(tmp, c["attrs"], "controller", where(s2, c["name_pos"]))
                if any(i["kind"] == "anonymous" for i in tmp.items) or tmp.overrides:
                    base_cov.overrides.append(f"{c['name']} ({where(s2, c['name_pos'])}) derives from {cls['name']} and "
                                              "serves its actions under its own attributes, which allow anonymous "
                                              "access or are not read")
            hits, reasons = self.scoped(tree, ("global-authorize-filter",), test, src.path, (), foreign=True)
            for c in hits[:1]:
                if c.get("reach"):
                    base_cov.notes.append(f"global filter at {c['file']}:{c['line']} may not be added: {c['reach']}")
                else:
                    base_cov.add("authorize", "global filter", f"{c['file']}:{c['line']}")
            base_cov.notes += reasons[:2]
            hits, reasons = self.scoped(tree, ("global-anonymous-filter",), test, src.path, (), foreign=True)
            for c in hits[:1]:
                base_cov.add("anonymous", "global filter", f"{c['file']}:{c['line']}")
                if c.get("reach"):
                    base_cov.notes.append(f"global filter at {c['file']}:{c['line']} may not be added: {c['reach']}")
            base_cov.overrides += reasons[:2]
            hits, reasons = self.scoped(tree, ("other-authorize-filter", "other-anonymous-filter"), test, src.path, (),
                                        foreign=True)
            for c in (hits + [None])[:2]:
                if c is None:
                    break
                msg = (f"`{'AuthorizeFilter' if 'authorize' in c['kind'] else 'AllowAnonymousFilter'}` at "
                       f"{c['file']}:{c['line']} is not a plain `Filters.Add(new ...)` in AddControllers/AddMvc/"
                       "Configure<MvcOptions>; which controllers it applies to is not read")
                (base_cov.notes if "authorize" in c["kind"] else base_cov.overrides).append(msg)
            base_cov.notes += [r for r in reasons[:2] if "authorize" in r]
            base_cov.overrides += [r for r in reasons[:2] if "anonymous" in r]
            attr_cov, conv_cov = Cov(), Cov()
            self.mount_items(tree, attr_cov, ("MapControllers",), test, src.path)
            conv_cov.merge(attr_cov)
            self.conventional_routes(tree, conv_cov, test, src.path)
            prefix = base_cov.routes[0] if base_cov.routes else None
            short = cls["name"][:-10] if cls["name"].endswith("Controller") else cls["name"]
            for meth in src.methods():
                if not (cls["body"][0] < meth["name_pos"] < cls["body"][1]) or "public" not in meth["mods"] \
                        or "static" in meth["mods"] or meth["name"] == cls["name"]:
                    continue
                inner = [c for c in src.classes() if c is not cls and cls["body"][0] < c["body"][0] < meth["name_pos"] < c["body"][1]]
                if inner:
                    continue
                attrs = attrs_before(src, meth["start"])
                anames = [n for n, _ in attrs]
                if "NonAction" in anames:
                    continue
                verbs = [(n, a) for n, a in attrs if n in HTTP_ATTRS]
                method = ",".join(sorted({HTTP_ATTRS[n] or ("ANY" if n == "Route" else "?") for n, _ in verbs})) or "ANY"
                tmpl = next((attr_route(a) for n, a in verbs if attr_route(a) is not None), None)
                if tmpl is not None and tmpl.startswith(("/", "~/")):
                    route = "/" + tmpl.lstrip("~/")
                elif prefix is not None or tmpl is not None:
                    route = compose([prefix or ""], tmpl or "")
                else:
                    route = "(conventional)"
                route = route.replace("[controller]", short).replace("[action]", meth["name"])
                cov = Cov()
                cov.merge(base_cov)
                cov.merge(conv_cov if route == "(conventional)" else attr_cov)
                self.attr_items(cov, attrs, "endpoint", where(src, meth["name_pos"]))
                if "override" in meth["mods"] and cls["base"] not in BASE_TYPES:
                    cov.overrides.append(f"{meth['name']} at {where(src, meth['name_pos'])} overrides a method of "
                                         + (f"`{cls['base']}`" if cls["base"] else "a base class declared on another part")
                                         + "; MVC also applies that method's attributes, which are not read")
                self.body_checks(src, cov, *meth["body"])
                out.append(dict(kind="controller", call=f"{cls['name']}.{meth['name']}", method=method, route=route,
                                file=src.path, line=src.line(meth["name_pos"]),
                                span=(src.line(meth["start"]), src.line(meth["body"][1])),
                                attr_span=src.line(attrs_start(src, meth["start"])),
                                coverage=self.finish(tree, cov, test, src.path), other_conventions=[],
                                handler_checks=cov.checks[:4], via=[], test_path=test))
        return out

    def pages(self, tree, src):
        out = []
        test = bool(TEST_PATH.search(src.path))
        text = src.code
        pages = [(m.group(1), m.start()) for m in re.finditer(r"(?m)^\s*@page\s+\"([^\"]*)\"", text)]
        if not pages:
            if re.search(r"(?m)^\s*@page\s*$", text):
                pages = [("(by path)", re.search(r"(?m)^\s*@page", text).start())]
            else:
                return out
        cov = Cov()
        for m in re.finditer(r"(?m)^\s*@attribute\s+\[([^\]\n]*)\]", text):
            if any(ob < m.start() < (match_fwd(text, ob) % (len(text) + 1)) for ob in code_blocks(text)):
                continue
            if markup_depth(text, m.start()):
                cov.unknown.append(f"`@attribute` at {src.path}:{src.line(m.start(1))} is inside an element; "
                                   "whether it is a directive is not read")
                continue
            fake = Src(src.path, m.group(1))
            fake.code = m.group(1)
            self.attr_items(cov, parse_attr_list(fake, 0, len(m.group(1))), "page", f"{src.path}:{src.line(m.start(1))}")
        if tree is not None:
            behind = src.path + ".cs"
            bsrc = self.src(tree, behind) if behind in tree.files() or isinstance(tree, FsTree) and tree.read(behind) else None
            if bsrc is not None:
                if src.path.endswith(".razor"):
                    want = os.path.basename(src.path)[:-6]
                    cands = [c for c in bsrc.classes() if c["name"] == want]
                    how = f"class `{want}`"
                else:
                    mm = re.search(r"(?m)^\s*@model\s+([\w.]+)", text)
                    want = mm.group(1).split(".")[-1] if mm else None
                    cands = [c for c in bsrc.classes() if c["name"] == want] if want else \
                        [c for c in bsrc.classes() if c["base"] == "PageModel"]
                    how = f"`@model {want}`" if want else "a `PageModel` class"
                if len(cands) == 1:
                    self.class_attrs(tree, bsrc, cands[0], cov, 0, "code-behind")
                elif bsrc.classes():
                    cov.unknown.append(f"code-behind {behind} has {len(cands)} classes matching {how}; "
                                       "its attributes are not read")
            if src.path.endswith(".razor"):
                d = os.path.dirname(src.path)
                while True:
                    imp = (d + "/" if d else "") + "_Imports.razor"
                    t = tree.read(imp)
                    if t:
                        t = clean_markup(t)
                        for m in re.finditer(r"(?m)^\s*@attribute\s+\[([^\]\n]*)\]", t):
                            fake = Src(imp, m.group(1))
                            fake.code = m.group(1)
                            ln = t.count("\n", 0, m.start(1)) + 1
                            self.attr_items(cov, parse_attr_list(fake, 0, len(m.group(1))), "imports", f"{imp}:{ln}")
                    if not d:
                        break
                    d = os.path.dirname(d)
                self.mount_items(tree, cov, ("MapRazorComponents",), test, src.path)
            else:
                d = os.path.dirname(src.path)
                while True:
                    imp = (d + "/" if d else "") + "_ViewImports.cshtml"
                    v = tree.read(imp)
                    if v:
                        v = clean_markup(v)
                        for m in re.finditer(r"(?m)^\s*@attribute\s+\[([^\]\n]*)\]", v):
                            cov.overrides.append(f"`@attribute [{squash(m.group(1), 40)}]` at {imp}:"
                                                 f"{v.count(chr(10), 0, m.start(1)) + 1}; whether it reaches this page "
                                                 "is not read")
                    if not d:
                        break
                    d = os.path.dirname(d)
                self.mount_items(tree, cov, ("MapRazorPages",), test, src.path)
                conv = [c for c in self.context(tree) if c["kind"] == "razor-pages-convention" and (test or not c["test_path"])]
                if conv:
                    cov.unknown.append(f"page conventions at {conv[0]['file']}:{conv[0]['line']} are not matched to paths")
        else:
            cov.unknown.append("other files of the tree are not readable (_Imports.razor, code-behind)")
        for route, pos in pages:
            ln = src.line(pos)
            out.append(dict(kind="page", call="@page", method="PAGE", route=route, file=src.path, line=ln, span=(ln, ln),
                            coverage=self.finish(tree, cov, test, src.path), other_conventions=[], handler_checks=[],
                            via=[], test_path=test))
        return out


def attrs_start(src, pos):
    code, j, start = src.code, pos - 1, pos
    while True:
        while j >= 0 and code[j].isspace():
            j -= 1
        if j < 0 or code[j] != "]":
            return start
        o = match_back(code, j)
        if o < 0:
            return start
        start, j = o, o - 1


def route_or_unread(args):
    r = attr_route(args)
    return r if r is not None else "{...}"


def compose(prefix, route):
    if prefix is None:
        return "?/" + (route or "").lstrip("/") if route is not None else "?"
    if route is None:
        return "/".join(prefix) if prefix else None
    if route.startswith("("):
        return route
    segs = [s.strip("/") for s in list(prefix) + [route]]
    joined = "/".join(s for s in segs if s)
    return "/" + joined


# ------------------------------------------------------------------ line facts

def comment_line(text):
    t = text.strip()
    return t.startswith(("//", "/*", "*", "@*", "<!--")) or not t


def line_facts(local, path, lines, side):
    """lines: {n: text} of one side -> facts for every token category."""
    facts = []
    if path.endswith(".json"):
        cats = CONFIG_TOKENS
    else:
        cats = dict(LINE_TOKENS)
        if local["auth"]:
            cats["auth-removed"] = cats["auth-removed"] + [(c, r"\b" + re.escape(c) + r"\s*\(") for c in sorted(local["auth"])]
        if local["anon"]:
            cats["anonymous-added"] = cats["anonymous-added"] + [(c, r"\b" + re.escape(c) + r"\s*\(") for c in sorted(local["anon"])]
    for cat, toks in cats.items():
        if cat == "auth-removed" and side != "removed" or cat == "anonymous-added" and side != "added":
            continue
        last = None
        for n, text in sorted(lines.items()):
            if comment_line(text):
                continue
            hit = [name for name, pat in toks if re.search(pat, text)]
            if not hit:
                continue
            if last is not None and n - last["end_line"] <= 2:
                f = last                                   # a run of nearby lines is one fact
                f["tokens"] += [h for h in hit if h not in f["tokens"]]
                f["end_line"] = n
            else:
                f = dict(id=cat, file=path, line=n, end_line=n, side=side, tokens=hit)
                facts.append(f)
                last = f
            if re.search(r"=\s*false\b", text) and cat == "authn-scheme-changed":
                f.setdefault("sets_false", []).append(n)
            pm = re.search(r"\bAdd(?:Default)?Policy\s*\(\s*(?:\w+\s*:\s*)?\"([^\"]{1,60})\"", text)
            if pm:
                f.setdefault("policies", []).append(pm.group(1))
    return facts


# ------------------------------------------------------------------ change sources

def relevant(path):
    return path.endswith(SOURCE_EXT) or bool(APPSETTINGS.search(path))


def range_changes(root, base, head, everything):
    head_c = resolve(root, head)
    if everything:
        base_c, mb = EMPTY_TREE, None
    else:
        base_c = resolve(root, base)
        r = git(root, "merge-base", base_c, head_c)
        mb = r.stdout.decode().strip() if r.returncode == 0 else None
    frm = mb or base_c
    raw = git_ok(root, "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-U0", "-M",
                 frm, head_c, "--", "*.cs", "*.razor", "*.cshtml", "*.json").decode("utf-8", "replace")
    changes = [f for f in parse_diff(raw) if relevant(f["path"]) or relevant(f["old_path"] or "")]
    head_t, base_t = GitTree(root, head_c), GitTree(root, frm)
    heads = head_t.read_many([f["path"] for f in changes if f["status"] != "D"])
    bases = base_t.read_many([f["old_path"] for f in changes if f["status"] not in ("A",) and f["old_path"]])
    for f in changes:
        f["head_text"] = heads.get(f["path"]) if f["status"] != "D" else None
        f["base_text"] = bases.get(f["old_path"]) if f["status"] != "A" and f["old_path"] else None
        f["blob"] = head_t.blob(f["path"]) if f["status"] != "D" else None
        f["base_blob"] = base_t.blob(f["old_path"]) if f["old_path"] and f["status"] != "A" else None
    rev = dict(mode="all" if everything else "range", base=None if everything else base, base_commit=base_c,
               merge_base=mb, head=head, head_commit=head_c, diff_from=frm)
    return rev, changes, head_t, (None if everything else base_t), []


def sparse(lines):
    if not lines:
        return ""
    top = max(lines)
    return "\n".join(lines.get(i, "") for i in range(1, top + 1)) + "\n"


def diff_changes(root, path):
    try:
        with open(path, "rb") as fh:
            raw = fh.read()
    except OSError as e:
        raise UsageError(f"cannot read --diff {path}: {e.strerror}")
    tree = FsTree(root)
    limits, changes = [], []
    text = raw.decode("utf-8", "replace")
    parsed = parse_diff(text)
    if not parsed and text.strip():
        raise UsageError(f"--diff {path}: no file section found (expected a unified diff with ---/+++ headers)")
    for f in parsed:
        if not (relevant(f["path"]) or relevant(f["old_path"] or "")):
            continue
        head = tree.read(f["path"]) if f["status"] != "D" else None
        if f["status"] != "D" and head is not None:
            hl = head.split("\n")
            if any(n > len(hl) or hl[n - 1] != t for n, t in f["added"].items()):
                limits.append(f"{f['path']}: the file under --root does not match the diff's head; read the diff's lines only")
                head = None
        if f["status"] != "D" and head is None:
            if not any(x.startswith(f"{f['path']}:") for x in limits):
                limits.append(f"{f['path']}: not under --root; read the diff's lines only")
            head = sparse({**f["context"], **f["added"]})
        base = None
        if f["status"] == "D":
            base = sparse({**f["removed"]})
        elif f["status"] != "A":
            hl = head.split("\n")
            kept = [t for i, t in enumerate(hl, 1) if i not in f["added"]]
            total = len(kept) + len(f["removed"])
            out, it = [], iter(kept)
            for i in range(1, total + 1):
                out.append(f["removed"][i] if i in f["removed"] else next(it, ""))
            base = "\n".join(out)
        f["head_text"], f["base_text"] = head, base
        f["blob"], f["base_blob"] = (tree.blob(f["path"]) if f["status"] != "D" else None), None
        changes.append(f)
    limits.append("diff mode: the base is rebuilt from the head and the diff; callers, base classes and context "
                  "are read from the tree under --root for both sides")
    rev = dict(mode="diff", diff=path, diff_sha256=hashlib.sha256(raw).hexdigest())
    return rev, changes, tree, tree, limits


# ------------------------------------------------------------------ comparison

def sig(cov):
    st = cov["state"]
    lv = sorted({l["level"] for l in cov.get("levels", [])})
    return st + ("(" + ",".join(lv) + ")" if lv else "")


def show_cov(cov):
    st = cov["state"]
    if st in ("authorized", "anonymous"):
        parts = []
        for l in cov["levels"][:3]:
            parts.append(l["level"] + (f" {l['detail']}" if l.get("detail") else "") + f" {l['at']}")
        s = f"{st}({'; '.join(parts)})"
        if cov.get("also_unknown"):
            s += f"; also unknown: {cov['also_unknown'][0]}"
        return s
    if st == "unknown":
        return "unknown(" + "; ".join(cov["reasons"][:2]) + ")"
    if st == "fallback":
        return f"fallback(policy at {cov['at']})"
    return "none-visible"


def key_of(ep):
    return (ep["kind"], ep["call"] if ep["kind"] != "minimal" else "", ep["method"], ep["route"])


def compare(an, changes, head_t, base_t):
    facts, files = [], []
    for f in changes:
        files.append(dict(path=f["path"], old_path=f["old_path"], status=f["status"], blob=f.get("blob"),
                          base_blob=f.get("base_blob")))
        test = bool(TEST_PATH.search(f["path"]))
        lf = line_facts(an.local, f["path"], f["added"], "added")
        lf += line_facts(an.local, f["old_path"] or f["path"], f["removed"], "removed")
        for x in lf:
            x["test_path"] = test
        facts += lf
        if not f["path"].endswith(SOURCE_EXT):
            continue
        hs = an.src(head_t, f["path"], f["head_text"]) if f["head_text"] is not None else None
        bs = an.src(base_t, f["old_path"], f["base_text"]) if f["base_text"] is not None and base_t is not None else None
        if f["head_text"] is not None and bs is None and f["base_text"] is not None:
            bs = Src(f["old_path"], f["base_text"])
        heps = an.endpoints(head_t, hs)
        beps = an.endpoints(base_t, bs) if bs is not None else []
        bmap = {}
        for e in beps:
            bmap.setdefault(key_of(e), []).append(e)
        added = set(f["added"])
        removed = set(f["removed"])
        for e in heps:
            lst = bmap.get(key_of(e))
            lo = min(e["span"][0], e.get("attr_span", e["span"][0]))
            touched = any(lo <= n <= e["span"][1] for n in added)
            if lst:
                b = lst.pop(0)
                blo = min(b["span"][0], b.get("attr_span", b["span"][0]))
                btouched = any(blo <= n <= b["span"][1] for n in removed)
                cov_changed = sig(b["coverage"]) != sig(e["coverage"])
                if touched or btouched or cov_changed:
                    x = endpoint_fact("endpoint-changed", e)
                    x["changed"] = [r for r, on in (("lines", touched or btouched), ("coverage", cov_changed)) if on]
                    if cov_changed:
                        x["coverage_before"] = b["coverage"]
                    facts.append(x)
            else:
                facts.append(endpoint_fact("endpoint-added", e))
        for lst in bmap.values():
            for b in lst:
                x = endpoint_fact("endpoint-removed", b)
                x["side"] = "removed"
                facts.append(x)
    return facts, files


GLOBAL_LINE = re.compile(r"\b(?:FallbackPolicy|SetFallbackPolicy|AuthorizeFilter|MapControllers|MapControllerRoute|"
                         r"MapDefaultControllerRoute|MapRazorPages|MapRazorComponents|MapBlazorHub|AuthorizeFolder|"
                         r"AuthorizePage|AllowAnonymousToFolder|AllowAnonymousToPage)\b")
ROUTE_BUILDER = ("this IEndpointRouteBuilder", "this RouteGroupBuilder", "this WebApplication",
                 "this IEndpointConventionBuilder", "this IApplicationBuilder")
CALLED = re.compile(r"\b(\w+)\s*(?:<[^<>(){};]*>)?\s*\(")


def ripple(an, changes, head_t, base_t):
    """Endpoints in files the change does not touch whose coverage it changes (a group, a base class,
    _Imports.razor or the global setup edited elsewhere) -> endpoint-changed facts with changed=[coverage]."""
    changed = {f["path"] for f in changes} | {f["old_path"] for f in changes if f["old_path"]}
    texts = []
    for f in changes:
        for side in ("head_text", "base_text"):
            if f.get(side) and f["path"].endswith(".cs"):
                texts.append(clean(f[side])[0])
    edited = [t for f in changes for t in list(f["added"].values()) + list(f["removed"].values())
              if f["path"].endswith(SOURCE_EXT) and not comment_line(t)]
    paths = set()
    def ctx_sig(tree):
        return sorted((c["kind"], c["file"], c.get("mount") or "", c.get("reach") is None) for c in an.context(tree))
    if any(GLOBAL_LINE.search(t) for t in edited) or ctx_sig(head_t) != ctx_sig(base_t):
        for t in (head_t, base_t):
            paths |= set(t.grep([".Map", "Controller", "@page"], SOURCE_EXT))
    else:
        called = set()
        for t in texts:
            called |= set(CALLED.findall(t))
        seen_files = set()
        for _ in range(CALLER_DEPTH):
            new = set()
            for tree in (head_t, base_t):
                for p in tree.grep(list(ROUTE_BUILDER), (".cs",)):
                    s2 = an.src(tree, p)
                    if s2 is None or p in seen_files:
                        continue
                    if any(m["is_extension"] and m["name"] in called for m in s2.methods()):
                        new.add(p)
            new -= seen_files
            if not new:
                break
            seen_files |= new
            for p in new:
                s2 = an.src(head_t, p) or an.src(base_t, p)
                if s2:
                    called |= set(CALLED.findall(s2.code))
        paths |= seen_files
        classes = set()
        for f in changes:
            for side, tree in (("head_text", head_t), ("base_text", base_t)):
                if f.get(side) and f["path"].endswith(".cs"):
                    classes |= {c["name"] for c in Src(f["path"], f[side]).classes()}
        if classes:
            for tree in (head_t, base_t):
                for p in tree.grep([": " + c for c in sorted(classes)], (".cs",)):
                    paths.add(p)
        for f in changes:
            for imports, ext in (("_Imports.razor", ".razor"), ("_ViewImports.cshtml", ".cshtml")):
                if f["path"].endswith(imports):
                    d = os.path.dirname(f["path"])
                    for tree in (head_t, base_t):
                        paths |= {p for p in tree.files() if p.endswith(ext) and (not d or p.startswith(d + "/"))}
    facts = []
    for p in sorted(paths - changed):
        if not p.endswith(SOURCE_EXT):
            continue
        heps = an.endpoints(head_t, an.src(head_t, p))
        beps = {}
        for e in an.endpoints(base_t, an.src(base_t, p)):
            beps.setdefault(key_of(e), []).append(e)
        for e in heps:
            lst = beps.get(key_of(e))
            if lst:
                b = lst.pop(0)
                if sig(b["coverage"]) != sig(e["coverage"]):
                    x = endpoint_fact("endpoint-changed", e)
                    x["changed"] = ["coverage"]
                    x["coverage_before"] = b["coverage"]
                    facts.append(x)
    return facts


def endpoint_fact(fid, e):
    x = dict(id=fid, file=e["file"], line=e["line"], kind=e["kind"], call=e["call"], method=e["method"],
             route=e["route"], coverage=e["coverage"], test_path=e["test_path"])
    for k in ("other_conventions", "handler_checks", "via"):
        if e.get(k):
            x[k] = e[k]
    return x


# ------------------------------------------------------------------ report

def parse_local(path, explicit):
    conv = dict(auth=set(), anon=set(), checks=set())
    if not os.path.isfile(path):
        if explicit:
            raise UsageError(f"local file not found: {path}")
        return conv, None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = re.match(r"^\s*(?:[-*]\s*)?(auth-convention|anonymous-convention|handler-check):\s*`?(\w+)`?", line)
            if m:
                conv[{"auth-convention": "auth", "anonymous-convention": "anon", "handler-check": "checks"}[m.group(1)]].add(m.group(2))
    return conv, path


def report(rev, files, facts, context, limits, local, local_src):
    order = {k: i for i, k in enumerate(FACT_IDS)}
    facts.sort(key=lambda f: (bool(f.get("test_path")), order[f["id"]], f["file"], f["line"]))
    counts = {k: 0 for k in FACT_IDS}
    for f in facts:
        counts[f["id"]] += 1
    cov_counts = {}
    for f in facts:
        if f["id"].startswith("endpoint-") and f["id"] != "endpoint-removed":
            st = f["coverage"]["state"] + (" (also unknown)" if f["coverage"].get("also_unknown") else "")
            cov_counts[st] = cov_counts.get(st, 0) + 1
    return dict(tool="authz-diff", schema=SCHEMA, revision=rev, files=files, counts=counts, coverage_counts=cov_counts,
                facts=facts, context=context, limits=limits,
                local=dict(source=local_src, auth_conventions=sorted(local["auth"]),
                           anonymous_conventions=sorted(local["anon"]), handler_checks=sorted(local["checks"])))


def markdown(rep, max_lines):
    rev, facts = rep["revision"], rep["facts"]
    n = len(facts)
    out = [f"# authz-diff facts ({len(rep['files'])} files, {n} fact{'' if n == 1 else 's'})", "",
           "Facts only; the reviewer judges what they mean for this change.", ""]
    if rev["mode"] == "range":
        out.append(f"Revision: `{rev['diff_from']}`" + (" (merge base)" if rev.get("merge_base") else "")
                   + f" to `{rev['head_commit']}`; blob ids are in --json")
    elif rev["mode"] == "all":
        out.append(f"Revision: every endpoint at `{rev['head_commit']}`, listed as added")
    else:
        out.append(f"Revision: diff `{rev['diff']}` (sha256 `{rev['diff_sha256'][:12]}`), head read from --root")
    if rep["coverage_counts"]:
        out.append("Endpoint coverage: " + ", ".join(f"{k} {v}" for k, v in sorted(rep["coverage_counts"].items())))
    tail = []
    ctx = [c for c in rep["context"] if not c["test_path"]]
    if ctx:
        tail += ["", "Context at the head:"]
        tail += [f"- {c['kind']}" + (f" ({c['mount']})" if c.get("mount") else "") + f" `{c['file']}:{c['line']}`"
                 for c in ctx[:5]]
        if len(ctx) > 5:
            tail.append(f"- ... {len(ctx) - 5} more in --json")
    if rep["limits"]:
        tail += ["", "Limits:"] + [f"- {l}" for l in rep["limits"][:4]]
        if len(rep["limits"]) > 4:
            tail.append(f"- ... {len(rep['limits']) - 4} more in --json")
    lines = []
    for f in facts:
        loc = f"`{f['file']}:{f['line']}`" + (" (base)" if f.get("side") == "removed" else "")
        tp = " (test path)" if f.get("test_path") else ""
        if f["id"].startswith("endpoint-"):
            s = f"- {f['id']} {loc} `{f['method']} {f['route']}`: {show_cov(f['coverage'])}"
            if f.get("coverage_before"):
                s += f" (was {show_cov(f['coverage_before'])})"
            if f.get("other_conventions"):
                s += "; other conventions: " + ", ".join(f["other_conventions"][:3])
            if f.get("handler_checks"):
                s += "; handler calls " + ", ".join(f["handler_checks"][:2])
            lines.append(s + tp)
        else:
            if f.get("end_line", f["line"]) != f["line"]:
                loc = loc.replace(f":{f['line']}`", f":{f['line']}-{f['end_line']}`", 1)
            extra = (" policy " + ", ".join(f"`{x}`" for x in f["policies"][:3]) if f.get("policies") else "") \
                + (" (= false at line " + ", ".join(map(str, f["sets_false"][:3])) + ")" if f.get("sets_false") else "")
            toks = f["tokens"][:6] + ([f"{len(f['tokens']) - 6} more"] if len(f["tokens"]) > 6 else [])
            lines.append(f"- {f['id']} {loc} {f['side']}: {', '.join(toks)}{extra}{tp}")
    budget = max_lines - len(out) - len(tail) - 2
    if lines:
        if len(lines) > budget:
            keep = max(budget - 1, 0)
            lines = lines[:keep] + [f"- ... {n - keep} more in --json"]
        out += ["", "Facts:"] + lines
    else:
        out += ["", "No endpoint, authorization, CORS or authentication change found."]
    out += tail
    if len(out) > max_lines:
        out = out[:max_lines - 1] + ["... truncated; the full report is in --json"]
    return "\n".join(out) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Endpoint and authorization changes in an ASP.NET Core change, as facts for a reviewer (no verdicts).",
        epilog="Default: the change between --base and --head. Prints a markdown summary; --json prints the full "
               "report. Exit 0 when the facts were computed, 2 on a usage error or a git error.")
    ap.add_argument("--root", default=".", help="repository root (for --diff: the tree at the diff's head)")
    ap.add_argument("--base", default="origin/main", help="range start (default origin/main; the merge base with --head is used)")
    ap.add_argument("--head", default="HEAD", help="range end (default HEAD)")
    ap.add_argument("--all", action="store_true", help="list every endpoint at --head, as if added")
    ap.add_argument("--diff", help="read this unified diff instead of a git range; the head is the tree under --root")
    ap.add_argument("--local", help="local conventions file (default references/local.md in the skill)")
    ap.add_argument("--json", action="store_true", help="print the full JSON report instead of markdown")
    ap.add_argument("--out", help="also write the markdown summary to this file (review evidence)")
    ap.add_argument("--json-out", help="also write the JSON report to this file (review evidence)")
    ap.add_argument("--max-lines", type=int, default=60, help="markdown summary line cap (default 60)")
    a = ap.parse_args(argv)
    root = os.path.realpath(a.root)
    try:
        if not os.path.isdir(root):
            raise UsageError(f"--root is not a directory: {a.root}")
        if a.all and a.diff:
            raise UsageError("pass at most one of --all and --diff")
        local, local_src = parse_local(a.local or DEFAULT_LOCAL, bool(a.local))
        if a.diff:
            rev, changes, head_t, base_t, limits = diff_changes(root, a.diff)
        else:
            if not is_git(root):
                raise UsageError(f"{a.root} is not a git checkout; pass --diff FILE with the tree at its head")
            rev, changes, head_t, base_t, limits = range_changes(root, a.base, a.head, a.all)
        an = Analyzer(local)
        facts, files = compare(an, changes, head_t, base_t)
        if base_t is not None and base_t is not head_t:
            facts += ripple(an, changes, head_t, base_t)
        elif rev["mode"] == "diff":
            limits.append("diff mode: coverage changes of endpoints in files the diff does not touch are not computed")
        for f in changes:
            if f["status"] != "D" and f["head_text"] is None and f["path"].endswith(SOURCE_EXT):
                limits.append(f"{f['path']}: binary, too large or not a regular file at the head; not read")
        context = an.context(head_t)
    except UsageError as e:
        print(f"authz-diff: {e}", file=sys.stderr)
        return 2
    if local_src and os.path.abspath(local_src).startswith(SKILL_DIR + os.sep):
        local_src = os.path.relpath(os.path.abspath(local_src), SKILL_DIR).replace(os.sep, "/")
    if rev["mode"] == "all":
        files = [x for x in files if any(f["file"] == x["path"] for f in facts)]
    rep = report(rev, files, facts, context, limits, local, local_src)
    md = markdown(rep, max(a.max_lines, 12))
    for path in (a.out, a.json_out):   # the evidence directory may not exist yet
        if path and os.path.dirname(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as fh:
            fh.write(md)
    if a.json_out:
        with open(a.json_out, "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=1)
            fh.write("\n")
    if a.json:
        json.dump(rep, sys.stdout, indent=1)
        sys.stdout.write("\n")
    else:
        sys.stdout.write(md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
