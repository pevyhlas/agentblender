#!/bin/bash
# Trusted all-PR transport for the ONE native review gate. See docs/ci-gates.md.
# SECURITY: the registered immutable workflow must sanitize BEFORE starting this shell.
# This script cannot authenticate its own workflow registration or repair startup injection.
#
# Invariants:
# Bind the event and fetched objects to the live repository, PR head, target and the
# workflow-pinned policy ref's tip. Run the native gate and helpers from that tip alone.
# Accept only a consistent host pass for the exact checked head and unchanged final
# bindings. Invalid output, timeout, cancellation or cleanup failure cannot pass.
#
# Threat model:
# Include untrusted candidate PR contents (forks included), changes to the live default
# branch or other host refs, and interrupted work. The default setting can veto but never
# select policy. Bound execution and clean up owned processes and scratch.
# Trust the registered launcher, pre-shell setup and runner. A hostile writer on the
# runner or a caller controlling shell startup is outside this script's boundary.
# shellcheck disable=SC2329,SC2016 # trap/callbacks; positional arguments expanded in the bounded Bash.
set -euo pipefail

# Fixed fresh-host tool locations, never the workspace/caller's PATH. Test transports instrument
# a disposable copy, not a production flag, environment switch, or alternate source argument.
PATH=/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin:/opt/homebrew/bin
export PATH LC_ALL=C LANG=C
umask 077
phase=bootstrap; error_code=interrupted; root=""; operation=""
work=""; reader_out=""; reader_err=""
guarded_pgids=()
finishing=false; pending_signal=""; groups_verified=true
native=null; native_exit=null; transport=refused; status=1; policy_error=""
capture_error=""
deadline=$((SECONDS+600))

# All output is buffered until owned processes are gone and private scratch is deleted.
check_signal() {
    if ! $finishing; then
        [ -z "$pending_signal" ] || refuse "$phase" "$pending_signal"
        [ "$SECONDS" -lt "$deadline" ] || refuse "$phase" overall-timeout
    fi
    return 0
}
request_signal() {
    [ -n "$pending_signal" ] || pending_signal="$1"
    # Never exit from a signal trap, including the entry to an EXIT trap before finish has
    # set its flags. Explicit safe checkpoints handle cancellation after ownership is known.
    return 0
}
groups_empty() {
    local pgid diagnostic
    # A ps inventory can miss a fork/exit handoff. Probe the kernel directly;
    # signal 0 cannot harm a reused group, but it must prevent deletion.
    for pgid in ${guarded_pgids[@]+"${guarded_pgids[@]}"}; do
        if diagnostic="$(builtin kill -0 -- "-$pgid" 2>&1)"; then
            return 1
        else
            # Bash reports ESRCH and EPERM with the same exit status. Only
            # the C-locale ESRCH diagnostic proves the group no longer exists.
            [ "$?" -eq 1 ] && [[ "$diagnostic" == *"kill: (-$pgid) - No such process" ]] || return 1
        fi
    done
}
remove_scratch() {
    groups_empty || return 1
    if [ -n "$root" ] && { ! rm -rf -- "$root" || [ -e "$root" ]; }; then return 1; fi
}
finish() {
    finishing=true
    trap - EXIT ERR
    # Queue, rather than default or exit on, repeated catchable signals during teardown.
    trap 'request_signal interrupted' INT TERM HUP
    trap 'request_signal overall-timeout' USR1
    [ -z "$operation" ] || collect_operation 2>>"$root/guard.err" || groups_verified=false
    # Cancellation can arrive after the native gate produced complete JSON.
    # Keep that diagnostic after verified cleanup without inventing a native exit.
    if [ -n "$capture_error" ]; then
        native=null; native_exit=null; transport=refused; status=1
    elif $groups_verified && [ "$native" = null ] && [ -n "$root" ] && [ -f "$root/native.json" ]; then
        if one_object "$root/native.json"; then native="$(cat "$root/native.json")"; fi
    fi
    if ! $groups_verified || ! remove_scratch; then
        phase=cleanup; error_code=cleanup-failed; transport=refused; status=1
    fi
    # Teardown is now complete. Close the catchable-signal window before the final decision
    # and buffered emission; signals queued during teardown must still prevent success.
    trap '' INT TERM HUP USR1
    if [ "$transport" = complete ]; then
        if [ "$SECONDS" -ge "$deadline" ] && [ -z "$pending_signal" ]; then pending_signal=overall-timeout; fi
        if [ -n "$pending_signal" ]; then
            phase=cleanup; error_code="$pending_signal"; transport=refused; status=1
        fi
    fi
    if [ "$transport" = complete ]; then
        printf '{"launcher":"complete","error":null,"native_exit":%s,"native":%s}\n' "$native_exit" "$native"
    else
        printf 'check-pr-review: %s/%s\n' "$phase" "$error_code" >&2
        if [ "$error_code" = policy-ref-required ]; then
            printf '%s\n' 'Product-Kit 3.1 requires --policy-ref <protected branch> in the registered required workflow.' >&2
        fi
        if [ "$error_code" = invalid-policy ] && [ -n "$policy_error" ]; then printf '%s\n' "$policy_error" >&2; fi
        printf '{"launcher":"refused","error":{"stage":"%s","code":"%s"},"native_exit":%s,"native":%s}\n' \
            "$phase" "$error_code" "$native_exit" "$native"
    fi
    exit "$status"
}
refuse() { phase="$1"; error_code="$2"; status="${3:-1}"; exit "$status"; }
trap finish EXIT
trap 'request_signal interrupted' INT TERM HUP
trap 'request_signal overall-timeout' USR1
trap 'refuse "$phase" command-failed' ERR

repository=""; event=""; policy_ref=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --repository) [ "$#" -ge 2 ] && [ -z "$repository" ] || refuse bootstrap usage 2; repository="$2"; shift ;;
        --event) [ "$#" -ge 2 ] && [ -z "$event" ] || refuse bootstrap usage 2; event="$2"; shift ;;
        --policy-ref)
            [ "$#" -ge 2 ] && [ -n "$2" ] || refuse bootstrap policy-ref-required 2
            case "$2" in -*) refuse bootstrap policy-ref-required 2 ;; esac # omitted value before a flag
            [ -z "$policy_ref" ] || refuse bootstrap invalid-policy-ref 2
            policy_ref="$2"; shift ;;
        *) refuse bootstrap usage 2 ;;
    esac
    shift
done
[ -n "$policy_ref" ] || refuse bootstrap policy-ref-required 2
[[ "$repository" =~ ^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$ ]] || refuse bootstrap usage 2
case "${repository#*/}" in .|..|*.git) refuse bootstrap usage 2 ;; esac
[ -n "$event" ] && [ -f "$event" ] && [ ! -L "$event" ] || refuse bootstrap usage 2
case "$event" in /*) ;; *) event="$PWD/$event" ;; esac
case "${GITHUB_EVENT_NAME:-}" in pull_request|pull_request_review) ;; *) refuse bootstrap context ;; esac
[ "${GITHUB_REPOSITORY:-}" = "$repository" ] &&
    [[ "${GITHUB_REPOSITORY_ID:-}" =~ ^[1-9][0-9]*$ ]] || refuse bootstrap context
[ -n "${GH_TOKEN:-}" ] || refuse bootstrap credential

# Defense in depth AFTER the mandatory pre-shell constructor. Do not inherit Git configuration,
# caller credential helpers, proxies or language/loader options into any child.
for variable in $(compgen -v); do
    case "$variable" in
        GIT_*|GH_*|GITHUB_TOKEN|BASH_ENV|ENV|CDPATH|LD_*|DYLD_*|PYTHON*|PERL*|RUBY*|NODE_*|\
        http_proxy|https_proxy|all_proxy|no_proxy|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|NO_PROXY)
            [ "$variable" = GH_TOKEN ] || unset "$variable" ;;
    esac
done
root="$(mktemp -d /tmp/prdkit-pr.XXXXXXXX)" || refuse bootstrap scratch
check_signal
HOME="$root/home"; GH_CONFIG_DIR="$root/gh"; XDG_CONFIG_HOME="$root/xdg"; TMPDIR="$root/tmp"
export HOME GH_CONFIG_DIR XDG_CONFIG_HOME TMPDIR
mkdir -p "$HOME" "$GH_CONFIG_DIR" "$XDG_CONFIG_HOME" "$TMPDIR" "$root/template" "$root/trusted"
cd "$root"
export GH_HOST=github.com GH_PROMPT_DISABLED=1 GH_PAGER=cat PAGER=cat
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_GLOBAL=/dev/null
export GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1 GIT_TERMINAL_PROMPT=0
export GIT_ATTR_NOSYSTEM=1 GIT_OPTIONAL_LOCKS=0
for tool in git gh jq; do command -v "$tool" >/dev/null || refuse bootstrap tools; done
# Parser diagnostics may quote input. Keep them private too; public stderr is phase/code only.
jq() { command jq "$@" 2>>"$root/json.err"; }

# Embedded in W's validated launcher, never loaded from a checkout.
cat > "$root/group-guard" <<'GUARD'
set -eu
set +m
op=$1 role=$2; shift 2
step="pgid-lookup"
trap 'printf "role=%s step=%s status=%s op=%s\n" "$role" "$step" "$?" "${op##*/}" >&2' EXIT
pgid="$(exec 6<&- 9>&-; ps -o pgid= -p "$$")"
step="pgid-check"
[[ ${pgid//[[:space:]]/} == "$$" ]] || exit 1
# The inherited duplex FD prevents either side blocking during open. Close it
# after obtaining a read-only FD: only the launcher keeps the write side.
step="control-open"
exec 7<"$op/$role.control"
exec 6<&-

record() {
    printf '%s\n' "$1" > "$op/$role.tmp" && mv "$op/$role.tmp" "$op/$role.status"
}
step="worker-launch"
if [ "$role" = work ]; then
    (
        exec 7<&-
        # Keep the status relay through TERM, without passing SIG_IGN to the
        # actual command. KILL may prevent a status from being recorded.
        trap ':' TERM
        # Bash 3.2 can save an inline-closed FD on a new number across exec.
        # Permanently close the failure writer in each child that must not own it.
        rc=0; (exec 9>&-; exec "$@") || rc=$?
        # Report a failed status write even if scratch cannot hold another
        # file. Only this relay writes to the private failure FIFO.
        if ! (exec 9>&-; record "$rc"); then
            printf 'failed\n' >&9
            # The marker wakes the normal fast poll; the FIFO remains the
            # failure record if scratch cannot accept the marker.
            : > "$op/work.failed"
        fi
    ) >"$op/out.pipe" 2>"$op/err.pipe" &
    exec 9>&-
else
    (
        exec 7<&-
        rc=0
        head -c 4194304 < "$op/$role.pipe" > "$1" || rc=$?
        if [ "$rc" -eq 0 ]; then
            size="$(wc -c < "$1")" || rc=$?
            if [ "$rc" -eq 0 ] && [ "$size" -ge 4194304 ]; then : > "$op/overflow"; fi
        fi
        record "$rc"
    ) &
fi
trap '' TERM HUP INT USR1
step="ready-write"
: > "$op/$role.ready"
trap - EXIT

# This is the only group signaler. This fresh Bash is the actual, live group
# leader; $$ is not a remembered child PID (nor a Bash subshell's inherited $$).
# A late FIFO message cannot make a dead guard signal a recycled group.
sent_term=false
while IFS= read -r request <&7; do
    case "$request" in
        release) exit 0 ;;
        term)
            if ! $sent_term; then
                [ ! -e "$op/$role.sealed" ] || exit 1
                sent_term=true
                kill -TERM -- "-$$" || exit 1
                # Relative kernel timer, independent of changes to wall clock.
                if IFS= read -r -t 5 request <&7; then
                    [ "$request" = release ] && exit 0
                    exit 1
                fi
                # Bash 3.2 does not distinguish timeout from EOF in $?. On EOF
                # the next read exits without any signal, even with grace set.
                : > "$op/$role.grace"
            fi ;;
        kill)
            [ -f "$op/$role.grace" ] && [ ! -e "$op/$role.sealed" ] || exit 1
            kill -KILL -- "-$$"
            exit 1 ;; # no retry, even if the syscall failed
        *) exit 1 ;;
    esac
done
GUARD

# A leader row must disappear (Bash reaps its own children asynchronously).
# Plain, unambiguously dead non-child zombies do not hold files. In particular
# Linux Zl can contain a live thread; unusual or exiting states never pass.
group_view() {
    ps -axo pid=,pgid=,stat= | awk -v group="$1" '
        NF != 3 || $1 !~ /^[0-9]+$/ || $2 !~ /^[0-9]+$/ { bad=1 }
        $1 == group {
            if (leader || $2 != group) bad=1
            leader=1
            if ($3 ~ /^[IRS][+<NsLl]*$/) guard=1
        }
        $2 == group && $1 != group && $3 !~ /^Z[+<NsL]*$/ { other=1 }
        END {
            if (!NR || bad) exit 1
            if (!leader && !other) print "gone"
            else if (guard) print other ? "busy" : "alone"
            else print "unconfirmed"
        }
    '
}
probe_group() { # pid timeout (whole seconds, supported by Bash 3.2)
    # One private inventory channel per operation. If ps or awk stalls, stop
    # using this channel; a delayed answer must not be read as a later snapshot.
    $probe_ok || return 1
    (
        exec 3<&- 4<&- 5<&- 7<&- 9>&-
        exec >&8 8>&-
        if state="$(group_view "$1")"; then
            printf '%s\n' "$state"
        else
            printf 'unconfirmed\n'
        fi
    ) &
    if IFS= read -r -t "$2" state <&8; then return 0; fi
    probe_ok=false
    return 1
}
guard_request() {
    # Linux and Darwin let a FIFO opened read/write hold both endpoints. Only
    # two tiny messages per group are possible, so writing cannot fill the pipe
    # or block waiting for a reader, even if the guard is already gone.
    printf '%s\n' "$1" >&"$2"
}
stop_guard() { # role pid control-fd
    local role="$1" pid="$2" fd="$3" state="" sent="" i end ready remaining
    [ -n "$pid" ] || return 0
    end=$((SECONDS+8))
    for ((i=0; i<80 && SECONDS<end; i++)); do
        # Observe registration before the snapshot, never combine a pre-launch
        # "alone" snapshot with a readiness file that appeared during the scan.
        ready=false
        if [ -f "$operation/$role.ready" ]; then ready=true; fi
        remaining=$((end-SECONDS))
        [ "$remaining" -gt 0 ] || break
        if [ "$remaining" -gt 3 ]; then remaining=3; fi
        probe_group "$pid" "$remaining" || break
        [ "$SECONDS" -lt "$end" ] || break
        case "$state:$sent" in
            gone:*) return 0 ;;
            alone:|alone:term)
                if $ready; then
                    guard_request release "$fd" || break
                    break
                fi ;;
            busy:)
                if $ready; then
                    guard_request term "$fd" || break
                    sent=term
                fi ;;
            busy:term)
                if [ -f "$operation/$role.grace" ]; then
                    guard_request kill "$fd" || break
                    break
                fi ;;
        esac
        sleep 0.1
    done
    # After release or KILL, there are no more signal requests; never call a
    # blocking wait. Require the guard and every live member to be absent from
    # fresh inventories, otherwise refuse and retain the scratch that they may use.
    end=$((SECONDS+5))
    for ((i=0; i<50 && SECONDS<end; i++)); do
        remaining=$((end-SECONDS))
        [ "$remaining" -gt 0 ] || break
        if [ "$remaining" -gt 3 ]; then remaining=3; fi
        probe_group "$pid" "$remaining" || break
        if [ "$state" = gone ]; then return 0; fi
        sleep 0.1
    done
    : > "$operation/$role.sealed"
    return 1
}

guard_status() {
    local rc
    read -r rc < "$operation/$1.status" &&
        [[ "$rc" =~ ^[0-9]{1,3}$ ]] && [ "$rc" -le 255 ] || return 1
    printf '%s\n' "$rc"
}
guard_exited_before_ready() { # role pid
    local role="$1" pid="$2" diagnostic rc
    [ ! -f "$operation/$role.ready" ] || return 1
    if builtin kill -0 -- "$pid" 2>/dev/null; then return 1; fi
    # Probe the child PID, not its group. EPERM is not proof of exit, and a
    # readiness file may have appeared between the first check and the probe.
    if diagnostic="$(builtin kill -0 -- "$pid" 2>&1)"; then return 1; else rc=$?; fi
    [ "$rc" -eq 1 ] && [[ "$diagnostic" == *"kill: ($pid) - No such process" ]] &&
        [ ! -f "$operation/$role.ready" ] || return 1
    # Do not wait on a PID. The guard's EXIT trap records the actual status
    # when possible; this line also covers exits before that trap was installed.
    printf 'role=%s step=pre-ready-exit op=%s\n' "$role" "${operation##*/}" >>"$root/guard.err" || :
    return 0
}
collect_operation() {
    local i pgid role shutdown_ok=true out_status err_status probe_ok=true notice
    for pgid in "$work" "$reader_out" "$reader_err"; do
        if [ -n "$pgid" ]; then guarded_pgids+=("$pgid"); fi
    done
    stop_guard work "$work" 3 || shutdown_ok=false
    # A signal or deadline may have stopped the main loop before it saw the
    # notice. Check again before classifying the result. EOF alone does not
    # mean the relay attempted a write; supervised KILL can interrupt it.
    # The sole writer closes on relay exit; the read normally returns data
    # or EOF immediately. Bound it if work shutdown is unverified.
    if [ -f "$operation/work.failed" ] ||
        { [ -n "$work" ] && IFS= read -r -t 1 notice <&7 && [ "$notice" = failed ]; }; then
        capture_error="${capture_error:-capture-failed}"
    fi
    if [ -f "$operation/work.status" ]; then
        bounded_exit="$(guard_status work)" || capture_error="${capture_error:-capture-failed}"
        if [ "$phase" = native ] && [ "$native_exit" = null ]; then native_exit="$bounded_exit"; fi
    fi
    # Writers are gone (or unverified). Let readers drain ordinary EOF, then
    # retire their own groups even when workload shutdown could not be proved.
    if $shutdown_ok && [ -z "$capture_error" ]; then
        for ((i=0; i<50; i++)); do
            if [ -f "$operation/out.status" ] && [ -f "$operation/err.status" ]; then break; fi
            sleep 0.1
        done
    fi
    stop_guard out "$reader_out" 4 || shutdown_ok=false
    stop_guard err "$reader_err" 5 || shutdown_ok=false
    for role in work out err; do
        if [ ! -f "$operation/$role.ready" ]; then capture_error="${capture_error:-guard-startup}"; fi
    done
    out_status="$(guard_status out 2>/dev/null)" || out_status=missing
    err_status="$(guard_status err 2>/dev/null)" || err_status=missing
    if [ "$out_status:$err_status" != 0:0 ]; then capture_error="${capture_error:-capture-failed}"; fi
    if [ -e "$operation/overflow" ]; then bounded_limit=overflow; fi
    exec 3>&- 4>&- 5>&- 7<&- 8>&- 9>&-
    operation=""; work=""; reader_out=""; reader_err=""
    $shutdown_ok
}

# No inherited file-size limit constrains Git packs or native working files.
capture_file() { # destination command... (selector data, not Git object storage)
    local destination="$1" rc; shift
    if "$@" | head -c 4194304 > "$destination"; then rc=0; else rc=$?; fi
    [ "$(wc -c < "$destination")" -lt 4194304 ] || return 1
    return "$rc"
}
start_guard() { # launched asynchronously; exec preserves the job's leader PID
    local control="$1"; shift
    # Only the selected control FD and (for work) the failure writer cross exec.
    exec 6<&"$control"
    exec 3<&- 4<&- 5<&- 7<&- 8<&-
    if [ "$1" != work ]; then exec 9>&-; fi
    exec 2>>"$root/guard.err" </dev/null >/dev/null
    exec /bin/bash --noprofile "$root/group-guard" "$operation" "$@"
}
bounded() { # seconds output error command...
    local seconds="$1" out="$2" err="$3" end guards_ready=false
    shift 3
    check_signal
    bounded_exit=null; bounded_limit=""
    operation="$(mktemp -d "$root/op.XXXXXXXX")" || refuse cleanup cleanup-failed
    mkfifo "$operation/work.control" "$operation/out.control" "$operation/err.control" \
        "$operation/out.pipe" "$operation/err.pipe" "$operation/inventory" "$operation/work.failure" ||
        refuse cleanup cleanup-failed
    exec 3<>"$operation/work.control" 4<>"$operation/out.control" 5<>"$operation/err.control" \
        8<>"$operation/inventory" 9<>"$operation/work.failure"
    exec 7<"$operation/work.failure" # the duplex opener above holds the write end during setup
    # Traps only queue cancellation. They cannot retire partially registered groups.
    set -m # give each async guard its own PGID (also on Bash 3.2)
    start_guard 4 out "$out" &
    reader_out=$!
    start_guard 5 err "$err" &
    reader_err=$!
    start_guard 3 work "$@" &
    work=$!
    exec 9>&-
    set +m
    end=$((SECONDS+seconds))
    while [ ! -f "$operation/work.status" ] || ! $guards_ready; do
        if [ -f "$operation/work.failed" ]; then break; fi
        if [ -e "$operation/overflow" ]; then bounded_limit=overflow; break; fi
        if [ -n "$pending_signal" ] || [ "$SECONDS" -ge "$deadline" ]; then break; fi
        if ! $guards_ready; then
            if [ -f "$operation/work.ready" ] && [ -f "$operation/out.ready" ] && [ -f "$operation/err.ready" ]; then
                guards_ready=true
                [ ! -f "$operation/work.status" ] || break
            elif guard_exited_before_ready work "$work" ||
                guard_exited_before_ready out "$reader_out" ||
                guard_exited_before_ready err "$reader_err"; then
                capture_error=guard-startup
                break
            fi
        fi
        if [ "$SECONDS" -ge "$end" ]; then bounded_limit=timeout; break; fi
        sleep 0.05
    done
    collect_operation 2>>"$root/guard.err" || { groups_verified=false; refuse cleanup cleanup-failed; }
    [ -z "$capture_error" ] || refuse "$phase" "$capture_error"
    check_signal
    case "$bounded_limit" in overflow) return 125 ;; timeout) return 124 ;; esac
    if [ "$bounded_exit" = null ]; then
        capture_error=capture-failed
        refuse "$phase" "$capture_error"
    fi
    return "$bounded_exit"
}
one_object() {
    local size kept
    [ -f "$1" ] || return 1
    size="$(wc -c < "$1")"
    kept="$(tr -d '\000' < "$1" | wc -c)"
    [ "$size" = "$kept" ] || return 1
    jq -e -s 'length == 1 and (.[0] | type == "object")' "$1" >/dev/null
}
api() { # endpoint output
    bounded 120 "$2" "$root/api.err" gh api --hostname github.com "$1" || refuse binding host-unavailable
    one_object "$2" || refuse binding host-json
}
sha() { [[ "$1" =~ ^[0-9a-f]{40}$ ]]; } # GitHub's native SHA-1 commit format
branch() {
    [[ "$1" != *[[:cntrl:]]* ]] && [ "$1" != HEAD ] &&
        git check-ref-format "refs/heads/$1" >/dev/null 2>&1
}
branch "$policy_ref" || refuse bootstrap invalid-policy-ref 2
policy_source="refs/heads/$policy_ref"
policy_tracking="refs/remotes/origin/$policy_ref"
policy_encoded="$(printf '%s' "$policy_ref" | jq -sRr @uri)"

# REST resources are singular, not lists: there is no pagination to silently truncate here.
# The native adapter owns complete review pagination.
read_bindings() {
    local prefix="$1" target_encoded
    api "repos/$repository" "$root/$prefix.repo"
    jq -e --arg repo "$repository" --arg rid "$GITHUB_REPOSITORY_ID" \
        '.full_name == $repo and (.id|tostring) == $rid' \
        "$root/$prefix.repo" >/dev/null || refuse binding repository
    jq -e --arg branch "$policy_ref" '.default_branch == $branch' \
        "$root/$prefix.repo" >/dev/null || refuse binding policy-ref-mismatch
    api "repos/$repository/pulls/$pr" "$root/$prefix.pr"
    jq -e --arg repo "$repository" --arg rid "$GITHUB_REPOSITORY_ID" --argjson pr "$pr" \
        '.number == $pr and .state == "open" and .merged_at == null and
         .base.repo.full_name == $repo and (.base.repo.id|tostring) == $rid and
         (.head.ref|type) == "string" and (.base.ref|type) == "string" and
         (.head.ref|test("[\u0000-\u001f\u007f]")|not) and
         (.base.ref|test("[\u0000-\u001f\u007f]")|not) and
         (.head.sha|test("^[0-9a-f]{40}$")) and
         (.head.repo.id|type) == "number" and .head.repo.id > 0 and
         (.head.repo.id|floor) == .head.repo.id' \
        "$root/$prefix.pr" >/dev/null || refuse binding pr
    target_branch="$(jq -r .base.ref "$root/$prefix.pr")"
    head_branch="$(jq -r .head.ref "$root/$prefix.pr")"
    head="$(jq -r .head.sha "$root/$prefix.pr")"
    if ! branch "$target_branch" || ! branch "$head_branch" || ! sha "$head"; then
        refuse binding ref
    fi
    target_encoded="$(printf '%s' "$target_branch" | jq -sRr @uri)"
    api "repos/$repository/git/ref/heads/$policy_encoded" "$root/$prefix.policy"
    api "repos/$repository/git/ref/heads/$target_encoded" "$root/$prefix.target"
    jq -e --arg ref "$policy_source" \
        '.ref == $ref and .object.type == "commit" and (.object.sha|test("^[0-9a-f]{40}$"))' \
        "$root/$prefix.policy" >/dev/null || refuse binding policy
    jq -e --arg ref "refs/heads/$target_branch" \
        '.ref == $ref and .object.type == "commit" and (.object.sha|test("^[0-9a-f]{40}$"))' \
        "$root/$prefix.target" >/dev/null || refuse binding target
    policy="$(jq -r .object.sha "$root/$prefix.policy")"
    target="$(jq -r .object.sha "$root/$prefix.target")"
    if ! sha "$policy" || ! sha "$target"; then refuse binding commit; fi
    # Compare identity/ref/object fields, not irrelevant mutable title/body or review metadata.
    jq -cn --arg policy_ref "$policy_source" --arg target "$target_branch" --arg head "$head" \
        --arg policy "$policy" --arg tip "$target" --slurpfile p "$root/$prefix.pr" \
        '[$policy_ref,$target,$head,$policy,$tip,$p[0].head.ref,$p[0].head.repo.id]' > "$root/$prefix.binding"
}
phase=binding
bounded 120 "$root/event" "$root/event.err" cat -- "$event" || refuse binding event-read
one_object "$root/event" || refuse binding event-json
jq -e --arg repo "$repository" --arg rid "$GITHUB_REPOSITORY_ID" --arg event "$GITHUB_EVENT_NAME" '
    (if $event == "pull_request" then
        (.action | IN("opened","reopened","synchronize","edited","ready_for_review","converted_to_draft")) and
        .number == .pull_request.number
     else
        (.action | IN("submitted","dismissed")) and
        (has("number") == false or .number == .pull_request.number)
     end) and
    (.pull_request.number | type == "number" and . > 0 and floor == .) and .repository.full_name == $repo and
    (.repository.id|tostring) == $rid and .pull_request.base.repo.full_name == $repo and
    (.pull_request.base.repo.id|tostring) == $rid and
    (.pull_request.head.ref|type) == "string" and (.pull_request.base.ref|type) == "string" and
    (.pull_request.head.ref|test("[\u0000-\u001f\u007f]")|not) and
    (.pull_request.base.ref|test("[\u0000-\u001f\u007f]")|not) and
    (.pull_request.head.sha|test("^[0-9a-f]{40}$")) and
    (.pull_request.base.sha|test("^[0-9a-f]{40}$"))
' "$root/event" >/dev/null || refuse binding event
pr="$(jq -r .pull_request.number "$root/event")"
[[ "$pr" =~ ^[1-9][0-9]*$ ]] || refuse binding pr
sha "$(jq -r .pull_request.base.sha "$root/event")" || refuse binding event-base
read_bindings before
jq -e --slurpfile p "$root/before.pr" '
    .pull_request.head.sha == $p[0].head.sha and .pull_request.head.ref == $p[0].head.ref and
    .pull_request.head.repo.id == $p[0].head.repo.id and .pull_request.base.ref == $p[0].base.ref
' "$root/event" >/dev/null || refuse binding event-mismatch

phase=fetch
objects="$root/objects"
git init -q --template="$root/template" "$objects" >/dev/null 2>"$root/git.err" || refuse fetch init
# No caller config/template, object store, alternates, hooks, attributes, worktree or remotes.
git -C "$objects" config core.hooksPath "$root/template"
git -C "$objects" config credential.helper ''
git -C "$objects" config protocol.allow never
git -C "$objects" config protocol.https.allow always
git -C "$objects" config http.followRedirects false
git -C "$objects" config fetch.recurseSubmodules false
git -C "$objects" config maintenance.auto false
git -C "$objects" config gc.auto 0
cat > "$root/askpass" <<'ASKPASS'
#!/bin/bash
case "$1" in
    *Username*) printf '%s\n' x-access-token ;;
    *Password*) printf '%s\n' "$GH_TOKEN" ;;
    *) exit 1 ;;
esac
ASKPASS
chmod 700 "$root/askpass"
export GIT_ASKPASS="$root/askpass"
canonical="refs/remotes/origin/$target_branch"
url="https://github.com/$repository.git"
bounded 120 "$root/fetch.out" "$root/fetch.err" git -C "$objects" fetch --no-tags --no-recurse-submodules \
    --no-auto-maintenance "$url" \
    "+refs/pull/$pr/head:refs/prdkit/head" \
    "+refs/heads/$target_branch:$canonical" \
    "+$policy_source:$policy_tracking" || refuse fetch acquisition
[ "$(git -C "$objects" rev-parse --verify --quiet "$policy_tracking")" = "$policy" ] ||
    refuse binding policy-commit-mismatch
[ "$(git -C "$objects" rev-parse refs/prdkit/head)" = "$head" ] &&
    [ "$(git -C "$objects" rev-parse "$canonical")" = "$target" ] || refuse binding fetched-commit
[ "$(git -C "$objects" rev-parse --is-shallow-repository)" = false ] &&
    [ ! -e "$objects/.git/objects/info/alternates" ] &&
    [ ! -e "$objects/.git/shallow" ] || refuse fetch incomplete
git -C "$objects" symbolic-ref refs/remotes/origin/HEAD "$policy_tracking"
# No URL/remote remains from which a native object read could implicitly acquire objects.
unset GIT_ASKPASS
rm "$root/askpass"
bounded 120 "$root/fsck.out" "$root/fsck.err" git -C "$objects" fsck --full --no-reflogs \
    "$head" "$target" "$policy" || refuse fetch incomplete

extract() {
    local file entry mode kind blob path
    for file in check-review.sh common.sh spec-lib.sh spec-navigation-lib.sh review-policy-lib.sh github-reviews-lib.sh; do
        path=".prdkit/scripts/bash/$file"
        entry="$(git -C "$objects" ls-tree "$policy" -- "$path")" || return 1
        read -r mode kind blob path <<< "$entry"
        case "$mode:$kind:$path" in
            "100644:blob:.prdkit/scripts/bash/$file"|"100755:blob:.prdkit/scripts/bash/$file") ;;
            *) return 1 ;;
        esac
        git -C "$objects" cat-file blob "$blob" > "$root/trusted/$file" || return 1
    done
}
phase=extract
bounded 120 "$root/extract.out" "$root/extract.err" /bin/bash --noprofile --norc -euc \
    "$(declare -f extract)"$'\n''objects="$1"; policy="$2"; root="$3"; extract' \
    _ "$objects" "$policy" "$root" || refuse extract closure
# Source ONLY the two enumerated, policy-bound native parsers; never adjacent caller files.
source "$root/trusted/common.sh"
source "$root/trusted/spec-lib.sh"
if ! spec_trusted_refs "$objects" --policy-errors "$root/policy-error" > "$root/approved"; then
    # Only the trusted reader's line-numbered message, never raw tool stderr.
    [ ! -s "$root/policy-error" ] || policy_error="$(cat "$root/policy-error")"
    refuse binding invalid-policy
fi
grep -qxF "$canonical" "$root/approved" || refuse binding unapproved-target

select_carrier() {
    local start path id bases
    bases="$(git -C "$objects" merge-base --all "$target" "$head")" || return 1
    sha "$bases" || return 1 # exactly one complete merge base; embedded newline is invalid
    start="$bases"
    if is_change_id "$head_branch"; then printf '%s\n' "$head_branch"; return 0; fi
    # Capability failure is fatal. --full-history includes side branches; combined diffs include
    # merge-created additions, but not imports from a parent. NUL paths never enter line parsing.
    capture_file "$root/net" git -C "$objects" diff --no-renames --no-ext-diff --no-textconv --diff-filter=A \
        --name-only -z "$start" "$head" -- specs/changes/ || return 1
    capture_file "$root/history" git -C "$objects" log --full-history --diff-merges=combined --no-renames --no-ext-diff \
        --no-textconv --diff-filter=A --name-only -z --format= "$start..$head" \
        -- specs/changes/ || return 1
    capture_file "$root/baseline" git -C "$objects" ls-tree -r -z --name-only "$start" -- specs/changes/ || return 1
    : > "$root/existing"; : > "$root/introduced"
    while IFS= read -r -d '' path; do
        case "$path" in specs/changes/*.md)
            id="${path#specs/changes/}"; id="${id%.md}"
            if is_change_id "$id"; then printf '%s\n' "$id" >> "$root/existing"; fi ;;
        esac
    done < "$root/baseline"
    cat "$root/net" "$root/history" > "$root/additions" || return 1
    while IFS= read -r -d '' path; do
        case "$path" in specs/changes/*.md)
            id="${path#specs/changes/}"; id="${id%.md}"
            if is_change_id "$id" && ! grep -qxF "$id" "$root/existing"; then
                printf '%s\n' "$id" >> "$root/introduced"
            fi ;;
        esac
    done < "$root/additions"
    sort -u "$root/introduced" > "$root/unique" || return 1
    [ "$(wc -l < "$root/unique")" -eq 1 ] || return 1
    cat "$root/unique"
}
phase=select
bounded 120 "$root/selected" "$root/select.err" /bin/bash --noprofile --norc -euc \
    "$(declare -f select_carrier capture_file sha is_change_id)"$'\n'\
'objects="$1"; root="$2"; target="$3"; head="$4"; head_branch="$5"; set -o pipefail; select_carrier' \
    _ "$objects" "$root" "$target" "$head" "$head_branch" || refuse select assignment
selected="$(cat "$root/selected")"
is_change_id "$selected" || refuse select assignment

phase=native
if bounded 180 "$root/native.json" "$root/native.err" /bin/bash --noprofile --norc \
    "$root/trusted/check-review.sh" --json --diagnostics --repo "$objects" \
    --base "$canonical" --head "$head" --pr "https://github.com/$repository/pull/$pr" "$selected"; then
    : # Keep the observed command exit, distinct from timeout/capture classification.
fi
native_exit="$bounded_exit"
check_signal
if one_object "$root/native.json"; then native="$(cat "$root/native.json")"
elif [ -z "$bounded_limit" ]; then refuse native invalid-json; fi
case "$bounded_limit" in
    overflow) refuse native capture-limit ;;
    timeout) refuse native execution-limit ;;
esac
check_signal
jq -e '
    (.problems|type) == "array" and (.authority|type) == "string" and
    (.result == "pass" or .result == "fail" or .result == "preview")
' "$root/native.json" >/dev/null || refuse native result-shape
if [ "$native_exit" -eq 0 ]; then
    # Previously installed checkers bind policy_at but do not emit policy_ref.
    jq -e --arg id "$selected" --arg pr "https://github.com/$repository/pull/$pr" \
        --arg h "$head" --arg ref "$canonical" --arg p "$policy" --arg t "$target" \
        --arg policy_ref "$policy_tracking" '
        .result == "pass" and .authority == "host" and .problems == [] and .change == $id and
        .pr == $pr and .head == $h and .pr_head == $h and .head_is_pr_head == true and
        .base == $ref and .base_trusted == true and .policy_at == $p and
        .diagnostics.head == $h and .diagnostics.base_ref == $ref and .diagnostics.base_at == $t and
        .diagnostics.policy_at == $p and
        (.diagnostics | (has("policy_ref") | not) or .policy_ref == $policy_ref) and
        .blocking == [] and (.reviews | type == "array") and
        all(.reviews[]; (.reviewer|type=="number" and .>0 and floor==.) and (.role|IN("publisher","owner"))) and
        ([.reviews[].reviewer] | length == (unique|length)) and
        (if has("areas") then
            .reviews as $reviews | .tier as $tier |
            (.areas|type=="array" and length>0) and
            all(.areas[]; . as $a |
                (.area|type=="string") and
                ((.reviewer==null and .review==null) or
                 ((.review|type=="string") and any($reviews[]; .role=="publisher" and
                     .reviewer==$a.reviewer and .review==$a.review)))) and
            ($tier=="T0" or any(.areas[]; .review!=null and ($tier!="T3" or .area=="design")))
         else true end) and
        (.tier == "T0" or
         ((.tier | IN("T1","T2","T3")) and any(.reviews[]; .role=="publisher") and
          (.tier != "T3" or any(.reviews[]; .role=="owner"))))
    ' "$root/native.json" >/dev/null || refuse native inconsistent-result
else
    jq -e --argjson rc "$native_exit" '
        (.result == "fail" and .authority == "host" and (.problems|length) > 0 and $rc == 1) or
        (.result == "preview" and .authority != "host" and
            (($rc == 1) or ($rc == 3 and .problems == [])))
    ' "$root/native.json" >/dev/null || refuse native inconsistent-result
fi
phase=binding
read_bindings after
cmp -s "$root/before.binding" "$root/after.binding" || refuse binding context-moved
transport=complete; status=1
[ "$native_exit" -ne 0 ] || status=0
exit "$status"
