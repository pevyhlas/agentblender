#!/usr/bin/env bash
#
# retrospective.sh — Write a per-change retrospective from the local prdkit ledger,
# optionally enriched with out-of-band token/model usage.
#
# Usage:
#   retrospective.sh [<change-id>]
#
# With no argument the change id is the current branch name (it must look like
# 004-export-csv). The ledger's "feature" id is the change id; 0.x feature ids work too.
#
# Reads:
#   .prdkit/ledger/events.jsonl                    — the durable local event spool
#   .prdkit/retrospective/<change-id>.enrich.json  — OPTIONAL token/model usage
#
# Writes:
#   .prdkit/cache/retrospectives/<change-id>.md    — gitignored, never under specs/
#
# Ledger-only: bash + coreutils + awk/sed. Optional enrichment requires jq 1.6+.
# The ledger lines are single-line JSON (guaranteed by ledger-emit.sh).
# No token or cost number is ever invented — the token section is populated only
# from the enrichment file, and stays a clear "run the enricher" note without it.

set -euo pipefail

SCRIPT_DIR="$(CDPATH="" cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/common.sh"
REPO_ROOT="$(get_repo_root)"
REPO_ROOT="$(CDPATH="" cd "$REPO_ROOT" && pwd -P)"

TAB="$(printf '\t')"

# ===========================================================================
# Enrichment is parsed once into a validated snapshot. All consumers use that
# snapshot, never a second read of a file that could change after validation.
# ===========================================================================

die() {
    echo "[prdkit] $*" >&2
    exit 1
}

load_enrichment() {
    command -v jq >/dev/null 2>&1 || die "Enrichment requires jq 1.6+; install jq and re-run. Report unchanged."
    if ! ENRICH_DATA="$(jq -cse --arg feature "$FEATURE_ID" '
        def count: type == "number" and . >= 0 and . <= 999999999999 and floor == .;
        def counts: type == "object" and all(.[]; count);
        def optional($key; test): if has($key) then .[$key] | test else true end;
        if length != 1 then error("expected one enrichment object") else .[0] end
        | select(type == "object")
        | select(.schema == "prdkit.retrospective.enrich.v1" and .feature == $feature)
        | select(optional("sessions"; count))
        | select(all(.source, .generated_at, .coverage; . == null or type == "string"))
        | select(.notes == null or (.notes | type == "array" and all(.[]; type == "string")))
        | select(.cost_estimate_usd == null or (.cost_estimate_usd | type == "number" and . >= 0 and . <= 999999999999))
        | select(.tokens | type == "object" and (.total | count)
            and optional("by_type"; counts) and optional("by_model"; counts)
            and optional("by_effort"; counts) and optional("by_agent"; counts))
    ' "$ENRICH" 2>/dev/null)"; then
        die "Invalid enrichment: require matching schema/feature and bounded non-negative integer counts (0..999999999999). Report unchanged."
    fi
    enrich_present=1
}

# jq escapes embedded tabs/newlines in keys so they cannot become row delimiters.
enrich_flat_obj() {
    jq -r --arg key "$1" '.tokens[$key] // {} | to_entries[] | [.key, (.value | floor)] | @tsv' <<< "$ENRICH_DATA"
}

enrich_notes() {
    jq -r '.notes // [] | .[] | [.] | @tsv' <<< "$ENRICH_DATA"
}

# Globals populated from the enrichment file (defaults keep the recommendation
# logic safe when no enrichment exists).
enrich_present=0
reasoning_share=""
dominant_model=""
dominant_model_share=""

compute_enrich_signals() {
    # No file-controlled operand enters Bash arithmetic. The count bound also
    # keeps multiplication by 100 below jq IEEE-754 exact-integer limits.
    reasoning_share="$(jq -r '.tokens | select(.total > 0 and .by_type.reasoning != null) | (.by_type.reasoning * 100 / .total) | floor' <<< "$ENRICH_DATA")"
    dominant_model="$(jq -r '.tokens.by_model // {} | to_entries | max_by(.value) | select(. != null) | [.key] | @tsv' <<< "$ENRICH_DATA")"
    dominant_model_share="$(jq -r '.tokens | select(.total > 0) | .total as $total | .by_model // {} | to_entries | max_by(.value) | select(. != null) | (.value * 100 / $total) | floor' <<< "$ENRICH_DATA")"
}

render_enrichment() {
    local src sessions gen cov total cost note
    src="$(jq -r '[.source // "unknown"] | @tsv' <<< "$ENRICH_DATA")"
    sessions="$(jq -r '.sessions // "?"' <<< "$ENRICH_DATA")"
    gen="$(jq -r '[.generated_at // "unknown"] | @tsv' <<< "$ENRICH_DATA")"
    cov="$(jq -r '[.coverage // ""] | @tsv' <<< "$ENRICH_DATA")"
    total="$(jq -r '.tokens.total | floor' <<< "$ENRICH_DATA")"
    cost="$(jq -r '.cost_estimate_usd' <<< "$ENRICH_DATA")"

    echo "Source: ${src:-unknown} · Sessions: ${sessions:-?} · Generated: ${gen:-unknown}"
    echo
    echo "- **Total tokens:** ${total:-unknown}"
    echo
    echo "**By type**"
    enrich_flat_obj by_type | while IFS="$TAB" read -r k v; do
        [ -n "$k" ] && echo "- $k: $v"
    done
    echo
    echo "**By model**"
    enrich_flat_obj by_model | while IFS="$TAB" read -r k v; do
        [ -n "$k" ] && echo "- $k: $v"
    done
    echo
    echo "**By effort**"
    enrich_flat_obj by_effort | while IFS="$TAB" read -r k v; do
        [ -n "$k" ] && echo "- $k: $v"
    done
    echo
    echo "**By agent**"
    enrich_flat_obj by_agent | while IFS="$TAB" read -r k v; do
        [ -n "$k" ] && echo "- $k: $v"
    done
    echo
    if [ -z "$cost" ] || [ "$cost" = "null" ]; then
        echo "- **Cost estimate:** unavailable — subscription auth, no authoritative price; token counts are exact."
    else
        echo "- **Cost estimate:** \$$cost"
    fi
    if [ -n "$cov" ]; then
        echo "- **Coverage:** $cov"
    fi
    if [ -n "$(enrich_notes)" ]; then
        echo
        echo "Notes:"
        enrich_notes | while IFS= read -r note; do
            [ -n "$note" ] && echo "- $note"
        done
    fi
}

# ===========================================================================
# Resolve the change id and the report destination.
# ===========================================================================
[ "$#" -le 1 ] || die "Usage: retrospective.sh [<change-id>]"
if [ "$#" -eq 1 ]; then
    FEATURE_ID="$1"
else
    FEATURE_ID="$(git -C "$REPO_ROOT" symbolic-ref --quiet --short HEAD 2>/dev/null || true)"
    [ -n "$FEATURE_ID" ] || die "Could not resolve the current change; pass it explicitly: retrospective.sh <change-id>"
fi
[[ "$FEATURE_ID" =~ ^[0-9]+-[A-Za-z0-9]+(-[A-Za-z0-9]+)*$ ]] || die "Expected a bare change id such as 042-export."
# Every directory on the way to the report must be a physical directory inside this
# project, never a symlink that could redirect the write elsewhere.
OUT_DIR="$REPO_ROOT/.prdkit/cache/retrospectives"
for dir in "$REPO_ROOT/.prdkit" "$REPO_ROOT/.prdkit/cache" "$OUT_DIR"; do
    if [ ! -e "$dir" ] && [ ! -L "$dir" ]; then mkdir "$dir"; fi
    [[ -d "$dir" && ! -L "$dir" ]] || die "Report directory must be a physical directory: ${dir#"$REPO_ROOT"/}"
done
# Anchor writes to the checked directory, independent of later ancestor symlinks.
CDPATH="" cd -P "$OUT_DIR"
[ "$(pwd -P)" = "$OUT_DIR" ] || die "Report directory resolves outside its expected physical path."
REPORT="$FEATURE_ID.md"
OUT="$OUT_DIR/$REPORT"
check_output() {
    [ "$(pwd -P)" = "$OUT_DIR" ] || die "Report directory moved during generation."
    [ ! -L "./$REPORT" ] || die "Report destination must not be a symlink."
    [ ! -e "./$REPORT" ] || [ -f "./$REPORT" ] || die "Report destination must be a regular file."
}
check_output
SPOOL="$REPO_ROOT/.prdkit/ledger/events.jsonl"
ENRICH="$REPO_ROOT/.prdkit/retrospective/$FEATURE_ID.enrich.json"
TODAY="$(date +%Y-%m-%d)"

# ===========================================================================
# Aggregate the ledger. One awk pass filters to this feature, dedupes on
# event_id (last wins), and emits metric lines ("M\tkey\tvalue") plus one
# review row per review.recorded ("R\tround\tscope\tagent\tverdict\tC\tM\tm").
# ===========================================================================
AGG=""
if [ -f "$SPOOL" ]; then
    AGG="$(awk -v fid="$FEATURE_ID" '
    function jstr(key, str,   s) {
        if (match(str, "\"" key "\"[[:space:]]*:[[:space:]]*\"")) {
            s = substr(str, RSTART + RLENGTH)
            sub(/".*/, "", s)
            return s
        }
        return ""
    }
    function jnum(key, str,   s) {
        if (match(str, "\"" key "\"[[:space:]]*:[[:space:]]*-?[0-9]+")) {
            s = substr(str, RSTART, RLENGTH)
            sub(/.*:[[:space:]]*/, "", s)
            return s
        }
        return ""
    }
    function feature_id(line,   obj) {
        if (match(line, /"feature"[[:space:]]*:[[:space:]]*\{[^}]*\}/)) {
            obj = substr(line, RSTART, RLENGTH)
            return jstr("id", obj)
        }
        return ""
    }
    function process(line,   ev, v, c, mj, mn, rnd, scope, ag, vv, p, conv, rest, obj, tsk, tov) {
        kept++
        ev = jstr("event", line)
        if (ev == "feature.created") {
            created_ts = jstr("ts", line)
            title = jstr("title", line)
        } else if (ev == "feature.merged") {
            merged_ts = jstr("ts", line)
            duration_days = jnum("duration_days", line)
            tasks_total = jnum("tasks_total", line)
        } else if (ev == "task.state_changed") {
            rest = line
            while (match(rest, /\{[^{}]*\}/)) {
                obj = substr(rest, RSTART, RLENGTH)
                rest = substr(rest, RSTART + RLENGTH)
                tsk = jstr("task", obj)
                tov = jstr("to", obj)
                if (tsk != "" && (tov == "[x]" || tov == "x")) {
                    if (!(tsk in completed)) { completed[tsk] = 1; ncompleted++ }
                }
            }
        } else if (ev == "review.recorded") {
            if (jnum("major", line) != "" || jnum("minor", line) != "") {
                print "U\tpre-redesign finding severity names"; return
            }
            rounds++
            v = jstr("verdict", line)
            if (v == "PASS") pass++
            else if (v == "FAIL") fail++
            else if (v == "WAIVED") waived++
            c  = jnum("critical", line); c  = (c  == "" ? 0 : c)
            h = jnum("high", line); h = (h == "" ? 0 : h)
            m = jnum("medium", line); m = (m == "" ? 0 : m)
            l = jnum("low", line); l = (l == "" ? 0 : l)
            tc += c; th += h; tm += m; tl += l
            rnd   = jnum("round", line); rnd   = (rnd   == "" ? "-" : rnd)
            scope = jstr("scope", line); scope = (scope == "" ? "-" : scope)
            ag    = jstr("agent", line); ag    = (ag    == "" ? "-" : ag)
            vv = (v == "" ? "-" : v)
            print "R\t" rnd "\t" scope "\t" ag "\t" vv "\t" c "\t" h "\t" m "\t" l
            if (!(ag in agseen)) { agseen[ag] = 1; agorder[++nag] = ag }
            agrounds[ag]++
            if (v == "PASS") agpass[ag]++
            else if (v == "FAIL") agfail[ag]++
            else if (v == "WAIVED") agwaived[ag]++
            agc[ag] += c; agh[ag] += h; agm[ag] += m; agl[ag] += l
        } else if (ev == "exploration.converged") {
            explor++
            p = jnum("passes", line); if (p != "") exppasses += p
            conv = jstr("convergence", line)
            if (conv == "HIGH") ch++
            else if (conv == "MEDIUM") cm++
            else if (conv == "LOW") cl++
        } else if (ev == "blocker.opened") {
            bopen++
        } else if (ev == "blocker.resolved") {
            bresolved++
        }
    }
    { raw[NR] = $0 }
    END {
        for (i = 1; i <= NR; i++) {
            if (feature_id(raw[i]) != fid) continue
            keepf[i] = 1
            id = jstr("event_id", raw[i])
            eidof[i] = id
            if (id != "") lastpos[id] = i
        }
        for (i = 1; i <= NR; i++) {
            if (!keepf[i]) continue
            if (eidof[i] != "" && lastpos[eidof[i]] != i) continue
            process(raw[i])
        }
        print "M\tevents_total\t"       (kept + 0)
        print "M\tcreated_ts\t"         created_ts
        print "M\tmerged_ts\t"          merged_ts
        print "M\tduration_days\t"      duration_days
        print "M\ttasks_total\t"        tasks_total
        print "M\ttasks_completed\t"    (ncompleted + 0)
        print "M\treview_rounds\t"      (rounds + 0)
        print "M\tpass\t"               (pass + 0)
        print "M\tfail\t"               (fail + 0)
        print "M\twaived\t"             (waived + 0)
        print "M\tfindings_c\t"         (tc + 0)
        print "M\tfindings_high\t"      (th + 0)
        print "M\tfindings_medium\t"    (tm + 0)
        print "M\tfindings_low\t"       (tl + 0)
        print "M\texploration_events\t" (explor + 0)
        print "M\texploration_passes\t" (exppasses + 0)
        print "M\tconv_high\t"          (ch + 0)
        print "M\tconv_medium\t"        (cm + 0)
        print "M\tconv_low\t"           (cl + 0)
        print "M\tblockers_opened\t"    (bopen + 0)
        print "M\tblockers_resolved\t"  (bresolved + 0)
        print "M\ttitle\t"              title
        for (i = 1; i <= nag; i++) {
            a = agorder[i]
            print "A\t" a "\t" (agrounds[a] + 0) "\t" (agpass[a] + 0) "\t" (agfail[a] + 0) "\t" (agwaived[a] + 0) "\t" (agc[a] + 0) "\t" (agh[a] + 0) "\t" (agm[a] + 0) "\t" (agl[a] + 0)
        }
    }
    ' "$SPOOL")"
fi

# --- Parse the aggregate into shell variables (bash 3.2-safe; no assoc arrays).
events_total=0; review_rounds=0; pass=0; fail=0; waived=0
findings_c=0; findings_high=0; findings_medium=0; findings_low=0
exploration_events=0; exploration_passes=0; conv_high=0; conv_medium=0; conv_low=0
blockers_opened=0; blockers_resolved=0; tasks_completed=0
created_ts=""; merged_ts=""; duration_days=""; tasks_total=""; title=""
review_rows=""
agent_rows=""
if [ -n "$AGG" ]; then
    while IFS="$TAB" read -r tag f1 f2 f3 f4 f5 f6 f7 f8 f9; do
        if [ "$tag" = "U" ]; then
            die "Unsupported private-era ledger findings; translate the export to critical/high/medium/low before reporting."
        elif [ "$tag" = "M" ]; then
            case "$f1" in
                events_total)       events_total="$f2" ;;
                created_ts)         created_ts="$f2" ;;
                merged_ts)          merged_ts="$f2" ;;
                duration_days)      duration_days="$f2" ;;
                tasks_total)        tasks_total="$f2" ;;
                tasks_completed)    tasks_completed="$f2" ;;
                review_rounds)      review_rounds="$f2" ;;
                pass)               pass="$f2" ;;
                fail)               fail="$f2" ;;
                waived)             waived="$f2" ;;
                findings_c)         findings_c="$f2" ;;
                findings_high)      findings_high="$f2" ;;
                findings_medium)    findings_medium="$f2" ;;
                findings_low)       findings_low="$f2" ;;
                exploration_events) exploration_events="$f2" ;;
                exploration_passes) exploration_passes="$f2" ;;
                conv_high)          conv_high="$f2" ;;
                conv_medium)        conv_medium="$f2" ;;
                conv_low)           conv_low="$f2" ;;
                blockers_opened)    blockers_opened="$f2" ;;
                blockers_resolved)  blockers_resolved="$f2" ;;
                title)              title="$f2" ;;
            esac
        elif [ "$tag" = "R" ]; then
            review_rows="${review_rows}| $f1 | $f2 | $f3 | $f4 | $f5 / $f6 / $f7 / $f8 |"$'\n'
        elif [ "$tag" = "A" ]; then
            agent_rows="${agent_rows}| $f1 | $f2 | $f3 | $f4 | $f5 | $f6 / $f7 / $f8 / $f9 |"$'\n'
        fi
    done <<EOF
$AGG
EOF
fi

if [ -e "$ENRICH" ] || [ -L "$ENRICH" ]; then
    [ -f "$ENRICH" ] || die "Enrichment must be a readable JSON file."
    load_enrichment
    compute_enrich_signals
fi

# ===========================================================================
# Honest heuristics — every recommendation is gated on its triggering data.
# ===========================================================================
emit_recommendations() {
    local emitted=0
    if [ "$fail" -gt 0 ] 2>/dev/null && [ "$fail" -ge "$pass" ] 2>/dev/null; then
        echo "- Reviews failed at least as often as they passed ($fail FAIL vs $pass PASS). Review the"
        echo "  spec and project setup before the next change — ambiguous specs drive review rework."
        emitted=1
    fi
    if [ -n "$dominant_model_share" ] && [ "$dominant_model_share" -ge 80 ] 2>/dev/null; then
        echo "- One model ($dominant_model) accounts for ${dominant_model_share}% of token spend. Consider a"
        echo "  cheaper model or a lower effort setting for routine tasks on the next change."
        emitted=1
    fi
    if [ -n "$reasoning_share" ] && [ "$reasoning_share" -ge 40 ] 2>/dev/null; then
        echo "- Reasoning tokens are ${reasoning_share}% of the total — effort may be set higher than routine"
        echo "  tasks need. Try a lower effort setting next time and compare outcomes."
        emitted=1
    fi
    if [ "$conv_low" -gt 0 ] 2>/dev/null; then
        echo "- $conv_low exploration(s) converged LOW — the spec was ambiguous. Tighten acceptance"
        echo "  criteria before exploring design on the next change."
        emitted=1
    fi
    if [ "$exploration_events" -gt 0 ] 2>/dev/null; then
        echo "- Compare recorded exploration effort with its agreed investment and useful decisions."
        echo "  Concept count or similarity alone does not establish quality or ambiguity."
        emitted=1
    fi
    if [ "$blockers_opened" -gt "$blockers_resolved" ] 2>/dev/null; then
        echo "- More blockers were opened ($blockers_opened) than resolved ($blockers_resolved) — carry-over"
        echo "  risk into the next change. Close or explicitly defer the open ones."
        emitted=1
    fi
    if [ "$enrich_present" -eq 0 ]; then
        echo "- No token enrichment was available, so no cost/model guidance could be produced."
        echo "  Supply the optional enrichment contract documented in docs/task-ledger-integration.md."
        emitted=1
    fi
    if [ "$emitted" -eq 0 ]; then
        echo "- No issues detected in the recorded data; the project setup looks healthy for this change."
    fi
}

# ===========================================================================
# Compose the report.
# ===========================================================================
heading="# Retrospective — $FEATURE_ID"
if [ -n "$title" ]; then
    heading="$heading ($title)"
fi

# Exclusive private staging directory; never write through the legacy .md.tmp.
TMP_OUTPUT="$(mktemp -d ./.retrospective.XXXXXX)"
trap 'rm -rf -- "$TMP_OUTPUT"' EXIT
trap 'exit 1' HUP INT TERM
{
    echo "$heading"
    echo "_Generated $TODAY by the delivery retrospective helper from the local prdkit ledger._"
    echo
    echo "> **Purpose.** Review this change's token, model, effort, and agent usage; confirm the"
    echo "> project is set up correctly; and recommend cost, speed, and model-selection improvements"
    echo "> for the next changes. Everything below is derived only from recorded data — no token or"
    echo "> cost number is invented."
    echo
    echo "## Summary"
    echo
    if [ "$events_total" -eq 0 ] 2>/dev/null; then
        if [ -f "$SPOOL" ]; then
            echo "- No ledger events were found for \`$FEATURE_ID\` in \`.prdkit/ledger/events.jsonl\`."
        else
            echo "- No ledger spool found at \`.prdkit/ledger/events.jsonl\` — nothing has been emitted yet."
        fi
        echo "- The rest of this retrospective reflects an empty ledger; re-run after the change records events."
    else
        echo "- Created: ${created_ts:-—}"
        if [ -n "$merged_ts" ]; then
            echo "- Merged: $merged_ts"
        else
            echo "- Merged: — (not merged)"
        fi
        if [ -n "$merged_ts" ] && [ -n "$duration_days" ]; then
            echo "- Duration: $duration_days days"
        fi
        if [ -n "$tasks_total" ]; then
            echo "- Tasks completed: $tasks_completed (tasks_total at merge: $tasks_total)"
        else
            echo "- Tasks completed: $tasks_completed"
        fi
        echo "- Review rounds: $review_rounds total — PASS $pass · FAIL $fail · WAIVED $waived"
        echo "- Findings: $findings_c critical · $findings_high high · $findings_medium medium · $findings_low low"
        if [ "$exploration_events" -gt 0 ] 2>/dev/null; then
            echo "- Exploration: $exploration_events converged pass-set(s), $exploration_passes passes total — HIGH $conv_high · MEDIUM $conv_medium · LOW $conv_low"
        fi
        if [ "$blockers_opened" -gt 0 ] 2>/dev/null || [ "$blockers_resolved" -gt 0 ] 2>/dev/null; then
            echo "- Blockers: $blockers_opened opened · $blockers_resolved resolved"
        fi
    fi
    echo
    echo "## Review detail"
    echo
    if [ -n "$review_rows" ]; then
        echo "| Round | Scope | Agent | Verdict | Findings C/H/M/L |"
        echo "|-------|-------|-------|---------|----------------|"
        printf '%s' "$review_rows"
    else
        echo "_No \`review.recorded\` events for this change._"
    fi
    echo
    echo "## Agent effectiveness"
    echo
    if [ -n "$agent_rows" ]; then
        echo "Per-agent review outcomes for this change support optional capability evaluation."
        echo "Finding counts alone do not establish reviewer effectiveness or product usefulness."
        echo
        echo "| Agent | Rounds | PASS | FAIL | WAIVED | Findings C/H/M/L |"
        echo "|-------|--------|------|------|--------|----------------|"
        printf '%s' "$agent_rows"
    else
        echo "_No \`review.recorded\` events naming an agent for this change._"
    fi
    echo
    echo "## Token & model usage"
    echo
    if [ "$enrich_present" -eq 1 ]; then
        render_enrichment
    else
        echo "_No token/model enrichment found._ An optional external adapter can write"
        echo "\`.prdkit/retrospective/$FEATURE_ID.enrich.json\` using the documented enrichment contract."
        echo "Then re-run \`/prdkit-learn\` or the retrospective helper. Token and cost numbers are never inferred from"
        echo "the ledger, so this section stays empty by design until the enrichment file exists."
    fi
    echo
    echo "## Recommendations"
    echo
    emit_recommendations
} > "$TMP_OUTPUT/report"

check_output
# GNU -T / BSD -h prevent a last-moment destination symlink-to-directory from
# turning a rename into a write inside that directory (Linux / macOS).
if [ "$(uname -s)" = Darwin ]; then
    mv -fh "$TMP_OUTPUT/report" "./$REPORT"
else
    mv -fT "$TMP_OUTPUT/report" "./$REPORT"
fi
echo "[prdkit] Wrote retrospective: $OUT"
