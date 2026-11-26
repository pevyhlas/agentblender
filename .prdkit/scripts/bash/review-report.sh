#!/usr/bin/env bash
# Local report formatting only: no publication, review authority or merge decision.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage:
  review-report.sh scaffold --reviewer NAME --harness HARNESS --base REV --head REV
                            --scope TEXT [--since REV]
  review-report.sh lint [--strict] [--rigor light|full] [--scaffold ORIGINAL] REPORT
  review-report.sh normalize REPORT

scaffold writes a header and an empty Findings section to stdout, with no verdict.
Use the report's reviewed revisions; Since defaults to "initial". NAME must be in
the project's Review registry, and Model comes from its tier map for NAME and HARNESS.

lint takes an LF text file. Put other report context before the ## Findings section.
Leave that section empty for zero findings. Otherwise use one block per finding:

  - High: path/to/file:42
    Scenario: What happens and why it matters.
    Evidence: What demonstrates it (or where to find the evidence).

List Critical, High, Medium and Low findings in that order; use positive line
numbers. The last two lines, with no text or blank lines after them, must be:

  Verdict: PASS
  Counts: 0 critical, 0 high, 0 medium, 0 low

Use PASS or FAIL and the actual counts. Critical and high block PASS; medium also
blocks under full rigor. Supply trusted policy's --rigor, or lint uses the installed
project's constitution. A PASS with medium findings needs known rigor.

Stamps are authoritative only in the opening scaffold header block; later
reviewer content is not a source of stamps. The coordinator saves the original
and passes --scaffold to require an exact match; without it, a report may omit
the block, but an opening "# Review report" must start a complete header.
Neither an optional standalone header nor a lint pass without --scaffold verifies
stamp values; only --scaffold against the coordinator's saved original does.

normalize writes a copy to stdout, changing only the explicit Verdict: line
immediately before the final Counts: line. It does not infer a verdict from counts.
Input is limited to 1 MiB (1,048,576 bytes). Values are matched case-insensitively,
ignoring surrounding whitespace:

  PASS | PASSED | APPROVE | APPROVED | ACCEPT | ACCEPTED
       | LGTM | SOUND                                          -> PASS
  FAIL | FAILED | REJECT | REJECTED | REQUEST CHANGES
       | CHANGES REQUESTED | CHANGES_REQUESTED | CHANGES_REQUIRED -> FAIL

Exit 0: ran (lint diagnostics on stdout); 1: lint diagnostics with --strict;
2: could not run, including normalize without a recognized explicit verdict.
EOF
}

die() { printf 'review-report.sh: %s\n' "$*" >&2; exit 2; }

mode="${1:-}"
case "$mode" in
    -h|--help) usage; exit ;;
    scaffold|lint|normalize) shift ;;
    *) die 'choose scaffold, lint or normalize; see --help' ;;
esac
case "${1:-}" in -h|--help) usage; exit ;; esac

SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)" || exit 2
source "$SCRIPT_DIR/common.sh" || die 'cannot load common.sh; reinstall the kit'
source "$SCRIPT_DIR/spec-lib.sh" || die 'cannot load spec-lib.sh; reinstall the kit'
source "$SCRIPT_DIR/review-policy-lib.sh" || die 'cannot load review-policy-lib.sh; reinstall the kit'
export LC_ALL=C

# Share fence handling between report, registry and tier-map reads without
# changing the default parsers. Backtick info strings cannot contain backticks.
REPORT_FENCE_AWK='function skip_fence(line,    marker, ending, opener) {
    marker = line; sub(/^ ? ? ?/, "", marker)
    if (report_fence != "") {
        ending = marker; sub(/[ \t]+$/, "", ending)
        if (length(ending) >= length(report_fence) && (ending ~ /^`+$/ || ending ~ /^~+$/) &&
            substr(ending, 1, 1) == substr(report_fence, 1, 1)) report_fence = ""
        return 1
    }
    if (match(marker, /^```+|^~~~+/)) {
        opener = substr(marker, 1, RLENGTH)
        if (substr(opener, 1, 1) == "~" || !index(substr(marker, RLENGTH + 1), "`")) {
            report_fence = opener
            return 1
        }
    }
    return 0
}'
REPORT_SKIP_AWK="$REPORT_FENCE_AWK"'
function spec_skip(line) {
    if (!report_comment && skip_fence(line)) return 1
    if (report_comment || line ~ /^[ \t]*<!--/) { report_comment = !index(line, "-->"); return 1 }
    return 0
}'

scaffold() {
    local reviewer='' harness='' base='' head='' since='' scope='' value seen=' ' key
    local root registry eligibility def tier problems
    while [ "$#" -gt 0 ]; do
        key="$1"; value="${2:-}"
        case "$key" in
            --reviewer|--harness|--base|--head|--since|--scope) ;;
            *) die "unknown scaffold option '$key'; see --help" ;;
        esac
        case "$seen" in *" $key "*) die "supply $key only once" ;; esac
        case "$value" in --*|*[[:cntrl:]]*) die "$key needs a single-line value" ;; esac
        [[ "$value" = *[![:space:]]* ]] || die "$key needs a nonblank value"
        seen="$seen$key "
        case "$key" in
            --reviewer) reviewer="$value" ;;
            --harness) harness="$value" ;;
            --base) base="$value" ;;
            --head) head="$value" ;;
            --since) since="$value" ;;
            --scope) scope="$value" ;;
        esac
        shift 2
    done
    [ -n "$reviewer" ] && [ -n "$harness" ] && [ -n "$base" ] && [ -n "$head" ] && [ -n "$scope" ] ||
        die 'scaffold requires --reviewer, --harness, --base, --head and --scope; see --help'
    [[ "$reviewer" =~ ^[a-z0-9][a-z0-9_.-]*$ ]] || die '--reviewer must be a lowercase registry identity'

    root="$(get_repo_root)" || die 'cannot locate the installed project'
    require_consumer_root "$root" review-report.sh
    source "$SCRIPT_DIR/install-lib.sh" || die 'cannot load install-lib.sh; reinstall the kit'
    source "$SCRIPT_DIR/agent-tiers-lib.sh" || die 'cannot load agent-tiers-lib.sh; reinstall the kit'
    case "$harness" in *[!a-z]*) die "--harness must be one of: $AT_HARNESSES" ;; esac
    case " $AT_HARNESSES " in *" $harness "*) ;; *) die "--harness must be one of: $AT_HARNESSES" ;; esac

    verify_lf_file "$root/.prdkit/memory/agents.md" ||
        die 'expected readable LF text in .prdkit/memory/agents.md; register the reviewer and configure the tier map there'
    registry="$(SPEC_SKIP_AWK="$REPORT_SKIP_AWK" review_registry < "$root/.prdkit/memory/agents.md")" ||
        die 'cannot read the Review registry in .prdkit/memory/agents.md'
    eligibility="$(review_eligibility "$registry" "$reviewer")" || die 'cannot check reviewer eligibility'
    [ "$eligibility" = reviewer ] ||
        die "'$reviewer' is $eligibility; expected one reviewer entry in the Review registry in .prdkit/memory/agents.md"

    def="$root/.prdkit/agents/$reviewer.md"
    [ -f "$def" ] && verify_lf_file "$def" && tier="$(at_fm_value "$def" tier)" && [ -n "$tier" ] ||
        die "set tier: in the neutral definition .prdkit/agents/$reviewer.md"
    at_def_harnesses "$def"
    [ -z "$AT_DH_ERR" ] || die ".prdkit/agents/$reviewer.md: $AT_DH_ERR"
    if $AT_DH_SET; then
        case " $AT_DH_LIST " in *" $harness "*) ;; *) die "'$reviewer' does not target '$harness' in its neutral definition" ;; esac
    fi
    at_load "$root" "$REPORT_FENCE_AWK" || die 'cannot load the configured tier map'
    problems="$(at_lint_map)" || die 'cannot check the configured tier map'
    [ -z "$problems" ] || die "$problems"
    # shellcheck disable=SC2153 # Set by at_resolve in agent-tiers-lib.sh.
    at_resolve "$reviewer" "$tier" "$harness" || die "cannot resolve Model for '$reviewer': $AT_R_ERR"
    case "$AT_R_MODEL" in *[[:cntrl:]]*) die 'resolved Model must be one line of text; fix the tier map' ;; esac

    printf '# Review report\n\nReviewer: %s\nBase: %s\nHead: %s\nSince: %s\nScope: %s\nModel: %s\n\n## Findings\n' \
        "$reviewer" "$base" "$head" "${since:-initial}" "$scope" "$AT_R_MODEL" ||
        die 'cannot write report scaffold'
}

if [ "$mode" = scaffold ]; then scaffold "$@"; exit; fi

strict=false; rigor=''; report=''; original=''
while [ "$#" -gt 0 ]; do
    case "$mode:$1" in
        lint:--strict) strict=true ;;
        lint:--rigor)
            [ -z "$rigor" ] || die 'supply --rigor only once'
            rigor="${2:-}"
            case "$rigor" in light|full) ;; *) die '--rigor needs light or full' ;; esac
            shift ;;
        lint:--scaffold)
            [ -z "$original" ] || die 'supply --scaffold only once'
            original="${2:-}"
            case "$original" in ''|-*) die '--scaffold needs the original scaffold file' ;; esac
            shift ;;
        *:-*) die "unknown $mode option '$1'; see --help (prefix local paths starting with - with ./)" ;;
        *) [ -z "$report" ] || die "$mode takes one report file; see --help"
            report="$1" ;;
    esac
    shift
done
[ -n "$report" ] || die "$mode needs a report file; see --help"
[ -f "$report" ] || die "cannot read '$report' as a regular file"
if [ "$mode" = normalize ]; then
    limit=1048576
    local_copy="$(mktemp)" || die 'cannot allocate temporary report storage'
    trap 'rm -f "$local_copy"' EXIT
    # Read at most one byte beyond the limit, even if the input grows during the read.
    head -c "$((limit + 1))" < "$report" > "$local_copy" ||
        die "cannot copy '$report' to temporary storage"
    bytes="$(wc -c < "$local_copy")" || die "cannot measure '$report'"
    [ "$bytes" -le "$limit" ] || die "'$report' exceeds the normalize limit of 1 MiB (1,048,576 bytes)"
else
    local_copy="$report"
fi
if ! verify_lf_file "$local_copy" allow-empty; then
    die "cannot read '$report' as an LF-terminated text file"
fi
if [ -n "$original" ]; then
    if ! [ -f "$original" ] || ! verify_lf_file "$original"; then
        die "cannot read '$original' as an LF-terminated original scaffold"
    fi
fi

if [ "$mode" = lint ] && [ -z "$rigor" ]; then
    root="$(get_repo_root)" || die 'cannot locate project policy; supply --rigor'
    constitution="$root/.prdkit/memory/constitution.md"
    rigor=unknown
    if [ -f "$constitution" ] && verify_lf_file "$constitution"; then
        rigor="$(read_rigor_text < "$constitution")" || die 'cannot read Rigor; supply --rigor'
    fi
fi

# Keep two lines in reserve for the trailer. Use awk status 3 for a completed
# lint with findings or a normalization refusal. cat checks writes that some
# awks fail to report (e.g. a full output device).
# shellcheck disable=SC2094 # REPORT_COPY reopens the input read-only.
REPORT_SCAFFOLD="$original" REPORT_COPY="$local_copy" awk -v mode="$mode" -v rigor="$rigor" "$REPORT_SKIP_AWK"'
    BEGIN {
        split("Critical High Medium Low", severities)
        for (i = 1; i <= 4; i++) rank[severities[i]] = i
        split("Reviewer Base Head Since Scope Model", labels)
        original = ENVIRON["REPORT_SCAFFOLD"]
        if (original != "" && !read_scaffold(original)) { invalid_scaffold = 1; exit 4 }
    }
    function valid_header_line(n, line,   prefix) {
        if (n == 1) return line == "# Review report"
        if (n == 2 || n == 9) return line == ""
        prefix = labels[n - 2] ": "
        return index(line, prefix) == 1 && substr(line, length(prefix) + 1) ~ /[^[:space:]]/
    }
    function read_scaffold(file,   line, i, extra) {
        for (i = 1; i <= 9; i++) {
            if ((getline line < file) != 1 || !valid_header_line(i, line)) return 0
            stamp[i] = line
        }
        if ((getline line < file) != 1 || line != "## Findings") return 0
        extra = getline line < file
        close(file)
        return extra == 0
    }
    function problem(n, text) { printf "line %d: %s\n", n, text; bad = 1 }
    function header_problem(n) {
        if (n >= 3 && n <= 8) {
            if (original != "") problem(n, "preserve the original scaffold line for " labels[n - 2] ":")
            else problem(n, "expected the " labels[n - 2] ": field at this position in the scaffold header")
        } else {
            if (original != "") problem(n, "preserve the original scaffold title and blank lines")
            else problem(n, "expected a blank line in the scaffold header")
        }
    }
    function finish_finding() {
        if (!start) return
        if (!scenario) problem(start, "finding needs a nonblank Scenario: line")
        if (!evidence) problem(start, "finding needs a nonblank Evidence: line")
    }
    function body(line, n, skip,   sep, severity, location) {
        if (!skip && line == "## Findings") {
            if (findings) problem(n, "keep exactly one ## Findings section")
            findings = 1
            return
        }
        if (!skip && line ~ /^(Verdict|Counts):/) {
            problem(n, "keep Verdict: and Counts: only in the final two lines")
            return
        }
        if (!findings && !skip && line ~ /^- (Critical|High|Medium|Low):/)
            problem(n, "move the finding under ## Findings")
        if (!findings || line ~ /^[ \t]*$/) return
        if (line ~ /^- / && !skip) {
            finish_finding()
            start = n; scenario = evidence = 0
            line = substr(line, 3)
            sep = index(line, ": ")
            severity = (sep ? substr(line, 1, sep - 1) : "")
            if (!(severity in rank))
                problem(n, "start a finding with - Critical:, - High:, - Medium: or - Low:")
            else {
                count[severity]++
                if (rank[severity] < previous_rank)
                    problem(n, "list findings in Critical, High, Medium, Low order")
                previous_rank = rank[severity]
            }
            location = (sep ? substr(line, sep + 2) : "")
            if (location !~ /^[^[:space:]].*:[1-9][0-9]*$/)
                problem(n, "give the finding a file:line location with a positive line number")
        } else if (start && !skip && line ~ /^  Scenario: [[:space:]]*[^[:space:]]/) {
            if (scenario++) problem(n, "give the finding only one Scenario: line")
        } else if (start && !skip && line ~ /^  Evidence: [[:space:]]*[^[:space:]]/) {
            if (evidence++) problem(n, "give the finding only one Evidence: line")
        } else problem(n, "expected a finding or its indented Scenario: or Evidence:; see --help")
    }
    function normalized(verdict) {
        sub(/^Verdict:[ \t]*/, "", verdict); sub(/[ \t]+$/, "", verdict)
        verdict = tolower(verdict)
        if (verdict ~ /^(pass|passed|approve|approved|accept|accepted|lgtm|sound)$/) return "PASS"
        if (verdict ~ /^(fail|failed|reject|rejected|request changes|changes requested|changes_requested|changes_required)$/) return "FAIL"
        return ""
    }
    {
        hidden = spec_skip($0)
        if (mode == "lint") {
            if (NR == 1) has_header = (original != "" || $0 == "# Review report")
            if (has_header && NR <= 9 && (original != "" ? $0 != stamp[NR] : !valid_header_line(NR, $0)))
                header_problem(NR)
            if (NR > 2) body(before, NR - 2, before_hidden)
        }
        before = last; before_hidden = last_hidden
        last = $0; last_hidden = hidden
    }
    END {
        if (invalid_scaffold) exit 4
        verdict_line = NR >= 2 && !before_hidden && before ~ /^Verdict:/
        counts_line = NR >= 1 && !last_hidden && last ~ /^Counts:/
        if (mode == "normalize") {
            verdict = (verdict_line && counts_line ? normalized(before) : "")
            if (verdict == "") exit 3
            # Replay the validated private copy only after the verdict is known.
            copy = ENVIRON["REPORT_COPY"]
            for (i = 1; i <= NR - 2; i++) {
                if ((getline line < copy) != 1) exit 5
                print line
            }
            print "Verdict: " verdict
            print last
            exit
        }
        if (original != "" || has_header)
            for (i = NR + 1; i <= 9; i++) header_problem(i)
        if (NR >= 2 && !verdict_line) body(before, NR - 1, before_hidden)
        if (NR >= 1 && !counts_line) body(last, NR, last_hidden)
        finish_finding()
        if (!findings) problem(1, "add a ## Findings section (empty if there are no findings)")
        if (!verdict_line || before !~ /^Verdict: (PASS|FAIL)$/)
            problem(NR < 2 ? 1 : NR - 1, "penultimate line must be exactly Verdict: PASS or Verdict: FAIL")
        expected = sprintf("Counts: %d critical, %d high, %d medium, %d low",
                           count["Critical"], count["High"], count["Medium"], count["Low"])
        if (!counts_line || last != expected)
            problem(NR ? NR : 1, "last line must match the findings: " expected)
        if (verdict_line && before == "Verdict: PASS") {
            if (count["Critical"] || count["High"] || (rigor == "full" && count["Medium"]))
                problem(NR - 1, "PASS is inconsistent with blocking findings")
            else if (count["Medium"] && rigor != "light")
                problem(NR - 1, "cannot verify PASS with medium findings; supply --rigor from trusted policy")
        }
        if (bad) exit 3
    }
' < "$local_copy" | cat
statuses=("${PIPESTATUS[@]}")
[ "${statuses[1]}" -eq 0 ] || die "cannot write $mode output; check the output destination"

case "${statuses[0]}:$mode" in
    0:*) exit 0 ;;
    3:lint) if $strict; then exit 1; else exit 0; fi ;;
    3:normalize) die 'expected a recognized Verdict: value immediately before the final Counts: line; see --help' ;;
    4:lint) die "'$original' must be the original unedited scaffold output" ;;
    *) die "cannot $mode '$report'; check the input and output" ;;
esac
