#!/usr/bin/env bash
# Read a single GitHub PR's reviews. No review bodies, commit authors or credentials are inputs.
#
# Invariants:
# Only the latest exact-head submission per eligible non-author account can grant approval.
# Its undismissed objections on any PR commit require a later exact-head approval by that account.
# Report open objections separately from approvals after complete, consistent pagination.
# With lenses, publisher eligibility is the union of accounts mapped to trusted reviewers.
# Only the single raw Configuration section can establish policy.
#
# Threat model:
# Review text and candidate policy are untrusted. Trust the authenticated GitHub response;
# a host failure, malformed page or changed PR binding cannot produce a host verdict.
# shellcheck disable=SC2034,SC2016 # Public outputs; jq/GraphQL variables are literal.

github_review_policy() {
    local root="$1" at="$2" config fields registry area entry
    RC_ERROR="Cannot read trusted review policy at ${at:-origin/HEAD}"
    [ -n "$at" ] || return 1
    config="$(git -C "$root" show "$at:.prdkit/memory/config.md" | review_text_stream)" || return 1
    config="$(printf '%s\n' "$config" | spec_review_config_section 2>&1)" || { RC_ERROR="$config"; return 1; }
    fields="$(printf '%s\n' "$config" | LC_ALL=C awk "$SPEC_REVIEW_SPACES_AWK"'
        function normalized(s) {
            gsub(/`/, "", s)
            gsub(/\305\277/, "s", s) # long s also case-folds to an ASCII key letter
            return tolower(spaces(s))
        }
        function fail(reason, key) {
            print reason ((key in line) ? " (config.md line " line[key] ")" : "")
            exit 1
        }
        {
            ntext=normalized($0)
            if (match(ntext,/review-(lenses|publishers)/) && substr(ntext,RSTART+RLENGTH) ~ /[:=]/) {
                key=substr(ntext,RSTART,RLENGTH)
                if (index($0,"- " key ":")!=1)
                    malformed="Invalid " key " key at config.md line " NR ": expected - " key ": <value>"
            }
        }
        /^- review-(host|publishers|owner|publisher-isolation|lenses):/ {
            key=$0; sub(/:.*/, "", key); sub(/^- /,"",key)
            sub(/^[^:]*:[[:space:]]*/,""); sub(/[[:space:]]+$/,"")
            values[key]=$0; count[key]++; line[key]=NR
        }
        END {
            if (malformed) fail(malformed)
            pub=values["review-publishers"]; lenses=values["review-lenses"]
            if (count["review-host"]!=1 || values["review-host"]!="github" ||
                count["review-owner"]!=1 || values["review-owner"]!~/^user:[1-9][0-9]*([[:space:]]*,[[:space:]]*user:[1-9][0-9]*)*$/) {
                if (count["review-lenses"]) fail("Trusted policy requires review-host: github and numeric review-owner")
                fail("Trusted policy requires review-host: github, numeric review-publishers and numeric review-owner")
            }
            if (count["review-publishers"] || !count["review-lenses"]) {
                if (count["review-publishers"]!=1 || pub!~/^(bot|user):[1-9][0-9]*([[:space:]]*,[[:space:]]*(bot|user):[1-9][0-9]*)*$/)
                    fail("Trusted policy requires review-host: github, numeric review-publishers and numeric review-owner", "review-publishers")
            }
            if (count["review-lenses"]) {
                if (count["review-lenses"]!=1 || !length(lenses)) fail("Expected one nonempty review-lenses mapping", "review-lenses")
                gsub(/[[:space:]]*,[[:space:]]*/, ",", lenses)
                n=split(lenses, entries, ",")
                for (i=1;i<=n;i++) {
                    if (entries[i]!~/^[a-z0-9][a-z0-9_.-]*=(bot|user):[1-9][0-9]*(\|(bot|user):[1-9][0-9]*)*$/)
                        fail("Invalid review-lenses entry: " entries[i], "review-lenses")
                    split(entries[i], pair, "=")
                    if (area[pair[1]]++) fail("Duplicate review-lenses area: " pair[1], "review-lenses")
                    m=split(pair[2], ids, "[|]")
                    for (j=1;j<=m;j++) {
                        id=ids[j]; num=id; sub(/^[^:]+:/,"",num)
                        if ((num in typed) && typed[num]!=id) fail("Conflicting review-lenses account types: " id, "review-lenses")
                        typed[num]=id
                        if (!(id in mapped)) union=union (length(union)?",":"") id
                        mapped[id]=1
                    }
                }
                if (count["review-publishers"]) {
                    gsub(/[[:space:]]/, "", pub); n=split(pub, ids, ",")
                    for (i=1;i<=n;i++) {
                        if (!(ids[i] in mapped)) fail("review-publishers differs from the review-lenses union", "review-publishers")
                        listed[ids[i]]=1
                    }
                    for (id in mapped) if (!(id in listed)) fail("review-publishers differs from the review-lenses union", "review-publishers")
                }
                pub=union
            }
            if (count["review-publisher-isolation"]) fail("review-publisher-isolation is unsupported by the native review gate", "review-publisher-isolation")
            print pub ";" values["review-owner"] ";" lenses
        }')" || { RC_ERROR="${fields:-Cannot parse trusted review policy}"; return 1; }
    fields="$(printf '%s' "$fields" | tr -d ' \t\r' | sed 's/bot://g;s/user://g')"
    IFS=';' read -r REVIEW_PUBLISHERS REVIEW_OWNERS REVIEW_LENSES <<< "$fields"
    if [ -n "$REVIEW_LENSES" ]; then
        RC_ERROR="Cannot read trusted review-lenses registry in agents.md"
        registry="$(git -C "$root" show "$at:.prdkit/memory/agents.md" | review_text_stream | review_registry)" || return 1
        while IFS= read -r entry; do
            area="${entry%%=*}"
            RC_ERROR="review-lenses area '$area' must name one eligible reviewer in trusted agents.md"
            [ "$(review_eligibility "$registry" "$area")" = reviewer ] || return 1
        done <<< "${REVIEW_LENSES//,/$'\n'}"
    fi
    RC_ERROR=""
}

github_review_page() { # repo number cursor; exec in a dedicated process
    local repo="$1" number="$2" cursor="$3" query args=()
    query='query PrdkitReviews($owner:String!, $name:String!, $number:Int!, $endCursor:String) {
      repository(owner:$owner,name:$name) { nameWithOwner pullRequest(number:$number) {
        number state headRefOid baseRefName author { ... on User { databaseId } ... on Bot { databaseId } }
        reviews(first:100,after:$endCursor) { totalCount nodes {
          fullDatabaseId state submittedAt commit { oid } author { ... on User { databaseId } ... on Bot { databaseId } }
        } pageInfo { hasNextPage endCursor } }
      } }
    }'
    [ -z "$cursor" ] || args=(-f "endCursor=$cursor")
    exec gh api graphql --hostname github.com -f query="$query" -f owner="${repo%/*}" -f name="${repo#*/}" \
        -F number="$number" ${args[@]+"${args[@]}"}
}

# Evidence only. Input is validated account<TAB>submittedAt<TAB>JSON records
# in GitHub page order. Full ISO seconds sort lexically; a tie goes to the
# later submission in host order, as in the policy replay below.
github_review_latest() {
    LC_ALL=C awk -F '\t' '
        {
            account="id:" $1
            if (!(account in time) || $2 >= time[account]) {
                time[account]=$2
                latest[account]=$3
            }
        }
        END { for (account in latest) print latest[account] }'
}

github_review_fetch() { # URL private-work expected-head expected-base
    local url="$1" work="$2" expected="$3" base="$4" repo number cursor="" next count=0 total="" page_total
    local re='^https://github\.com/([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)/pull/([1-9][0-9]*)$'
    RC_ERROR='--pr must be a github.com pull request URL'
    [[ "$url" =~ $re ]] || return 1
    repo="${BASH_REMATCH[1]}"; number="${BASH_REMATCH[2]}"
    RC_ERROR='online review checks require gh and jq'
    command -v gh >/dev/null && command -v jq >/dev/null || return 1
    RC_ERROR='Incomplete, malformed or non-advancing GitHub review pages'
    : > "$work/pages"; : > "$work/cursors"
    while :; do
        (github_review_page "$repo" "$number" "$cursor") > "$work/page" || return 1
        jq -esc --argjson number "$number" '
          def id: type=="number" and .>0 and .==floor and .<9007199254740992;
          def sha: type=="string" and test("^[0-9a-f]{40}\\z");
          if length==1 and (.[0].errors // [])==[] then .[0].data.repository.pullRequest else null end |
          select(.number==$number and .state=="OPEN" and (.headRefOid|sha) and (.baseRefName|type=="string") and
            (.author.databaseId|id) and (.reviews as $r |
              ($r.totalCount|type=="number" and .>=0 and .==floor) and
              ($r.nodes|type=="array" and length<=100) and
              ($r.pageInfo.hasNextPage|type=="boolean") and
              ($r.pageInfo|has("endCursor")) and
              ($r.pageInfo.endCursor==null or ($r.pageInfo.endCursor|type=="string" and length>0 and (test("[[:cntrl:]]")|not))) and
              (if $r.pageInfo.hasNextPage then ($r.nodes|length)>0 and $r.pageInfo.endCursor!=null else true end) and
              all($r.nodes[]; (.fullDatabaseId|tostring|test("^[1-9][0-9]*\\z")) and
                ((.state=="PENDING" and .submittedAt==null) or ((.state|IN("APPROVED","COMMENTED","DISMISSED","CHANGES_REQUESTED")) and
                (.submittedAt|type=="string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\\z")) and
                (.author.databaseId|id) and (.commit.oid|sha))))))' "$work/page" > "$work/validated" || return 1
        cat "$work/validated" >> "$work/pages" || return 1
        next="$(jq -r '.reviews.pageInfo.endCursor // ""' "$work/validated")" || return 1
        if [ -n "$next" ]; then
            if grep -Fxq -- "$next" "$work/cursors"; then return 1; fi
            printf '%s\n' "$next" >> "$work/cursors" || return 1
        fi
        page_total="$(jq -r .reviews.totalCount "$work/validated")" || return 1
        [ -z "$total" ] || [ "$total" = "$page_total" ] || return 1
        total="$page_total"
        count=$((count + $(jq '.reviews.nodes|length' "$work/validated")))
        if jq -e '.reviews.pageInfo.hasNextPage' "$work/validated" >/dev/null; then
            [ "$count" -lt "$total" ] || return 1
            cursor="$next"
        else break; fi
    done
    jq -se '([.[] | del(.reviews.nodes,.reviews.pageInfo)] | unique | length)==1 and
        (.[0].reviews.totalCount as $n | [.[].reviews.nodes[].fullDatabaseId|tostring] |
         length==$n and (unique|length)==$n)' "$work/pages" >/dev/null || return 1
    pr_head="$(jq -r '.headRefOid' "$work/validated")" || return 1
    RC_ERROR='The PR head or target differs from the checked head and trusted base'
    [ "$pr_head" = "$expected" ] && [ "$(jq -r .baseRefName "$work/validated")" = "$base" ] || return 1
    # Replay each account's submissions in time order (host order breaks timestamp ties).
    # GitHub marks the original review DISMISSED, so only still-live requests open blockers.
    local decisions
    decisions="$(jq -sc --arg publishers "$REVIEW_PUBLISHERS" --arg owners "$REVIEW_OWNERS" --arg head "$expected" '
        .[0].author.databaseId as $maker | ($publishers|split(",")) as $pub | ($owners|split(",")) as $own |
        [.[].reviews.nodes[] | select(.state!="PENDING" and .author.databaseId!=$maker) |
         (.author.databaseId|tostring) as $id |
         .role = (if $own|index($id)!=null then "owner" elif $pub|index($id)!=null then "publisher" else empty end)] |
        group_by(.author.databaseId) | map(reduce (sort_by(.submittedAt)[]) as $r
          ({approval:null,blocking:[]};
           ($r | {reviewer:.author.databaseId,review:(.fullDatabaseId|tostring),role}) as $entry |
           if $r.commit.oid==$head and $r.state=="APPROVED" then
               .approval=$entry | .blocking=[]
           else
               (if $r.commit.oid==$head then .approval=null else . end) |
               if $r.state=="CHANGES_REQUESTED" then .blocking += [$entry + {commit:$r.commit.oid}] else . end
           end))' "$work/pages")" || return 1
    approvals="$(jq -c '[.[].approval | select(.!=null)]' <<< "$decisions")" || return 1
    blocking="$(jq -c '[.[].blocking[]]' <<< "$decisions")" || return 1
    RC_ERROR=""
}
