#!/usr/bin/env python3
"""Compute dependency-license and vendored-file facts for a reviewer. It reports what changed and
which license each dependency declares; it never says whether that is acceptable (the reviewer
judges).

Modes:
  --base REF [--head REF]   dependencies added, removed, version-changed or scope-changed (same
                            versions, but added to or removed from a manifest, or a changed
                            dev-only or direct flag) between the merge base of REF and --head
                            (default HEAD), and files added in that range
  --all [--head REF]        every dependency and vendored file at --head; in a directory that is
                            not a git checkout, the files on disk

Manifests: npm package-lock.json / npm-shrinkwrap.json; NuGet packages.lock.json,
Directory.Packages.props, *.csproj/*.fsproj/*.vbproj and Directory.Build.props/.targets
PackageReference; Python requirements*.txt and uv.lock. Other lockfiles (yarn, pnpm, poetry,
Pipfile) are listed under `limits`, not parsed.

Licenses are resolved offline only; nothing touches the network:
  npm     the lockfile entry's `license`, else node_modules/<pkg>/package.json on disk when its
          version matches the lockfile
  NuGet   the cached nuspec (<license> expression, or a licenses.nuget.org/<expression> URL) in
          --nuget-cache, $NUGET_PACKAGES or ~/.nuget/packages
  Python  License-Expression / License in a matching .venv or venv dist-info METADATA
A license that cannot be resolved this way is `unknown`; it is never inferred.

Classes (per dependency, against the allow/review/deny lists in references/local.md or --policy):
  allowed, review, denied, unlisted (an id on no list), unknown (not resolved), unparsed (not an
  SPDX expression), unclassified (no policy). SPDX expressions: OR takes the best option, AND the
  worst; `X WITH exception` is looked up as written, else takes X's class, with denied lowered to
  review (an exception only adds permissions). A license matching several lists takes the
  strictest. `*-packages:` lines classify a package by name and override its license class.

Fact ids: license-denied, license-review, license-unlisted, license-unknown, license-unparsed,
vendored-without-license (a font, 3D model, texture, audio file or any file under a vendor
directory, added or moved with no license file in its directory or an ancestor below the repo root; a
license file found in an ancestor, such as a project's own LICENSE, is reported with
license_file_at "parent" and listed in the summary, not raised as a fact).

Revision: the report names the base, merge base and head commits and each manifest's git blob id,
so evidence can be matched to the head under review.

Output: a markdown summary (at most --max-lines lines), or --json. Exit 0 when the scan ran
(whatever it found), 2 on a usage error, an unknown ref or an unreadable path.
"""
import argparse
import fnmatch
import hashlib
import json
import os
import re
import subprocess
import sys

try:
    import tomllib
except ImportError:                                     # Python < 3.11
    tomllib = None

SCHEMA = 1
SKILL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_POLICY = os.path.join(SKILL_DIR, "references", "local.md")
# Report order: the most direct conflict first (ordering only; the reviewer judges).
CHECKS = ("license-denied", "license-review", "license-unlisted", "license-unknown", "license-unparsed",
          "vendored-without-license")
CLASS_CHECK = {"denied": "license-denied", "review": "license-review", "unlisted": "license-unlisted",
               "unknown": "license-unknown", "unparsed": "license-unparsed"}
CLASSES = ("denied", "review", "unlisted", "unknown", "unparsed", "unclassified", "allowed")
RANK = {"allowed": 0, "review": 1, "unlisted": 2, "denied": 3}   # OR takes the lowest, AND the highest
POLICY_KEYS = ("allow", "review", "deny", "allow-packages", "review-packages", "deny-packages",
               "vendored-dirs", "vendored-extensions", "not-vendored", "license-files")

UNPARSED_LOCKS = ("yarn.lock", "pnpm-lock.yaml", "poetry.lock", "Pipfile.lock", "paket.lock", "bun.lockb")
NUGET_PROJECT = re.compile(r"(?:\.(?:cs|fs|vb)proj|^Directory\.Build\.(?:props|targets))$")

FONT_EXT = (".ttf", ".otf", ".woff", ".woff2", ".eot", ".ttc")
MODEL_EXT = (".blend", ".fbx", ".obj", ".glb", ".gltf", ".dae", ".3ds", ".stl", ".ply", ".usd", ".usda",
             ".usdc", ".usdz", ".abc", ".max", ".ma", ".mb")
TEXTURE_EXT = (".hdr", ".exr", ".tga", ".dds", ".ktx", ".ktx2", ".psd")
AUDIO_EXT = (".wav", ".ogg", ".mp3", ".flac", ".opus", ".aif", ".aiff", ".m4a", ".aac")
VENDOR_DIRS = ("vendor", "vendored", "third_party", "third-party", "thirdparty", "3rdparty", "external",
               "extern")
LICENSE_WORD = (r"licen[cs]es?|copying\d*|notices?|copyright|unlicense|ofl|attributions?|credits|thirdpartynotices|"
                r"eula|fontlog|acknowledge?ments?")
# What may follow (or precede) the license word in a name: a license id, a version, or a few plain words,
# as in LICENSE-MIT, MIT-LICENSE, LICENSE-2.0, COPYING.LESSER, THIRD-PARTY-NOTICES. Anything else
# (credits_theme.m4a, license-faq.pdf, license_check.gd) is not a license file.
LICENSE_TAG = (r"\d+|v\d+|mit|bsd\w*|isc|[al]?gpl\w*|mpl\w*|apache\w*|lesser|lib|ofl\w*|sil|cc\w*|zlib|eupl\w*|epl\w*|"
               r"bsl\w*|psf|python|artistic\w*|w3c|x11|wtfpl|unicode|ufl|ipa|ms|pl|rl|third|party|thirdparty|3rdparty|"
               r"full|text|en|all|summary|deps|dependencies|and|or|exceptions?|runtime|" + LICENSE_WORD)
TEXT_EXT = re.compile(r"\.(?:txt|md|rst|html?|markdown|pdf)$", re.I)
SKIP_DIRS = {".git", "node_modules", ".venv", "venv", "__pycache__", "bin", "obj"}


def is_license_file(name):
    """A license or notice file by name (LICENSE, COPYING.LESSER, MIT-LICENSE.txt, OFL.txt, a
    `<file>.license` sidecar); never a media or code file such as Copyright-Sans.ttf."""
    low = name.lower()
    if low.endswith((".license", ".licence")):
        return True
    stem = TEXT_EXT.sub("", low)
    tokens = [t for t in re.split(r"[-_. ]+", stem) if t]
    if not tokens or not any(re.fullmatch(LICENSE_WORD, t) for t in tokens):
        return False
    return all(re.fullmatch(LICENSE_TAG, t) for t in tokens)


# ------------------------------------------------------------------ SPDX

TOKEN = re.compile(r"\s*(\(|\)|[A-Za-z0-9.+:_-]+)")


def parse_spdx(text):
    """-> ('or'|'and', [nodes]) / ('lic', id, exception|None). Raises ValueError."""
    toks, pos, text = [], 0, text.strip()
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m or not m.group(1):
            raise ValueError(f"unexpected text at {text[pos:]!r}")
        toks.append(m.group(1))
        pos = m.end()
        while pos < len(text) and text[pos].isspace():
            pos += 1
    i = 0

    def peek():
        return toks[i].upper() if i < len(toks) else None

    def take():
        nonlocal i
        i += 1
        return toks[i - 1]

    def expr(op, sub):
        nodes = [sub()]
        while peek() == op:
            take()
            nodes.append(sub())
        return nodes[0] if len(nodes) == 1 else (op.lower(), nodes)

    def atom():
        if peek() is None:
            raise ValueError("expression ends early")
        if peek() == "(":
            take()
            node = expr("OR", lambda: expr("AND", atom))
            if peek() != ")":
                raise ValueError("unclosed parenthesis")
            take()
            return node
        if peek() in ("AND", "OR", "WITH", ")"):
            raise ValueError(f"unexpected {toks[i]!r}")
        lic = take()
        if peek() == "WITH":
            take()
            if peek() in (None, "AND", "OR", "WITH", "(", ")"):
                raise ValueError("WITH needs an exception id")
            return ("lic", lic, take())
        return ("lic", lic, None)

    node = expr("OR", lambda: expr("AND", atom))
    if i != len(toks):
        raise ValueError(f"unexpected {toks[i]!r}")
    return node


def lookup(policy, text):
    """The strictest list whose glob matches `text`, or None. A `X WITH Y` text matches only list
    items that are themselves `WITH` pairs, and a plain id only plain items."""
    pair = " with " in text.lower()
    for cls, key in (("denied", "deny"), ("review", "review"), ("allowed", "allow")):
        if any(fnmatch.fnmatchcase(text.lower(), g.lower()) for g in policy[key] if (" with " in g.lower()) == pair):
            return cls
    return None


def classify(policy, license_text):
    """-> (class, [{license, class}] per term)."""
    if license_text is None or license_text.strip().upper() in ("", "NOASSERTION", "NONE"):
        return "unknown", []
    try:
        tree = parse_spdx(license_text)
    except ValueError:
        return "unparsed", []
    terms = []

    def ev(node):
        if node[0] == "lic":
            name = node[1] + (f" WITH {node[2]}" if node[2] else "")
            if policy is None:
                terms.append(dict(license=name, **{"class": "unclassified"}))
                return None
            cls = lookup(policy, name) if node[2] else None
            if cls is None:
                cls = lookup(policy, node[1]) or "unlisted"
                if node[2] and cls == "denied":
                    cls = "review"
            terms.append(dict(license=name, **{"class": cls}))
            return RANK[cls]
        ranks = [ev(n) for n in node[1]]
        if None in ranks:
            return None
        return min(ranks) if node[0] == "or" else max(ranks)

    r = ev(tree)
    if r is None:
        return "unclassified", terms
    return next(c for c, v in RANK.items() if v == r), terms


def load_policy(path):
    """-> {key: [items]} for the POLICY_KEYS lines, or None when the file has no license list."""
    if not path or not os.path.isfile(path):
        return None
    pol = {k: [] for k in POLICY_KEYS}
    found = False
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            m = re.match(r"^\s*(?:[-*]\s*)?`?(" + "|".join(map(re.escape, POLICY_KEYS)) + r")`?\s*:\s*(.*)$", line)
            if not m:
                continue
            found = found or m.group(1) in ("allow", "review", "deny")
            for item in m.group(2).replace("`", "").split(","):     # `X WITH Y` stays one item
                pol[m.group(1)] += [" ".join(item.split())] if " with " in item.lower() else item.split()
    return pol if found or any(pol[k] for k in POLICY_KEYS) else None


# ------------------------------------------------------------------ trees

def git_blob_id(data):
    """The id git gives this content (`git hash-object`)."""
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


ENV = dict(os.environ, GIT_OPTIONAL_LOCKS="0")         # read-only: never take the index lock


def git(root, *args, binary=False):
    r = subprocess.run(["git", "-C", root, *args], capture_output=True, timeout=120, env=ENV)
    if r.returncode:
        raise RuntimeError(f"git {' '.join(args)}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout if binary else r.stdout.decode("utf-8", errors="replace")


class GitTree:
    """The files of one commit, read from git objects (no checkout)."""

    def __init__(self, root, commit, scope=""):
        self.root, self.commit, self.blobs, self.submodules = root, commit, {}, {}
        out = git(root, "ls-tree", "-r", "-z", "--full-tree", commit, *(["--", scope] if scope else []))
        for entry in out.split("\0"):
            if not entry:
                continue
            meta, path = entry.split("\t", 1)
            mode, kind, sha = meta.split()
            if kind == "blob" and mode != "120000":     # symlinks are not files of their own
                self.blobs[path] = sha
            elif kind == "commit":                      # a submodule: its files are in another repository
                self.submodules[path] = sha

    def paths(self):
        return self.blobs.keys()

    def blob(self, path):
        return self.blobs[path]

    def read(self, path):
        return git(self.root, "cat-file", "blob", self.blobs[path], binary=True)


class DiskTree:
    """The files under a directory that is not a git checkout."""

    def __init__(self, root):
        self.root, self.files, self.submodules = root, [], {}
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
            for f in sorted(filenames):
                if os.path.islink(os.path.join(dirpath, f)):
                    continue
                self.files.append(os.path.relpath(os.path.join(dirpath, f), root).replace(os.sep, "/"))
        self.fileset, self._blobs = set(self.files), {}

    def paths(self):
        return self.fileset

    def read(self, path):
        with open(os.path.join(self.root, path), "rb") as fh:
            return fh.read()

    def blob(self, path):
        if path not in self._blobs:
            self._blobs[path] = git_blob_id(self.read(path))
        return self._blobs[path]


# ------------------------------------------------------------------ manifests

def manifest_kind(path):
    base = path.rsplit("/", 1)[-1]
    if base in ("package-lock.json", "npm-shrinkwrap.json"):
        return "npm", "npm-lock"
    if base == "packages.lock.json":
        return "nuget", "nuget-lock"
    if base == "Directory.Packages.props":
        return "nuget", "nuget-central"
    if NUGET_PROJECT.search(base):
        return "nuget", "nuget-project"
    if base == "uv.lock":
        return "pypi", "uv-lock"
    if re.match(r"^requirements[\w.-]*\.txt$", base):
        return "pypi", "requirements"
    return None


def key_lines(text, pattern):
    """{key: first line number} for every line whose `pattern` group 1 matches."""
    out = {}
    for n, line in enumerate(text.splitlines(), 1):
        m = pattern.match(line)
        if m:
            out.setdefault(m.group(1), n)
    return out


JSON_KEY = re.compile(r'^\s*"([^"]+)"\s*:\s*\{')


def dep(name, version, line, direct=False, dev=False, **extra):
    return dict(name=name, version=version, line=line, direct=direct, dev=dev, **extra)


def parse_npm_lock(text, sibling_pkg=None):
    d = json.loads(text)
    lines = key_lines(text, JSON_KEY)
    out = []
    pk = d.get("packages")
    if isinstance(pk, dict):
        # Direct names per owner: the root project and each workspace (a key with no node_modules/).
        # Hoisted packages sit under the root's node_modules/, so a workspace's direct dependency
        # there counts as direct too.
        owners = {}
        for key, e in pk.items():
            if "node_modules/" in key or not isinstance(e, dict):
                continue
            owners[key] = set()
            for k in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies"):
                owners[key] |= set((e.get(k) or {}).keys())
        hoisted = set().union(*owners.values()) if owners else set()
        for key, e in pk.items():
            if "node_modules/" not in key or not isinstance(e, dict) or e.get("link"):
                continue
            owner, last = key.rsplit("node_modules/", 1)
            owner = owner.rstrip("/")
            name = e.get("name") or last
            direct = name in (hoisted if owner == "" else owners.get(owner, ()))
            out.append(dep(name, e.get("version"), lines.get(key), direct=direct,
                           dev=bool(e.get("dev")), license=e.get("license"), key=key))
        return out
    direct = set()
    if sibling_pkg:
        for k in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies"):
            direct |= set((sibling_pkg.get(k) or {}).keys())

    def walk(deps, prefix):                              # lockfile v1: nested `dependencies`
        for name, e in (deps or {}).items():
            if not isinstance(e, dict):
                continue
            key = prefix + "node_modules/" + name
            out.append(dep(name, e.get("version"), lines.get(name), direct=not prefix and name in direct,
                           dev=bool(e.get("dev")), license=None, key=key))
            walk(e.get("dependencies"), key + "/")
    walk(d.get("dependencies"), "")
    return out


def parse_nuget_lock(text):
    d = json.loads(text)
    lines = key_lines(text, JSON_KEY)
    seen = {}
    for pkgs in (d.get("dependencies") or {}).values():
        for name, e in (pkgs or {}).items():
            if not isinstance(e, dict) or e.get("type") == "Project":
                continue
            k = (name, e.get("resolved"))
            prev = seen.get(k)
            direct = e.get("type") == "Direct" or bool(prev and prev["direct"])
            seen[k] = dep(name, e.get("resolved"), lines.get(name), direct=direct)
    return list(seen.values())


XML_ATTR = re.compile(r'([\w:]+)\s*=\s*"([^"]*)"')
PKG_ELEM = re.compile(r"<(PackageVersion|GlobalPackageReference|PackageReference)\b([^>]*?)(/>|>(.*?)</\1\s*>)", re.S)
XML_COMMENT = re.compile(r"<!--.*?-->", re.S)


def parse_msbuild(text, central=None):
    """PackageVersion / GlobalPackageReference / PackageReference items -> deps. `central` maps
    lower-case ids to Directory.Packages.props versions for references without a version."""
    clean = XML_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    out = []
    for m in PKG_ELEM.finditer(clean):
        attrs = dict(XML_ATTR.findall(m.group(2)))
        name = attrs.get("Include") or attrs.get("Update")
        if not name:
            continue
        version = attrs.get("VersionOverride") or attrs.get("Version")
        if version is None and m.group(4):
            v = re.search(r"<Version>\s*([^<]*?)\s*</Version>", m.group(4))
            version = v.group(1) if v else None
        if version is None and central is not None:
            version = central.get(name.lower())
        priv = attrs.get("PrivateAssets") or (re.search(r"<PrivateAssets>\s*([^<]*?)\s*<", m.group(4) or "") or [None, None])[1]
        line = clean.count("\n", 0, m.start()) + 1
        out.append(dep(name, version, line, direct=True, dev=(priv or "").lower() == "all"))
    return out


def norm_py(name):
    return re.sub(r"[-_.]+", "-", name).lower()


def parse_requirements(text):
    out, buf, start = [], "", None
    for n, raw in enumerate(text.splitlines(), 1):
        if start is None:
            start = n
        if raw.rstrip().endswith("\\"):
            buf += raw.rstrip()[:-1] + " "
            continue
        s = re.sub(r"(^|\s)#.*$", "", buf + raw).strip()
        buf, line, start = "", start, None
        if not s or s.startswith("-") or "://" in s or s.startswith((".", "/")):
            continue
        m = re.match(r"^([A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*([^;]*)", s)
        if not m:
            continue
        spec = m.group(2).split("--")[0].strip()
        exact = re.fullmatch(r"===?\s*([^\s,]+)", spec)
        out.append(dep(norm_py(m.group(1)), exact.group(1) if exact else (spec or None), line, direct=True))
    return out


def parse_uv_lock(text):
    if tomllib is None:
        raise ValueError("uv.lock needs Python 3.11+ (tomllib)")
    d = tomllib.loads(text)
    lines = key_lines(text, re.compile(r'^name\s*=\s*"([^"]+)"'))
    pkgs = d.get("package") or []
    roots = [p for p in pkgs if any(k in (p.get("source") or {}) for k in ("editable", "virtual"))]
    direct = set()
    for p in roots:
        direct |= {norm_py(x["name"]) for x in p.get("dependencies") or [] if "name" in x}
        for group in ("optional-dependencies", "dev-dependencies"):
            for lst in (p.get(group) or {}).values():
                direct |= {norm_py(x["name"]) for x in lst if "name" in x}
    return [dep(norm_py(p["name"]), p.get("version"), lines.get(p["name"]), direct=norm_py(p["name"]) in direct)
            for p in pkgs if p not in roots and "name" in p]


class Inventory:
    """Every dependency in one tree, per manifest."""

    def __init__(self, tree, cache):
        self.tree, self.manifests, self.limits = tree, {}, []
        paths = sorted(tree.paths())
        central = {}
        for p in paths:
            if manifest_kind(p) == ("nuget", "nuget-central"):
                try:
                    central[p.rsplit("/", 1)[0] if "/" in p else ""] = {
                        d["name"].lower(): d["version"] for d in parse_msbuild(self._text(p)) if d["version"]}
                except (UnicodeDecodeError, OSError, RuntimeError) as e:
                    self.limits.append(f"{p}: not read ({e})")
        pset = set(paths)
        for p in paths:
            base = p.rsplit("/", 1)[-1]
            if base in UNPARSED_LOCKS:
                self.limits.append(f"{p}: lockfile format not parsed")
            d = p[:-len(base)]
            if base == "package.json" and "/node_modules/" not in "/" + p and not any(
                    d + f in pset for f in ("package-lock.json", "npm-shrinkwrap.json", *UNPARSED_LOCKS)):
                self.limits.append(f"{p}: no lockfile beside it; its dependencies are not read")
            if base == "pyproject.toml" and d + "uv.lock" not in pset and not any(
                    q.startswith(d) and manifest_kind(q) == ("pypi", "requirements") for q in pset):
                self.limits.append(f"{p}: no uv.lock or requirements file beside it; its dependencies are not read")
            kind = manifest_kind(p)
            if not kind:
                continue
            try:
                blob = tree.blob(p)
                cmap = self._central_for(p, central) if kind[1] == "nuget-project" else None
                key = (blob, kind[1], tuple(sorted(cmap.items())) if cmap else None)
                if kind[1] == "requirements":
                    text = self._text(p)
                    if re.search(r"^\s*(-r|-c|--requirement|--constraint)\b", text, re.M):
                        self.limits.append(f"{p}: -r/-c includes are not followed")
                    if re.search(r"^\s*[A-Za-z0-9][\w.-]*\s*(\[[^\]]*\])?\s*@|^\s*(-e\s+)?\w+\+?\w*://", text, re.M):
                        self.limits.append(f"{p}: URL and VCS requirements are not read")
                if key not in cache:
                    cache[key] = self._parse(p, kind[1], central)
                self.manifests[p] = dict(ecosystem=kind[0], kind=kind[1], blob=blob, deps=cache[key])
            except (ValueError, UnicodeDecodeError, OSError, RuntimeError, KeyError, TypeError) as e:
                self.limits.append(f"{p}: not parsed ({type(e).__name__}: {e})")

    def _text(self, p):
        return self.tree.read(p).decode("utf-8-sig")

    @staticmethod
    def _central_for(p, central):
        d = p.rsplit("/", 1)[0] if "/" in p else ""
        while True:
            if d in central:
                return central[d]
            if not d:
                return None
            d = d.rsplit("/", 1)[0] if "/" in d else ""

    def _parse(self, p, kind, central):
        text = self._text(p)
        if kind == "npm-lock":
            sib = (p.rsplit("/", 1)[0] + "/" if "/" in p else "") + "package.json"
            pkg = None
            if sib in self.tree.paths():
                try:
                    pkg = json.loads(self.tree.read(sib).decode("utf-8-sig"))
                except ValueError:
                    pkg = None
            return parse_npm_lock(text, pkg)
        if kind == "nuget-lock":
            return parse_nuget_lock(text)
        if kind == "nuget-central":
            return parse_msbuild(text)
        if kind == "nuget-project":
            return parse_msbuild(text, self._central_for(p, central))
        if kind == "uv-lock":
            return parse_uv_lock(text)
        return parse_requirements(text)

    def by_package(self):
        """{(ecosystem, name): {manifest path: [deps]}}; NuGet ids compare case-insensitively."""
        out = {}
        for path, m in self.manifests.items():
            for d in m["deps"]:
                name = d["name"].lower() if m["ecosystem"] == "nuget" else d["name"]
                out.setdefault((m["ecosystem"], name), {}).setdefault(path, []).append(d)
        return out


# ------------------------------------------------------------------ license resolution

CONCRETE = re.compile(r"^v?\d[\w.+-]*$")


class Resolver:
    def __init__(self, root, nuget_cache, on_disk):
        self.root, self.nuget_cache, self.on_disk, self.memo = root, nuget_cache, on_disk, {}

    def resolve(self, eco, name, version, entries):
        """-> {version, license, source, detail?}; license None when not resolved offline."""
        k = (eco, name, version)
        if k not in self.memo:
            if version is None or not CONCRETE.match(version):
                r = dict(license=None, source=None, detail=f"version `{version}` is not a single version")
            else:
                r = getattr(self, "_" + eco)(name, version, entries)
            self.memo[k] = dict(version=version, **r)
        return self.memo[k]

    def _npm(self, name, version, entries):
        for path, d in entries:
            if d["version"] == version and d.get("license"):
                lic = d["license"]
                return dict(license=lic if isinstance(lic, str) else str(lic), source=f"lockfile {path}")
        if self.on_disk:
            for path, d in entries:
                if d["version"] != version:
                    continue
                lockdir = os.path.dirname(path)
                pj = os.path.join(self.root, lockdir, d["key"], "package.json")
                try:
                    with open(pj, encoding="utf-8") as fh:
                        meta = json.load(fh)
                except (OSError, ValueError):
                    continue
                if meta.get("version") != version:
                    continue
                lic, how = npm_package_license(meta)
                if lic:
                    return dict(license=lic, source=os.path.relpath(pj, self.root).replace(os.sep, "/"),
                                **(dict(detail=how) if how else {}))
        return dict(license=None, source=None, detail="no license in the lockfile; " + (
            "not installed in node_modules at this version" if self.on_disk else
            "--head is not the checked-out commit; node_modules not read"))

    def _nuget(self, name, version, entries):
        if not self.nuget_cache:
            return dict(license=None, source=None, detail="no NuGet package cache")
        lid = name.lower()
        for v in dict.fromkeys([version.lower(), nuget_normal(version)]):
            nuspec = os.path.join(self.nuget_cache, lid, v, lid + ".nuspec")
            if os.path.isfile(nuspec):
                return nuspec_license(nuspec)
        return dict(license=None, source=None, detail="not in the NuGet package cache (not restored here)")

    def _pypi(self, name, version, entries):
        if not self.on_disk:
            return dict(license=None, source=None, detail="--head is not the checked-out commit; .venv not read")
        dirs = []
        for path, _ in entries:
            d = os.path.dirname(path)
            while True:
                if d not in dirs:
                    dirs.append(d)
                if not d:
                    break
                d = os.path.dirname(d)
        sites = []
        for venv in (os.path.join(self.root, x, v) for x in dirs for v in (".venv", "venv")):
            lib = os.path.join(venv, "lib")
            if os.path.isdir(lib):
                sites += [os.path.join(lib, py, "site-packages") for py in sorted(os.listdir(lib))]
            sites.append(os.path.join(venv, "Lib", "site-packages"))      # the Windows layout
        for site in sites:
            if not os.path.isdir(site):
                continue
            for f in os.listdir(site):
                m = re.match(r"^(.+)-([^-]+)\.dist-info$", f)
                if m and norm_py(m.group(1)) == name and m.group(2) == version:
                    return dist_info_license(os.path.join(site, f, "METADATA"), self.root)
        return dict(license=None, source=None, detail="not installed in a .venv or venv beside the manifest or above it")


def nuget_normal(version):
    """NuGet's normalized folder name: no build metadata, no fourth part when it is zero."""
    v = version.split("+", 1)[0].lower()
    m = re.match(r"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(-.*)?$", v)
    if not m:
        return v
    parts = [m.group(i) or "0" for i in (1, 2, 3)] + ([m.group(4)] if m.group(4) and m.group(4) != "0" else [])
    return ".".join(str(int(x)) for x in parts) + (m.group(5) or "")


def npm_package_license(meta):
    lic = meta.get("license")
    if isinstance(lic, dict):
        lic = lic.get("type")
    if isinstance(lic, str) and lic.strip():
        return lic, None
    lics = meta.get("licenses")
    if isinstance(lics, list):
        types = [x.get("type") if isinstance(x, dict) else x for x in lics]
        types = [t for t in types if isinstance(t, str) and t.strip()]
        if types:
            return (types[0] if len(types) == 1 else "(" + " AND ".join(types) + ")"), \
                "legacy `licenses` array, joined with AND (the strictest reading)"
    return None, None


def nuspec_license(path):
    with open(path, encoding="utf-8-sig", errors="replace") as fh:
        text = fh.read()
    src = os.path.join("~/.nuget/packages", *path.replace(os.sep, "/").split("/")[-3:]) \
        if "/.nuget/packages/" in path.replace(os.sep, "/") else path
    m = re.search(r"<license\b([^>]*)>\s*([^<]*?)\s*</license>", text)
    if m:
        kind = (dict(XML_ATTR.findall(m.group(1))).get("type") or "").lower()
        if kind == "expression":
            return dict(license=m.group(2), source=f"nuspec {src}")
        if kind == "file":
            return dict(license=None, source=f"nuspec {src}",
                        detail=f"license text in package file `{os.path.join(os.path.dirname(src), m.group(2))}`, not classified")
    u = re.search(r"<licenseUrl>\s*([^<]*?)\s*</licenseUrl>", text)
    if u:
        e = re.match(r"^https?://licenses\.nuget\.org/(.+)$", u.group(1))
        if e:
            from urllib.parse import unquote
            return dict(license=unquote(e.group(1)), source=f"nuspec {src} licenseUrl")
        return dict(license=None, source=f"nuspec {src}", detail=f"licenseUrl only: {u.group(1)}")
    return dict(license=None, source=f"nuspec {src}", detail="nuspec declares no license")


def dist_info_license(path, root):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            head = fh.read().split("\n\n", 1)[0]
    except OSError:
        return dict(license=None, source=None, detail="dist-info without METADATA")
    src = os.path.relpath(path, root).replace(os.sep, "/")
    fields = {}
    for line in head.splitlines():
        if ":" in line and not line.startswith((" ", "\t")):
            k, v = line.split(":", 1)
            fields.setdefault(k.strip().lower(), v.strip())
    if fields.get("license-expression"):
        return dict(license=fields["license-expression"], source=src)
    if fields.get("license") and len(fields["license"]) <= 80 and fields["license"].upper() != "UNKNOWN":
        return dict(license=fields["license"], source=src, detail="METADATA License field")
    cls = [l.split("::")[-1].strip() for l in head.splitlines() if l.startswith("Classifier: License ::")]
    return dict(license=None, source=src, detail="classifiers only: " + ", ".join(cls) if cls else "METADATA declares no license")


# ------------------------------------------------------------------ vendored files

def vendored_kind(path, policy):
    low = path.lower()
    pol = policy or {}
    if any(fnmatch.fnmatchcase(low, g.lower()) for g in pol.get("not-vendored", [])):
        return None
    base = low.rsplit("/", 1)[-1]
    if is_license_file(base):
        return None
    ext = os.path.splitext(base)[1]
    segs = low.split("/")[:-1]
    if any(s in VENDOR_DIRS for s in segs) or "wwwroot/lib/" in low or \
            any(fnmatch.fnmatchcase(low, g.lower()) for g in pol.get("vendored-dirs", [])):
        return "vendored-dir"
    for kind, exts in (("font", FONT_EXT), ("model", MODEL_EXT), ("texture", TEXTURE_EXT), ("audio", AUDIO_EXT)):
        if ext in exts:
            return kind
    if ext and ext in [e.lower() if e.startswith(".") else "." + e.lower() for e in pol.get("vendored-extensions", [])]:
        return "extension"
    return None


def license_file_for(path, all_paths, dirs, policy):
    """The nearest license file next to `path` (its sidecar, its directory, or an ancestor below the
    repository root), or None."""
    extra = [g.lower() for g in (policy or {}).get("license-files", [])]
    for side in (path + ".license", path + ".licence"):
        if side in all_paths:
            return side
    d = path.rsplit("/", 1)[0] if "/" in path else ""
    while d:
        for f in dirs.get(d, ()):
            if is_license_file(f) and not f.lower().endswith((".license", ".licence")) or \
                    any(fnmatch.fnmatchcase(f.lower(), g) for g in extra):
                return d + "/" + f
        d = d.rsplit("/", 1)[0] if "/" in d else ""
    return None


# ------------------------------------------------------------------ scan

def worst(classes):
    order = list(CLASSES)
    return min(classes, key=order.index) if classes else "unknown"


def package_rule(policy, eco, name):
    if not policy:
        return None
    for cls, key in (("denied", "deny-packages"), ("review", "review-packages"), ("allowed", "allow-packages")):
        for g in policy[key]:
            if fnmatch.fnmatchcase(name.lower(), g.lower()) or fnmatch.fnmatchcase(f"{eco}:{name}".lower(), g.lower()):
                return dict(**{"class": cls}, rule=f"{key}: {g}")
    return None


def signature(entries):
    """What a manifest says about one package: its versions with their dev and direct flags."""
    return sorted((d["version"] or "", bool(d["dev"]), bool(d["direct"])) for d in entries)


def placements(side):
    """{(manifest, version): dev-only in that manifest?} for one package."""
    out = {}
    for p, ds in side.items():
        for d in ds:
            k = (p, d["version"] or "")
            out[k] = out.get(k, True) and bool(d["dev"])
    return out


def new_placements(h, b):
    """Head placements the base did not have: a version new to its manifest, or one that was
    dev-only there and is now runtime. These are the ones whose license the change brings in."""
    hp, bp = placements(h), placements(b)
    return sorted(k for k, dev in hp.items() if k not in bp or (bp[k] and not dev))


def new_placements_dev(h, b):
    """[((manifest, version), dev-only)] for the head placements the base did not have."""
    hp = placements(h)
    return [(k, hp[k]) for k in new_placements(h, b)]


def scope_changes(h, b):
    """How a package's placement changed: the manifests it was added to or removed from, versions
    that turned from dev-only to runtime (or back), and its direct flag."""
    out = [f"added to {p}" for p in sorted(set(h) - set(b))] + [f"removed from {p}" for p in sorted(set(b) - set(h))]
    hp, bp = placements(h), placements(b)
    hd = [d for ds in h.values() for d in ds]
    bd = [d for ds in b.values() for d in ds]
    if all(d["dev"] for d in bd) and not all(d["dev"] for d in hd):
        out.append("now runtime (was dev-only)")
    elif all(d["dev"] for d in hd) and not all(d["dev"] for d in bd):
        out.append("now dev-only (was runtime)")
    else:
        out += [f"{v or '-'} now runtime in {p} (was dev-only)" for (p, v), dev in sorted(hp.items())
                if not dev and bp.get((p, v)) is True and p in b]
    if any(d["direct"] for d in hd) and not any(d["direct"] for d in bd):
        out.append("now direct (was transitive)")
    elif any(d["direct"] for d in bd) and not any(d["direct"] for d in hd):
        out.append("now transitive (was direct)")
    return out


def scan(root, mode, head_tree, base_tree, policy, resolver):
    cache, limits = {}, []
    head = Inventory(head_tree, cache)
    limits += head.limits
    base = Inventory(base_tree, cache) if base_tree is not None else None
    hp = head.by_package()
    bp = base.by_package() if base else {}
    deps = []
    for key in sorted(set(hp) | set(bp)):
        eco = key[0]
        h, b = hp.get(key, {}), bp.get(key, {})
        hv = sorted({d["version"] for ds in h.values() for d in ds if d["version"]})
        bv = sorted({d["version"] for ds in b.values() for d in ds if d["version"]})
        scope = []
        if base is not None:
            paths = sorted(p for p in set(h) | set(b) if signature(h.get(p, [])) != signature(b.get(p, [])))
            if not h:
                change = "removed"
            elif not b:
                change = "added"
            elif hv != bv:
                change, scope = "version-changed", scope_changes(h, b)
            elif paths:
                change, scope = "scope-changed", scope_changes(h, b) or [f"flags changed in {paths[0]}"]
            else:
                continue
        else:
            change, paths = "present", sorted(h)
        side = h if h else b
        name = next(d["name"] for ds in side.values() for d in ds)
        entries = [(p, d) for p, ds in side.items() for d in ds]
        item = dict(ecosystem=eco, name=name, change=change, versions=hv, base_versions=bv if base is not None else None,
                    direct=any(d["direct"] for _, d in entries), dev=all(d["dev"] for _, d in entries),
                    manifests=[dict(path=p, line=next((d["line"] for d in side.get(p, []) if d["line"]), None))
                               for p in paths])
        if scope:
            item["scope"] = scope
        # The license of each version the change places somewhere new (a manifest that did not have
        # it, or runtime where it was dev-only); versions that only went away bring no new license.
        placed = {}                         # version -> dev-only in every new placement?
        for (p, v), dev in sorted(placements(h).items()) if base is None else new_placements_dev(h, b):
            placed[v or None] = placed.get(v or None, True) and dev
        if change != "removed" and placed:
            new = sorted(placed, key=lambda v: (v is None, v or ""))
            concrete = [v for v in new if v and CONCRETE.match(v)] or new
            item["licenses"] = []
            for v in concrete:
                lic = dict(resolver.resolve(eco, key[1], v, entries))
                if placed[v] and not item["dev"]:
                    lic["dev"] = True       # this version comes in dev-only although the package is not
                item["licenses"].append(lic)
            per = []
            terms = []
            for lic in item["licenses"]:
                c, t = classify(policy, lic["license"])
                lic["class"] = c
                per.append(c)
                terms += [x for x in t if x not in terms]
            item["class"], item["terms"] = worst(per), terms
            rule = package_rule(policy, eco, name)
            if rule:
                item["package_rule"] = rule
                item["class"] = rule["class"]
        deps.append(item)
    return head, base, deps, limits


def vendored_files(head_tree, added, policy):
    all_paths = set(head_tree.paths())
    dirs = {}
    for p in all_paths:
        d, f = (p.rsplit("/", 1) if "/" in p else ("", p))
        dirs.setdefault(d, []).append(f)
    for v in dirs.values():
        v.sort()
    out = []
    for p in sorted(added):
        if p in head_tree.submodules:   # vendored code whose license, if any, lies in another repository
            out.append(dict(path=p, kind="submodule", blob=head_tree.submodules[p], license_file=None,
                            license_file_at=None))
            continue
        if p not in all_paths:          # a symlink: its target is a file of its own, or outside the tree
            continue
        kind = vendored_kind(p, policy)
        if kind:
            lf = license_file_for(p, all_paths, dirs, policy)
            near = lf is not None and (lf.startswith(p + ".") or lf.rsplit("/", 1)[0] == (p.rsplit("/", 1)[0] if "/" in p else ""))
            out.append(dict(path=p, kind=kind, blob=head_tree.blob(p), license_file=lf,
                            license_file_at=None if lf is None else ("beside" if near else "parent")))
    return out


def versions_label(d):
    if d["change"] == "version-changed":
        s = f"{', '.join(d['base_versions']) or '-'} -> {', '.join(d['versions']) or '-'}"
    else:
        s = ", ".join(d["versions"] if d["change"] != "removed" else d["base_versions"]) or "-"
    return s + (" [" + "; ".join(d["scope"]) + "]" if d.get("scope") else "")


def build_facts(deps, vendored):
    facts = []
    for d in deps:
        cls = d.get("class")
        if cls not in CLASS_CHECK:
            continue
        m = d["manifests"][0] if d["manifests"] else dict(path=None, line=None)
        lic = "; ".join(f"{l['license'] or 'unknown'}" + (f" at {l['version']}" if len(d["licenses"]) > 1 else "")
                        + (" (dev-only there)" if l.get("dev") else "")
                        + (f" ({l['detail']})" if l.get("detail") else "") for l in d["licenses"])
        rule = f"; {d['package_rule']['rule']}" if d.get("package_rule") else ""
        facts.append(dict(check=CLASS_CHECK[cls], ecosystem=d["ecosystem"], name=d["name"], file=m["path"], line=m["line"],
                          detail=f"{d['change']} {versions_label(d)}, {'direct' if d['direct'] else 'transitive'}"
                                 f"{', dev' if d['dev'] else ''}: {lic}{rule}"))
    for v in vendored:
        if not v["license_file"]:
            facts.append(dict(check="vendored-without-license", file=v["path"], line=None,
                              detail=f"submodule at commit {v['blob'][:12]}; its files, and any license file, are "
                                     f"in that repository and not read" if v["kind"] == "submodule" else
                                     f"{v['kind']} with no license file beside it or in a parent directory"))
    return facts


# ------------------------------------------------------------------ output

def count(items, key, order=None):
    out = {}
    for i in items:
        out[i[key]] = out.get(i[key], 0) + 1
    return {k: out[k] for k in (order or sorted(out)) if k in out}


def markdown(rep, max_lines):
    deps, rev = rep["dependencies"], rep["revision"]
    head = (f"# license-scan facts ({rep['mode']}: {len(deps)} dependencies"
            + (" changed" if rep["mode"] == "diff" else "") + f", {len(rep['vendored'])} vendored files"
            + (" added" if rep["mode"] == "diff" else "") + f", {len(rep['facts'])} facts)")
    out = [head, "", "Facts only; the reviewer judges what they mean for this change.", ""]
    if rev is None:
        out.append("Revision: not a git checkout (files on disk); manifest blob ids are in --json")
    else:
        s = f"Revision: head `{rev['head']}`"
        if rep["mode"] == "diff":
            s += f", base `{rev['base']}`" + (f" ({rev['base_ref']})" if rev["base_ref"] != rev["base"] else "") \
                + f", merge base `{rev['merge_base']}`"
        if rev.get("scope"):
            s += f", files under `{rev['scope']}/` only"
        out.append(s + "; manifest blob ids are in --json")
        if rev.get("uncommitted"):
            out.append("Uncommitted (not scanned): " + ", ".join(f"`{p}`" for p in rev["uncommitted"][:5])
                       + (f" and {len(rev['uncommitted']) - 5} more" if len(rev["uncommitted"]) > 5 else ""))
    pol = rep["policy"]
    out.append(f"Policy: {pol['source']} (allow {len(pol['allow'])}, review {len(pol['review'])}, deny {len(pol['deny'])}, "
               f"package rules {sum(len(pol[k]) for k in ('allow-packages', 'review-packages', 'deny-packages'))})"
               if pol else "Policy: none (no references/local.md lists); licenses inventoried, not classified")
    eco = count(rep["manifests"], "ecosystem")
    out.append(f"Manifests read: {len(rep['manifests'])}" + (" (" + ", ".join(f"{k} {v}" for k, v in eco.items()) + ")" if eco else ""))
    c = rep["counts"]
    if deps:
        out.append("Dependencies: " + ", ".join(f"{v} {k}" for k, v in c["changes"].items())
                   + f"; {sum(1 for d in deps if d['change'] in ('added', 'present') and not d['direct'])} transitive"
                   + (" added" if rep["mode"] == "diff" else ""))
        out.append("Classes: " + ", ".join(f"{k} {v}" for k, v in c["classes"].items()))
    lics = {}
    for d in deps:
        for l in d.get("licenses", []):
            lics.setdefault(l["license"] or "unknown", []).append(d["name"])
    if lics:
        parts = []
        for k, names in sorted(lics.items(), key=lambda kv: (-len(kv[1]), kv[0])):
            parts.append(f"{k} {len(names)}" + (" (" + ", ".join(sorted(set(names))) + ")" if len(names) <= 2 else ""))
        line = "Licenses: " + ", ".join(parts)
        out.append(line if len(line) <= 600 else line[:597] + "...")
    tail = []
    if rep["limits"]:
        shown = rep["limits"][:8]
        tail += ["", "Limits:"] + [f"- {l}" for l in shown]
        if len(rep["limits"]) > len(shown):
            tail.append(f"- ... {len(rep['limits']) - len(shown)} more limits in --json")
    facts = sorted(rep["facts"], key=lambda f: (CHECKS.index(f["check"]), f["file"] or "", f.get("name") or ""))
    lines, groups = [], {}
    for f in facts:
        if f["check"] == "vendored-without-license":
            d = f["file"].rsplit("/", 1)[0] if "/" in f["file"] else "."
            groups.setdefault(d, []).append(f)
            continue
        where = f"`{f['file']}" + (f":{f['line']}" if f["line"] else "") + "`" if f["file"] else "(no manifest)"
        who = f" {f['ecosystem']} `{f['name']}`" if f.get("name") else ""
        lines.append(f"- {f['check']}{who} {where}: {f['detail']}")
    for d, fs in sorted(groups.items()):
        kinds = sorted({f["detail"].split(" ", 1)[0] for f in fs})
        lines.append(f"- vendored-without-license `{fs[0]['file']}`" if len(fs) == 1 else
                     f"- vendored-without-license `{d}/`: {len(fs)} files (" + ", ".join(
                         f"`{f['file'].rsplit('/', 1)[-1]}`" for f in fs[:3]) + (", ..." if len(fs) > 3 else "") + ")")
        lines[-1] += (f"; submodule at {fs[0]['detail'].split()[3]}, its license is in that repository and not read"
                      if kinds == ["submodule"] else f"; {', '.join(kinds)}, no license file beside or above")
    changes = [d for d in deps if d.get("class") in ("allowed", "unclassified") and rep["mode"] == "diff"]
    other = [f"- {d['change']} {d['ecosystem']} `{d['name']}` {versions_label(d)}"
             f" ({'direct' if d['direct'] else 'transitive'}): "
             + "; ".join(l["license"] or "unknown" for l in d["licenses"]) for d in changes]
    other += [f"- {d['change']} {d['ecosystem']} `{d['name']}` {versions_label(d)}"
              + (" (no new placement, no license lookup)" if d["change"] != "removed" else "")
              for d in deps if "licenses" not in d and rep["mode"] == "diff"]
    covered = {}
    for v in rep["vendored"]:
        if v["license_file"]:
            covered.setdefault((v["license_file"], v["license_file_at"]), []).append(v["path"])
    for (lf, at), ps in sorted(covered.items(), key=lambda kv: (kv[0][1] != "parent", kv[0][0])):
        other.append(f"- vendored {', '.join(f'`{x}`' for x in ps[:2])}" + (f" and {len(ps) - 2} more" if len(ps) > 2 else "")
                     + f": license file `{lf}`" + (" in a parent directory" if at == "parent" else ""))
    budget = max_lines - len(out) - len(tail) - 2
    if lines:
        if len(lines) > budget:
            keep = max(budget - 1, 0)
            lines = lines[:keep] + [f"- ... {len(facts) - keep} more in --json"]
        out += ["", "Facts:"] + lines
    budget = max_lines - len(out) - len(tail) - 2
    if other and budget > 1:
        if len(other) > budget:
            other = other[:budget - 1] + [f"- ... {len(other) - budget + 1} more in --json"]
        out += ["", "Other changes:"] + other
    out += tail
    if len(out) > max_lines:
        out = out[:max_lines - 1] + ["... truncated; the full report is in --json"]
    return "\n".join(out) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Dependency-license and vendored-file facts for a reviewer (no verdicts, no network).",
        epilog="Prints a markdown summary; --json prints the full report. Exit 0 when the scan ran, "
               "2 on a usage error, an unknown ref or an unreadable path.")
    ap.add_argument("--root", default=".", help="repository root (default .)")
    ap.add_argument("--base", help="diff range: compare the merge base of this ref with --head")
    ap.add_argument("--head", help="the revision to scan (default HEAD)")
    ap.add_argument("--all", action="store_true", help="every dependency and vendored file at --head instead of a diff")
    ap.add_argument("--json", action="store_true", help="print the full JSON report instead of markdown")
    ap.add_argument("--policy", help="allow/review/deny lists (default references/local.md in the skill)")
    ap.add_argument("--nuget-cache", help="NuGet global packages folder (default $NUGET_PACKAGES or ~/.nuget/packages)")
    ap.add_argument("--out", help="also write the markdown summary to this file (review evidence)")
    ap.add_argument("--json-out", help="also write the JSON report to this file (review evidence)")
    ap.add_argument("--max-lines", type=int, default=60, help="markdown summary line cap (default 60)")
    a = ap.parse_args(argv)

    def fail(msg):
        print(f"license-scan: {msg}", file=sys.stderr)
        return 2
    if bool(a.base) == bool(a.all):
        return fail("pass --base REF for a diff range (with --head, default HEAD), or --all for every dependency "
                    "at --head (or on disk when --root is not a git checkout)")
    root = os.path.abspath(a.root)
    if not os.path.isdir(root):
        return fail(f"--root is not a directory: {a.root}")
    if a.policy and not os.path.isfile(a.policy):
        return fail(f"policy file not found: {a.policy}")
    policy_path = a.policy or DEFAULT_POLICY
    policy = load_policy(policy_path)
    try:
        top = git(root, "rev-parse", "--show-toplevel").strip()
    except (RuntimeError, OSError):
        top = None
    nuget_cache = a.nuget_cache or os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
    if not os.path.isdir(nuget_cache):
        nuget_cache = None
    rev, base_tree = None, None
    try:
        if top is None:
            if a.base or a.head:
                return fail(f"--base/--head need a git checkout; {a.root} is not one (use --all alone)")
            head_tree = DiskTree(root)
        else:
            scope = os.path.relpath(root, top).replace(os.sep, "/")
            scope = "" if scope == "." else scope + "/"
            root = top
            head = git(root, "rev-parse", "--verify", (a.head or "HEAD") + "^{commit}").strip()
            head_tree = GitTree(root, head, scope)
            rev = dict(head_ref=a.head or "HEAD", head=head, **({"scope": scope.rstrip("/")} if scope else {}))
            if a.base:
                base = git(root, "rev-parse", "--verify", a.base + "^{commit}").strip()
                mb = git(root, "merge-base", base, head).strip()
                base_tree = GitTree(root, mb, scope)
                rev.update(base_ref=a.base, base=base, merge_base=mb)
    except RuntimeError as e:
        return fail(str(e))
    try:
        on_disk = top is None or rev["head"] == git(root, "rev-parse", "HEAD").strip()
    except RuntimeError:        # an unborn HEAD: the working tree is not the revision scanned
        on_disk = False
    resolver = Resolver(root, nuget_cache, on_disk)
    head_inv, base_inv, deps, limits = scan(root, "diff" if a.base else "all", head_tree, base_tree, policy, resolver)
    if a.base:
        diff = git(root, "diff", "-z", "--name-status", "-M", "--diff-filter=ACR", rev["merge_base"], rev["head"],
                   *(["--", scope] if scope else []))
        parts, added, i = diff.split("\0"), [], 0
        while i < len(parts) - 1:
            status = parts[i]
            if status.startswith(("C", "R")):
                added.append(parts[i + 2])
                i += 3
            else:
                added.append(parts[i + 1])
                i += 2
    else:
        added = list(head_tree.paths()) + list(head_tree.submodules)
    vendored = vendored_files(head_tree, added, policy)
    if rev is not None and on_disk:
        st = git(root, "status", "--porcelain", "-z", "--untracked-files=all", *(["--", scope] if scope else []))
        unc, parts, i = [], st.split("\0"), 0
        while i < len(parts):
            e = parts[i]
            if len(e) > 3:
                unc.append(e[3:])
                i += 2 if "R" in e[:2] or "C" in e[:2] else 1   # a rename or copy is followed by its source path
            else:
                i += 1
        rev["uncommitted"] = sorted(p for p in unc if manifest_kind(p) or vendored_kind(p, policy))
    if not nuget_cache and any(d["ecosystem"] == "nuget" for d in deps):
        limits.append("no NuGet package cache found; NuGet licenses are unknown")
    reuse = [p for p in ("REUSE.toml", ".reuse/dep5") if p in set(head_tree.paths())]
    if reuse:
        limits.append(f"{', '.join(reuse)} present; REUSE annotations are not read, so license_file may be missing for covered files")
    facts = build_facts(deps, vendored)
    shown = policy_path
    if os.path.abspath(policy_path).startswith(SKILL_DIR + os.sep):
        shown = os.path.relpath(os.path.abspath(policy_path), SKILL_DIR).replace(os.sep, "/")
    manifests = [dict(path=p, ecosystem=m["ecosystem"], kind=m["kind"], blob=m["blob"],
                      base_blob=(base_inv.manifests.get(p) or {}).get("blob") if base_inv else None)
                 for p, m in sorted(head_inv.manifests.items())]
    if base_inv:
        manifests += [dict(path=p, ecosystem=m["ecosystem"], kind=m["kind"], blob=None, base_blob=m["blob"])
                      for p, m in sorted(base_inv.manifests.items()) if p not in head_inv.manifests]
    rep = dict(tool="license-scan", schema=SCHEMA, mode="diff" if a.base else "all", revision=rev,
               policy=dict(source=shown, **policy) if policy else None, nuget_cache=bool(nuget_cache),
               manifests=manifests, dependencies=deps, vendored=vendored,
               counts=dict(changes=count(deps, "change", ("added", "version-changed", "scope-changed", "removed", "present")),
                           classes=count([d for d in deps if "class" in d], "class", CLASSES),
                           checks={c: sum(1 for f in facts if f["check"] == c) for c in CHECKS}),
               facts=facts, limits=limits)
    md = markdown(rep, max(a.max_lines, 15))
    for path in (a.out, a.json_out):   # the evidence directory may not exist yet
        if path and os.path.dirname(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as fh:
            fh.write(md)
    if a.json_out:
        with open(a.json_out, "w", encoding="utf-8") as fh:
            fh.write(json.dumps(rep, indent=2) + "\n")
    sys.stdout.write(json.dumps(rep, indent=2) + "\n" if a.json else md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
