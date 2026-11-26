"""GitHub PR evidence, not a review or merge gate."""

import argparse
import datetime
import json
import mmap
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote, urlsplit


REPO = re.compile(r"[\w.-]+/[\w.-]+", re.ASCII)
SHA = re.compile(r"[0-9a-f]{40}")
CHECK_STATES = {"REQUESTED", "QUEUED", "IN_PROGRESS", "COMPLETED", "WAITING", "PENDING"}
CONCLUSIONS = {
    "ACTION_REQUIRED", "TIMED_OUT", "CANCELLED", "FAILURE", "SUCCESS", "NEUTRAL",
    "SKIPPED", "STARTUP_FAILURE", "STALE",
}
MERGE_STATES = {"DIRTY", "UNKNOWN", "BLOCKED", "BEHIND", "UNSTABLE", "HAS_HOOKS", "CLEAN"}
# These active branch rules do not define named required status checks.
OTHER_BRANCH_RULES = {
    "branch_name_pattern", "code_scanning", "commit_author_email_pattern", "commit_message_pattern",
    "committer_email_pattern", "creation", "deletion", "deployments", "file_extension_restriction",
    "file_path_restriction", "max_file_path_length", "max_file_size", "merge_queue", "non_fast_forward",
    "pull_request", "required_deployments", "required_linear_history", "required_signatures", "update",
    "copilot_code_review", "required_code_scanning", "required_code_quality",
}
REVIEWS_LIB = Path(__file__).with_name("github-reviews-lib.sh")

META_QUERY = """query PrdkitPRFacts($owner:String!, $name:String!, $number:Int!) {
  repository(owner:$owner,name:$name) { nameWithOwner pullRequest(number:$number) {
    number state isDraft headRefOid baseRefOid baseRefName mergeable mergeStateStatus
    baseRef { name branchProtectionRule {
      requiresStatusChecks requiredStatusCheckContexts requiredStatusChecks { context app { databaseId } }
    } }
  } }
}"""
CHECK_QUERY = """query PrdkitCheckFacts($owner:String!, $name:String!, $number:Int!, $endCursor:String) {
  repository(owner:$owner,name:$name) { nameWithOwner pullRequest(number:$number) {
    number headRefOid baseRefName commits(last:1) { nodes { commit { oid
      statusCheckRollup { contexts(first:100,after:$endCursor) {
        totalCount pageInfo { hasNextPage endCursor }
        nodes { __typename
          ... on CheckRun { id name status conclusion detailsUrl permalink
            checkSuite { app { databaseId } } isRequired(pullRequestNumber:$number)
          }
          ... on StatusContext { id context state targetUrl isRequired(pullRequestNumber:$number) }
        }
      } }
    } } }
  } }
}"""


class Refusal(Exception):
    pass


class TimedOut(Exception):
    pass


def require(condition, reason):
    if not condition:
        raise Refusal(reason)


def field(obj, key, kind, *, nullable=False):
    require(isinstance(obj, dict) and key in obj, f"missing {key}")
    value = obj[key]
    require(type(value) is kind or (nullable and value is None), f"invalid {key}")
    return value


def text(obj, key, *, nullable=False):
    value = field(obj, key, str, nullable=nullable)
    require(value is None or (value.strip() and value.isprintable()), f"invalid {key}")
    return value


def enum(obj, key, candidates):
    value = text(obj, key)
    require(value in candidates, f"unexpected {key}: {value}")
    return value


def sha(obj, key):
    value = text(obj, key)
    require(SHA.fullmatch(value), f"invalid {key}: expected a full commit SHA")
    return value


def positive_id(obj, key):
    value = field(obj, key, int)
    require(value > 0, f"invalid {key}")
    return value


def app_id(obj, key):
    app = field(obj, key, dict, nullable=True)
    return None if app is None else positive_id(app, "databaseId")


def link(obj, key):
    url = text(obj, key, nullable=True)
    if url is not None:
        parsed = urlsplit(url)
        require(parsed.scheme in {"http", "https"} and parsed.hostname and not parsed.username
                and not parsed.password and not any(c.isspace() for c in url), f"invalid {key} URL")
    return url


def json_load(data, label):
    def unique(pairs):
        obj = {}
        for key, value in pairs:
            require(key not in obj, f"{label}: duplicate JSON key {key!r}")
            obj[key] = value
        return obj

    def invalid(token):
        raise Refusal(f"{label}: invalid JSON constant {token}")

    try:
        return json.loads(data, object_pairs_hook=unique, parse_constant=invalid)
    except (ValueError, RecursionError) as exc:
        raise Refusal(f"{label}: expected complete JSON from gh ({exc})") from exc


def graphql_data(raw, label):
    require(isinstance(raw, dict) and "errors" not in raw, f"{label}: GraphQL errors")
    return field(raw, "data", dict)


def collect_pages(read_page):
    cursor = ""
    seen = set()
    total = None
    items = []
    while True:
        page = read_page(cursor)
        if page is None and not cursor:
            return []
        count = field(page, "totalCount", int)
        batch = field(page, "nodes", list)
        info = field(page, "pageInfo", dict)
        more = field(info, "hasNextPage", bool)
        end = text(info, "endCursor", nullable=True)
        require(0 <= count and (total is None or total == count) and len(batch) <= 100,
                "inconsistent GitHub page count; retry the snapshot")
        total = count
        items.extend(batch)
        if not more:
            require(len(items) == total, "incomplete GitHub pagination; retry")
            return items
        require(batch and end and end not in seen and len(items) < total,
                "missing or non-advancing GitHub page; retry")
        seen.add(end)
        cursor = end


class GitHub:
    def __init__(self, repo, number, deadline=None):
        self.deadline = deadline
        self.env = {**os.environ, "GH_HOST": "github.com", "GH_PROMPT_DISABLED": "1",
                    "GH_NO_UPDATE_NOTIFIER": "1", "GH_FORCE_TTY": "", "NO_COLOR": "1"}
        if repo is None:
            data = self.get("repository lookup", ["gh", "repo", "view", "--json", "nameWithOwner,url"])
            repo = text(data, "nameWithOwner")
            require(text(data, "url").lower() == f"https://github.com/{repo}".lower(),
                    "default repository is not on github.com; pass --repo OWNER/NAME")
        require(REPO.fullmatch(repo) and not {".", ".."}.intersection(repo.split("/")),
                "pass a github.com repository as --repo OWNER/NAME")
        self.repo, self.number = repo, number

    def remaining(self):
        left = None if self.deadline is None else self.deadline - time.monotonic()
        if left is not None and left <= 0:
            raise TimedOut
        return left

    def run(self, label, argv, *, data=None, output=subprocess.PIPE):
        remaining = self.remaining()
        try:
            proc = subprocess.run(argv, input=b"" if data is None else data, stdout=output,
                                  stderr=subprocess.PIPE, env=self.env,
                                  timeout=60 if remaining is None else min(60, remaining))
        except subprocess.TimeoutExpired as exc:
            self.remaining()
            raise Refusal(f"{label}: gh request timed out; check connectivity and retry") from exc
        except OSError as exc:
            raise Refusal(f"{label}: cannot run {argv[0]} ({exc}); check PATH") from exc
        if proc.returncode:
            lines = proc.stderr.decode("utf-8", "replace").strip().splitlines()
            detail = f": {clean(lines[0], 240)}" if lines else ""
            raise Refusal(f"{label}: command exited {proc.returncode}{detail}; check gh access and retry")
        self.remaining()
        return proc.stdout

    def get(self, label, argv):
        return json_load(self.run(label, argv), label)

    def api(self, suffix, *args):
        return ["gh", "api", "--hostname", "github.com", f"repos/{self.repo}/{suffix}", *args]

    def graphql(self, label, query, cursor=""):
        owner, name = self.repo.split("/")
        argv = ["gh", "api", "graphql", "--hostname", "github.com", "-f", f"query={query}",
                "-f", f"owner={owner}", "-f", f"name={name}", "-F", f"number={self.number}"]
        if cursor:
            argv += ["-f", f"endCursor={cursor}"]
        repository = field(graphql_data(self.get(label, argv), label), "repository", dict)
        require(text(repository, "nameWithOwner").lower() == self.repo.lower(),
                f"{label}: returned repository does not match --repo")
        pr = field(repository, "pullRequest", dict)
        require(field(pr, "number", int) == self.number, f"{label}: returned a different PR number")
        return pr

    def metadata(self, expected=None):
        pr = self.graphql("PR metadata", META_QUERY)
        head = sha(pr, "headRefOid")
        if expected is not None and head != expected:
            raise Refusal(f"stale head: PR #{self.number} no longer points to {expected}; read the PR again")
        base = text(pr, "baseRefName")
        ref = field(pr, "baseRef", dict, nullable=True)
        require(ref is not None and text(ref, "name") == base,
                "PR base branch is missing or changed; cannot inventory required checks")
        classic = field(ref, "branchProtectionRule", dict, nullable=True)
        return ({
            "repo": self.repo, "number": self.number, "head_sha": head, "base_ref": base,
            "base_sha": sha(pr, "baseRefOid"), "state": enum(pr, "state", {"OPEN", "CLOSED", "MERGED"}),
            "draft": field(pr, "isDraft", bool),
            "mergeable": enum(pr, "mergeable", {"MERGEABLE", "CONFLICTING", "UNKNOWN"}),
            "merge_state": enum(pr, "mergeStateStatus", MERGE_STATES),
        }, classic)

    def branch_rules(self, base):
        # A missing Link header does not prove a full page was the last page.
        # Require an explicit short (possibly empty) page before accepting this list.
        endpoint = f"rules/branches/{quote(base, safe='')}"
        size = 100
        max_pages = 10
        seen = set()
        for number in range(1, max_pages + 1):
            label = f"active base branch rules page {number}"
            page = self.get(label, self.api(f"{endpoint}?per_page={size}&page={number}"))
            require(isinstance(page, list) and len(page) <= size,
                    f"{label}: expected a rule array of at most {size} entries")
            entries = {json.dumps(rule, sort_keys=True) for rule in page}
            require(len(entries) == len(page) and not seen.intersection(entries),
                    f"{label}: repeated rules; retry a complete branch rule inventory")
            seen.update(entries)
            yield from page
            if len(page) < size:
                return
        raise Refusal(f"branch rules page budget exhausted ({max_pages} pages of {size}) without a short page; "
                      "inspect the active base branch rules in GitHub before retrying")

    def requirements(self, base, classic):
        required = {}

        def add(name, source):
            prior = required.get(name)
            require(prior is None or source is None or prior == source,
                    f"conflicting required publishers for {name!r}; inspect base branch rules")
            required[name] = source if source is not None else prior

        if classic is not None and field(classic, "requiresStatusChecks", bool):
            contexts = field(classic, "requiredStatusCheckContexts", list, nullable=True)
            checks = field(classic, "requiredStatusChecks", list, nullable=True)
            require(contexts or checks, "classic branch protection does not enumerate required checks")
            for name in contexts or []:
                add(text({"context": name}, "context"), None)
            for check in checks or []:
                add(text(check, "context"), app_id(check, "app"))

        for rule in self.branch_rules(base):
            kind = text(rule, "type")
            if kind in OTHER_BRANCH_RULES:
                continue
            require(kind == "required_status_checks",
                    f"unsupported base branch rule {kind!r}; inspect GitHub for required checks")
            params = field(rule, "parameters", dict)
            checks = field(params, "required_status_checks", list)
            require(checks, "required status check rule has no contexts")
            for check in checks:
                name = text(check, "context")
                source = field(check, "integration_id", int, nullable=True) \
                    if "integration_id" in check else None
                require(source is None or source == -1 or source > 0,
                        "invalid integration_id in required status check rule")
                add(name, source if source != -1 else None)
        return required

    def checks(self, meta, required):
        def read(cursor):
            pr = self.graphql("exact-head checks", CHECK_QUERY, cursor)
            require(sha(pr, "headRefOid") == meta["head_sha"],
                    "stale head: PR changed while reading checks; read the PR again")
            require(text(pr, "baseRefName") == meta["base_ref"], "PR target changed; read the PR again")
            commits = field(field(pr, "commits", dict), "nodes", list)
            require(len(commits) == 1, "GitHub did not return one PR head commit")
            commit = field(commits[0], "commit", dict)
            require(sha(commit, "oid") == meta["head_sha"],
                    "GitHub check rollup is not for the PR head; refusing alternate commit checks")
            rollup = field(commit, "statusCheckRollup", dict, nullable=True)
            return None if rollup is None else field(rollup, "contexts", dict)

        found = {}
        seen = set()
        for node in collect_pages(read):
            identity = text(node, "id")
            require(identity not in seen, "duplicated check page entry; retry the snapshot")
            seen.add(identity)
            kind = enum(node, "__typename", {"CheckRun", "StatusContext"})
            necessary = field(node, "isRequired", bool)
            name = text(node, "name" if kind == "CheckRun" else "context")
            if name not in required:
                require(not necessary, f"GitHub reports unaccounted required check {name!r}; inspect base rules")
                continue
            if kind == "CheckRun":
                source = app_id(field(node, "checkSuite", dict), "app")
            else:
                source = None  # The legacy status API does not expose an integration ID.
            if required[name] is not None and required[name] != source:
                require(not necessary, f"cannot verify the required publisher of {name!r}; inspect base rules")
                continue
            require(necessary, f"GitHub disagrees with the base requirement for {name!r}; retry or inspect rules")
            require(name not in found, f"ambiguous required check {name!r}; use distinct check names")
            if kind == "CheckRun":
                status = enum(node, "status", CHECK_STATES)
                conclusion = text(node, "conclusion", nullable=True)
                require((status == "COMPLETED" and conclusion in CONCLUSIONS)
                        or (status != "COMPLETED" and conclusion is None),
                        f"unexpected status or conclusion for {name!r}")
                url = link(node, "detailsUrl") or link(node, "permalink")
            else:
                state = enum(node, "state", {"EXPECTED", "PENDING", "SUCCESS", "ERROR", "FAILURE"})
                status, conclusion = (state, None) if state in {"EXPECTED", "PENDING"} else ("COMPLETED", state)
                url = link(node, "targetUrl")
            facts = {"name": name, "status": status, "conclusion": conclusion, "link": url}
            found[name] = (facts, identity, source)
        return [
            found.get(name, ({"name": name, "status": "NOT_STARTED", "conclusion": None, "link": None},
                             None, None))
            for name in sorted(required)
        ]

    def reviews(self, meta):
        def read(cursor):
            argv = ["bash", "-c", 'source "$1" && github_review_page "$2" "$3" "$4"', "reviews",
                    str(REVIEWS_LIB), self.repo, str(self.number), cursor]
            data = graphql_data(self.get("native reviews", argv), "native reviews")
            repository = field(data, "repository", dict)
            require(text(repository, "nameWithOwner").lower() == self.repo.lower(),
                    "review page is for a different repository; retry")
            pr = field(repository, "pullRequest", dict)
            require(field(pr, "number", int) == self.number and sha(pr, "headRefOid") == meta["head_sha"]
                    and text(pr, "baseRefName") == meta["base_ref"] and text(pr, "state") == meta["state"],
                    "review page is not bound to the same PR snapshot; retry")
            return field(pr, "reviews", dict)

        records, seen = [], set()
        for review in collect_pages(read):
            rid = field(review, "fullDatabaseId", str)
            require(re.fullmatch(r"[1-9][0-9]*", rid) and rid not in seen,
                    "invalid or duplicated native review ID")
            seen.add(rid)
            state = enum(review, "state", {"PENDING", "APPROVED", "COMMENTED", "DISMISSED", "CHANGES_REQUESTED"})
            submitted = text(review, "submittedAt", nullable=True)
            if state == "PENDING":
                require(submitted is None, "pending review unexpectedly has a submission time")
                continue
            require(submitted and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", submitted),
                    "submitted review is missing a GitHub UTC timestamp")
            datetime.datetime.fromisoformat(submitted)
            account = positive_id(field(review, "author", dict), "databaseId")
            fact = {"account_id": account, "review_id": rid, "state": state, "submitted_at": submitted,
                    "commit_sha": sha(field(review, "commit", dict), "oid")}
            records.append(f"{account}\t{submitted}\t{json.dumps(fact)}\n")
        argv = ["bash", "-c", 'source "$1" && github_review_latest', "reviews", str(REVIEWS_LIB)]
        latest = self.run("native review reduction", argv, data="".join(records).encode("ascii"))
        facts = sorted(map(json.loads, latest.splitlines()), key=lambda r: r["account_id"])
        return {"latest_by_account": facts, "blocking": [r for r in facts if r["state"] == "CHANGES_REQUESTED"]}

    def confirm(self, meta, required, checks):
        current, classic = self.metadata(meta["head_sha"])
        require(current["base_ref"] == meta["base_ref"] and current["state"] == meta["state"],
                "PR state or target changed; take a fresh snapshot")
        require(self.requirements(current["base_ref"], classic) == required,
                "base branch check requirements changed during the read; retry")
        # Compare the entire named inventory, including check identities and the
        # link/publisher used for diagnostics. Unrelated optional checks don't matter.
        unchanged = self.checks(current, required) == checks
        # Recheck the PR binding after the last check request, even on a mismatch.
        final, final_classic = self.metadata(meta["head_sha"])
        require(final["base_ref"] == current["base_ref"] and final["state"] == current["state"]
                and final_classic == classic, "PR target, state or classic rules changed; retry")
        return final, unchanged

    def diagnose(self, check, app, head, signatures):
        """Return a verified Actions job ID (if eligible), a log excerpt, and whether it matched."""
        url = check["link"] or ""
        pattern = (re.escape(f"https://github.com/{self.repo}/actions/runs/")
                   + r"([1-9][0-9]*)/job/([1-9][0-9]*)")
        match = re.fullmatch(pattern, url, re.ASCII | re.IGNORECASE)
        if app != 15368 or not match or check["conclusion"] not in {"FAILURE", "TIMED_OUT"}:
            return None, ["(no supported GitHub Actions job log for this result)"], False
        run, job = map(int, match.groups())
        path = f"actions/jobs/{job}"
        record = self.get("Actions job lookup", self.api(path))
        require(positive_id(record, "id") == job and positive_id(record, "run_id") == run
                and sha(record, "head_sha") == head and text(record, "html_url").lower() == url.lower()
                and text(record, "name") == check["name"] and text(record, "status") == "completed"
                and text(record, "conclusion").upper() == check["conclusion"],
                "Actions job no longer matches the failed exact-head check; retry")
        with tempfile.TemporaryFile() as logfile:
            # gh normally refuses color escapes, which are common in Actions logs.
            # Keep raw bytes private and sanitize every line before printing it.
            self.run("Actions failure log", self.api(f"{path}/logs", "--include", "--allow-escape-sequences"),
                     output=logfile)
            require(logfile.tell() > 0, "GitHub returned an empty Actions job log; inspect the check link")
            with mmap.mmap(logfile.fileno(), 0, access=mmap.ACCESS_READ) as log:
                start = log_body_offset(log)
                matched = any(log.find(signature, start) >= 0 for signature in signatures)
                return job, log_excerpt(log, start), matched

    def rerun_job(self, job):
        # A successful POST has HTTP 201; the API need not include a JSON body.
        reply = self.run("Actions job rerun", self.api(f"actions/jobs/{job}/rerun", "--method", "POST", "--include"))
        require(http_status(reply.split(b"\n", 1)[0], b"201"),
                "unexpected Actions job rerun response; inspect the job before retrying")


def clean(value, limit):
    return "".join(c if c.isprintable() else "?" for c in value[:limit])


def http_status(line, expected):
    parts = line.split()
    return len(parts) >= 2 and parts[0] in {b"HTTP/1.1", b"HTTP/2", b"HTTP/2.0"} and parts[1] == expected


def log_body_offset(log):
    header = re.search(rb"\r?\n\r?\n", log[:32_768])
    require(header is not None, "Actions job log response is missing HTTP headers; inspect the check link")
    body = header.end()
    headers = log[:header.start()].splitlines()
    require(headers, "Actions job log response is missing an HTTP status; inspect the check link")
    status, *fields = headers
    types = [line.split(b":", 1)[1].split(b";", 1)[0].strip().lower()
             for line in fields if line.lower().startswith(b"content-type:")]
    lengths = [line.split(b":", 1)[1].strip()
               for line in fields if line.lower().startswith(b"content-length:")]
    require(http_status(status, b"200") and types == [b"text/plain"] and body < len(log)
            and b"\0" not in log[body:body + 4096],
            "expected a nonempty plain-text Actions job log; inspect the check link")
    require(not lengths or lengths == [str(len(log) - body).encode("ascii")],
            "incomplete Actions job log; inspect the check link")
    return body


def log_excerpt(log, start):
    # Actions often appends cleanup steps after the error. Prefer the first
    # GitHub error annotation near the tail when one is available.
    tail = log[max(start, len(log) - 32_768):].decode("utf-8", "replace").splitlines()
    marker = next((i for i, line in enumerate(tail) if "##[error]" in line or "::error" in line), None)
    excerpt = tail[-12:] if marker is None else tail[max(0, marker - 3): marker + 9]
    return [clean(line, 300) for line in excerpt]


def state(host):
    meta, classic = host.metadata()
    required = host.requirements(meta["base_ref"], classic)
    checks = host.checks(meta, required)
    reviews = host.reviews(meta)
    final, unchanged = host.confirm(meta, required, checks)
    require(unchanged, "required checks changed during the read; retry the snapshot")
    require(final["base_sha"] == meta["base_sha"] and final["draft"] == meta["draft"],
            "PR base commit or draft status changed; retry")
    print(json.dumps({**final, "required_checks": [check for check, _, _ in checks], "reviews": reviews,
                      "fetched_at": datetime.datetime.now(datetime.UTC).isoformat(timespec="seconds").replace("+00:00", "Z")}))
    return 0


def wait(host, head, interval, signatures):
    rerun = {}
    binding = None
    while True:
        meta, classic = host.metadata(head)
        required = host.requirements(meta["base_ref"], classic)
        require(required, "PR has no required status checks on its base branch; inspect branch rules")
        current = (meta["base_ref"], required)
        require(binding is None or current == binding, "PR target or required checks changed; start a fresh wait")
        binding = current
        checks = host.checks(meta, required)
        terminal = all(check["status"] == "COMPLETED" and rerun.get(check["name"]) != identity
                       for check, identity, _ in checks)
        if terminal:
            failures = []
            for check, identity, app in checks:
                if check["conclusion"] != "SUCCESS":
                    name = check["name"]
                    job, excerpt, matched = host.diagnose(check, app, head, signatures if name not in rerun else ())
                    failures.append((check, identity, job, excerpt, matched))
            _, unchanged = host.confirm(meta, required, checks)
            if not unchanged:
                time.sleep(min(interval, host.remaining()))
                continue
            retried = False
            for check, identity, job, _, matched in failures:
                name = check["name"]
                if matched and job is not None and name not in rerun:
                    host.rerun_job(job)
                    rerun[name] = identity
                    retried = True
                    print(f"Rerun requested: {name} [{head}] {check['link']}", file=sys.stderr, flush=True)
                    # GitHub may also rerun dependents. Observe the new results
                    # before submitting another request, even in the same workflow.
                    break
            if not retried:
                for check, _, _, excerpt, _ in failures:
                    print(f"{check['name']}\t{check['conclusion']}\t{check['link'] or '-'}")
                    for line in excerpt:
                        print(f"  | {line}")
                return 1 if failures else 0
        time.sleep(min(interval, host.remaining()))


def cli_int(value):
    if re.fullmatch(r"[1-9][0-9]{0,8}", value):
        return int(value)
    raise argparse.ArgumentTypeError("expected a positive integer (1–999999999)")


def main(tool, argv):
    parser = argparse.ArgumentParser(prog=f"{tool}.sh", allow_abbrev=False,
                                     description="Exact-head GitHub evidence only; never a merge verdict.")
    parser.add_argument("--repo", metavar="OWNER/NAME", help="default: gh's current github.com repository")
    parser.add_argument("pr_number", type=cli_int)
    if tool == "ci-wait":
        parser.add_argument("--head", required=True, metavar="SHA", help="full expected PR head SHA")
        parser.add_argument("--timeout", type=cli_int, default=600, metavar="SECONDS",
                            help="overall timeout (default: 600)")
        parser.add_argument("--interval", type=cli_int, default=10, metavar="SECONDS",
                            help="poll interval (default: 10)")
        parser.add_argument("--rerun-on", type=Path, metavar="FILE",
                            help="opt in: one literal UTF-8 failure-log substring per line")
    args = parser.parse_args(argv)
    try:
        if tool == "pr-state":
            return state(GitHub(args.repo, args.pr_number))
        require(SHA.fullmatch(args.head), "--head must be a full lowercase 40-hex SHA")
        signatures = ()
        if args.rerun_on is not None:
            require(args.rerun_on.is_file(), "--rerun-on must name a readable UTF-8 file")
            signatures = tuple(line.encode("utf-8") for line in args.rerun_on.read_text(encoding="utf-8").split("\n")
                               if line.strip())
            require(signatures and all(b"\0" not in s for s in signatures), "--rerun-on has no valid signatures")
        return wait(GitHub(args.repo, args.pr_number, time.monotonic() + args.timeout),
                    args.head, args.interval, signatures)
    except TimedOut:
        print(f"{tool}.sh: timed out waiting for required checks on {args.head}", file=sys.stderr)
        return 3
    except (Refusal, OSError, ValueError, RecursionError) as exc:
        print(f"{tool}.sh: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2:]))
