#!/usr/bin/env bash
# Object-bound package context. Standard tools only. Caller owns package/scratch paths,
# inventory(), safe_path(), HEAD_SHA, POLICY_AT, CHANGE_ID and SCRIPT_DIR.
# No candidate scripts execute. The installed bounds command is called on object materialization.
# shellcheck disable=SC2154,SC2034 # Builder globals and bounds' cross-source outputs.

context_select_spans() { # source file; at/selector passed as awk -v arguments
    awk "$@" "$SPEC_SKIP_AWK"'
        { text[NR]=$0; if (spec_skip($0)) next; live[NR]=1
          if ($0 ~ /^#+[ \t]+/) {
              h=$0; sub(/[ \t]+.*/, "", h); level[NR]=length(h)
              title=$0; sub(/^#+[ \t]+/, "", title); sub(/[ \t]+#+[ \t]*$/, "", title)
              slug=tolower(title); gsub(/[^a-z0-9 _-]/, "", slug); gsub(/ /, "-", slug)
              if (selector!="" && (title==selector || slug==selector)) { count++; at=NR }
          } }
        END {
            if (selector!="" && count!=1) exit 1
            if (!at || !live[at]) exit 1
            depth=level[at] ? level[at] : 100
            for (i=at-1;i>0;i--) if (level[i] && level[i]<depth) { parents[++n]=i; depth=level[i] }
            for (i=n;i>0;i--) print parents[i] "\t" parents[i]
            end=NR
            if (level[at]) {
                for (i=at+1;i<=NR;i++) if (level[i] && level[i]<=level[at]) { end=i-1; break }
            } else if (text[at] ~ /^\|/) {
                # Column names and delimiter, never sibling rows, give the selected row meaning.
                first=at; while (first>1 && text[first-1] ~ /^\|/) first--
                if (first>=at || text[first+1] !~ /^\|[-| :\t]+$/) exit 1
                print first "\t" first+1
                end=at
            }
            else if (text[at] ~ /^[ \t]*[-*+][ \t]+/) {
                match(text[at],/^[ \t]*/); indent=RLENGTH; gap=0
                for (i=at+1;i<=NR;i++) {
                    if (!live[i]) continue
                    if (text[i] ~ /^[ \t]*$/) { gap=1; continue }
                    match(text[i],/^[ \t]*/); d=RLENGTH
                    if (level[i] || text[i] ~ /^\|/ || (d<=indent && (gap || text[i] ~ /^[ \t]*[-*+][ \t]+/))) { end=i-1; break }
                    gap=0
                }
            } else exit 1
            print at "\t" end
        }'
}

context_gap() {
    # Verification is a dispatch reminder even for a complete structural package.
    [ "$1" = verification ] || reuse_context=custom
    printf '%s\n' "$*" >> "$PACKAGE_DIR/context-gaps.txt"
    printf -- '-\t-\t-\t-\t-\t-\t%s\t%s\n' "$(spec_field "$1")" "$(spec_field "$*")" >> "$PACKAGE_DIR/context-manifest.tsv"
}

context_object() { # rev path -> blob; never dereference a symlink/tree/submodule
    local entry
    safe_path "$2" || return 1
    entry="$(git -C "$REPO_ROOT" ls-tree "$1" -- ":(literal)$2")" || return 1
    case "$entry" in 100644\ blob\ *|100755\ blob\ *) ;; *) return 1 ;; esac
    entry="${entry#* blob }"; printf '%s\n' "${entry%%$'\t'*}"
}

context_span() { # rev path first last obligation reason
    local rev="$1" path="$2" first="$3" last="$4" obligation="$5" reason="$6" blob name=""
    local artifact source_rev source_path source_blob source_first source_last rest fresh=false
    if ! blob="$(context_object "$rev" "$path")" ||
        ! GIT_NO_LAZY_FETCH=1 git -C "$REPO_ROOT" cat-file blob "$blob" > "$CTX_TMP/blob" ||
        ! review_file_without_nul "$CTX_TMP/blob"; then
        context_gap "$obligation" "missing/unsafe/unreadable context $rev:$path"; return
    fi
    [ "$last" != all ] || last="$(awk 'END { print NR }' "$CTX_TMP/blob")"
    [ "$last" != 0 ] || first=0
    # Reuse exact spans, recording every obligation below. Explicit snapshots live exclusively
    # under context/<repo-path>; generated files must never share that namespace.
    while IFS=$'\t' read -r artifact source_rev source_path source_blob source_first source_last rest; do
        if [ "$source_rev" = "$rev" ] && [ "$source_path" = "$path" ] && [ "$source_blob" = "$blob" ] &&
            [ "$source_first" = "$first" ] && [ "$source_last" = "$last" ]; then name="$artifact"; break; fi
    done < "$PACKAGE_DIR/context-manifest.tsv"
    if [ -z "$name" ]; then
        name="context-sections/$CTX_N.txt"; CTX_N=$((CTX_N+1)); fresh=true
        mkdir -p "$PACKAGE_DIR/context-sections"
        if [ -e "$PACKAGE_DIR/$name" ] || [ -L "$PACKAGE_DIR/$name" ]; then
            echo "ERROR: generated context artifact collision: $name; incomplete round retained" >&2; exit 1
        fi
        if [ "$last" = 0 ]; then
            : > "$PACKAGE_DIR/$name"; context_gap "$obligation" "empty required context $rev:$path"
        else sed -n "${first},${last}p" "$CTX_TMP/blob" > "$PACKAGE_DIR/$name"; fi
    fi
    case "$obligation" in parent|checkpoint|child-row|parent-obligation)
        awk "$SPEC_SKIP_AWK $SPEC_ID_AWK"'
            { if (!spec_skip($0)) spec_ids($0, "") }' "$PACKAGE_DIR/$name" |
            cut -d "$(printf '\037')" -f1 >> "$CTX_TMP/parent-ids"
        if grep -q '](' "$PACKAGE_DIR/$name"; then
            context_gap parent-link "selected parent obligation contains a link; supply its required captured context explicitly"
        fi ;;
    esac
    if $fresh; then inventory "$name" "$rev:$path:$first-$last"; fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$rev" "$path" "$blob" "$first" "$last" \
        "$(spec_field "$obligation")" "$(spec_field "$reason")" >> "$PACKAGE_DIR/context-manifest.tsv"
}

context_select() { # rev path selector obligation reason [source line]
    local rev="$1" path="$2" selector="$3" obligation="$4" reason="$5" at="${6:-0}" blob a b spans
    if ! blob="$(context_object "$rev" "$path")" ||
        ! GIT_NO_LAZY_FETCH=1 git -C "$REPO_ROOT" cat-file blob "$blob" > "$CTX_TMP/select" ||
        ! review_file_without_nul "$CTX_TMP/select"; then
        context_gap "$obligation" "missing/unsafe context $rev:$path"; return
    fi
    if ! spans="$(context_select_spans -v at="$at" -v selector="$selector" < "$CTX_TMP/select")"; then
        context_gap "$obligation" "ambiguous/missing/unsupported section $path#$selector (line $at)"; return
    fi
    while IFS=$'\t' read -r a b; do
        context_span "$rev" "$path" "$a" "$b" "$obligation" "$reason"
    done <<< "$spans"
}

context_id() { # actual ID -> unique definition, never select an arbitrary sibling
    local wanted="$1" reason="$2" locations location path line
    locations="$(awk -F '\037' -v id="$wanted" '$1==id { print $7 }' "$CTX_TMP/items")"
    if [ -z "$locations" ] || [[ "$locations" == *$'\n'* ]]; then
        context_gap "$wanted" "missing/ambiguous spec ID $wanted"; return
    fi
    location="$locations"; path="${location%:*}"; line="${location##*:}"
    context_select "$HEAD_SHA" "$path" "" "$wanted" "$reason" "$line"
}

review_context_build() {
    local path blob rev record record_rev last parent parent_field header checkpoint row count a b id location reason target anchor line entry
    CTX_TMP="$PACKAGE_SCRATCH/context"; mkdir -p "$CTX_TMP/material/.prdkit"
    CTX_N=1
    printf 'artifact\tsource_rev\tsource_path\tsource_blob\tfirst_line\tlast_line\tobligation\treason\n' > "$PACKAGE_DIR/context-manifest.tsv"
    : > "$PACKAGE_DIR/context-gaps.txt"
    if ! git -C "$REPO_ROOT" diff --quiet "$HEAD_SHA" --; then
        context_gap worktree "working tree differs from bound HEAD; only committed objects are packaged, verify local evidence applicability"
    fi
    record="specs/changes/$CHANGE_ID.md"; record_rev="$HEAD_SHA"
    if ! context_object "$record_rev" "$record" >/dev/null; then
        entry="$(git -C "$REPO_ROOT" ls-tree "$HEAD_SHA" -- ":(literal)$record" 2>/dev/null)" || entry=unreadable
        if [ -n "$entry" ]; then
            record_rev=""; context_gap child "actual child record is unsafe/unreadable at HEAD: $record; no historical substitution"
        else
            last="$(git -C "$REPO_ROOT" log -1 --format=%H "$HEAD_SHA" -- "$record" 2>/dev/null || true)"
            if [ -n "$last" ] && context_object "$last^" "$record" >/dev/null; then
                record_rev="$(git -C "$REPO_ROOT" rev-parse "$last^")"
            else record_rev=""; context_gap child "actual child record missing or unrecoverable: $record"; fi
        fi
    fi
    # Materialize canon from H; bounds scope alone may use the recovered historical child.
    # No checkout, index, hooks or candidate helper is used.
    printf 'path\tsource_rev\tsource_blob\n' > "$PACKAGE_DIR/bounds-inputs.tsv"
    if ! git -C "$REPO_ROOT" ls-tree -r -z --name-only "$HEAD_SHA" -- specs > "$CTX_TMP/spec-paths"; then
        context_gap canon "cannot completely enumerate spec entries"
    fi
    while IFS= read -r -d '' path; do
        case "$path" in specs/*.md) ;; *) continue ;; esac
        case "$path" in specs/changes/*) continue ;; esac
        if blob="$(context_object "$HEAD_SHA" "$path")"; then
            mkdir -p "$(dirname "$CTX_TMP/material/$path")"
            if ! GIT_NO_LAZY_FETCH=1 git -C "$REPO_ROOT" cat-file blob "$blob" > "$CTX_TMP/material/$path" ||
                ! review_file_without_nul "$CTX_TMP/material/$path"; then
                context_gap canon "unreadable/non-text input $path"; rm -f "$CTX_TMP/material/$path"
            fi
        else context_gap canon "unsafe spec input $(printf '%q' "$path")"; fi
    done < "$CTX_TMP/spec-paths"
    if [ -n "$record_rev" ]; then
        context_span "$record_rev" "$record" 1 all child "actual record (historical when source_rev differs from HEAD)"
        mkdir -p "$CTX_TMP/material/specs/changes"
        blob="$(context_object "$record_rev" "$record")"
        git -C "$REPO_ROOT" cat-file blob "$blob" > "$CTX_TMP/material/$record"
        printf '%s\t%s\t%s\n' "$record" "$record_rev" "$blob" >> "$PACKAGE_DIR/bounds-inputs.tsv"
    fi
    while IFS= read -r path; do
        blob="$(context_object "$HEAD_SHA" "$path")"
        printf '%s\t%s\t%s\n' "$path" "$HEAD_SHA" "$blob" >> "$PACKAGE_DIR/bounds-inputs.tsv"
    done < <(spec_files "$CTX_TMP/material")
    spec_items "$CTX_TMP/material" > "$CTX_TMP/items"
    # Reuse the actual bounds computation, including all/later-stage/exception widening.
    source "$SCRIPT_DIR/bounds.sh"
    if [ -n "$record_rev" ] && (bounds_for_root "$CTX_TMP/material" "$CHANGE_ID") > "$PACKAGE_DIR/bounds.txt" 2> "$PACKAGE_DIR/bounds.stderr"; then
        while IFS= read -r id; do context_id "$id" "returned by bounds.sh (later-stage limits preserved)"; done \
            < <(awk '/^- [CNQ]-[0-9]/ { print $2 }' "$PACKAGE_DIR/bounds.txt")
    else context_gap bounds "bounds unavailable; inspect bounds.stderr"; fi
    for path in bounds-inputs.tsv bounds.txt bounds.stderr; do
        [ ! -f "$PACKAGE_DIR/$path" ] || inventory "$path" "object-materialized inputs; installed bounds.sh"
    done
    # Policy is frozen at the trusted default; proposed policy remains in the full diff.
    for path in .prdkit/memory/constitution.md .prdkit/memory/agents.md .prdkit/memory/config.md; do
        if [ -n "$POLICY_AT" ]; then context_span "$POLICY_AT" "$path" 1 all policy "trusted default-branch policy"
        else context_gap policy "no trusted policy binding for $path"; fi
    done
    for path in specs/product.md specs/architecture.md; do
        context_span "$HEAD_SHA" "$path" 1 all canon "protocol-required canon"
    done
    path=specs/design.md
    if [ -f "$CTX_TMP/material/$path" ]; then
        context_span "$HEAD_SHA" "$path" 1 all canon "optional L1 UI design"
    fi
    # Parts across interfaces cannot be inferred semantically from paths. Conservatively supply
    # the committed part canon rather than silently dropping a cross-interface obligation.
    while IFS= read -r path; do
        case "$path" in specs/parts/*) context_span "$HEAD_SHA" "$path" 1 all parts "part and cross-interface obligations" ;; esac
    done < <(spec_files "$CTX_TMP/material")
    if [ -n "$record_rev" ]; then
        header="$(awk "$SPEC_SKIP_AWK $SPEC_CHANGE_TAG_AWK"'
            { $0 = change_without_tags($0) }
            { if (spec_skip($0)) next } /^## / { exit } { print }' "$CTX_TMP/material/$record")"
        count="$(printf '%s\n' "$header" | awk '{ n+=gsub(/\*\*Parent\*\*:/,"&") } END { print n+0 }')"
        parent_field="$(printf '%s\n' "$header" | awk '/\*\*Parent\*\*:/ { sub(/^.*\*\*Parent\*\*:[[:space:]]*/, ""); sub(/·.*/, ""); sub(/[[:space:]]+$/, ""); print }')"
        parent="$(printf '%s\n' "$parent_field" | grep -Eo '[0-9]{3,}-[a-z0-9]+(-[a-z0-9]+)*' | LC_ALL=C sort -u || true)"
        if [ "$count" -gt 1 ] || [[ "$parent" == *$'\n'* ]]; then context_gap parent "ambiguous Parent field"
        elif [ -n "$parent" ]; then
            path="specs/changes/$parent.md"
            if blob="$(context_object "$HEAD_SHA" "$path")" &&
                git -C "$REPO_ROOT" cat-file blob "$blob" > "$CTX_TMP/parent"; then
                b="$(awk '/^## / { print NR-1; exit }' "$CTX_TMP/parent")"
                if [ -n "$b" ]; then context_span "$HEAD_SHA" "$path" 1 "$b" parent "parent header"
                else context_gap parent "unsupported parent structure without sections: $path"; fi
                checkpoint="$(awk "$SPEC_SKIP_AWK"'
                    { if (spec_skip($0)) next }
                    /^#+ / && tolower($0) ~ /outcome checkpoint/ { print NR }' "$CTX_TMP/parent")"
                if [ -z "$checkpoint" ] || [[ "$checkpoint" == *$'\n'* ]]; then context_gap parent "missing/ambiguous outcome checkpoint in $path"
                else context_select "$HEAD_SHA" "$path" "" checkpoint "required parent checkpoint" "$checkpoint"; fi
                row="$(awk -v id="$CHANGE_ID" "$SPEC_SKIP_AWK"'
                    { if (spec_skip($0)) next }
                    /^\|/ && $0 ~ ("(^|[^a-z0-9-])" id "([^a-z0-9-]|$)") { print NR }' "$CTX_TMP/parent")"
                if [ -z "$row" ] || [[ "$row" == *$'\n'* ]]; then context_gap parent "missing/ambiguous child row in $path"
                else context_select "$HEAD_SHA" "$path" "" child-row "actual child row" "$row"; fi
                while IFS= read -r line; do
                    context_select "$HEAD_SHA" "$path" "" parent-obligation "parent dependency/hold/limit/architecture/goal/lane" "$line"
                done < <(awk "$SPEC_SKIP_AWK"'
                    { if (spec_skip($0)) next }
                    /^#+ / && tolower($0) ~ /depend|hold|limit|architect|critical path|original goal|lane/ { print NR }' "$CTX_TMP/parent")
            else context_gap parent "missing/unsafe parent $path"; fi
        elif [ -n "$parent_field" ]; then
            printf '%s\n' "$parent_field" | grep -Eiq '^(none|—|n/a)$' ||
                context_gap parent "unsupported Parent field; supply explicit context"
        fi
        # Resolve all actual child citations, not just its declared Bounds.
        while IFS= read -r id; do context_id "$id" "child spec/acceptance/design/dependency citation"; done \
            < <(spec_citations "$CTX_TMP/material" "$record" | cut -d "$(printf '\037')" -f1 | LC_ALL=C sort -u)
        while IFS= read -r line; do context_gap reference "$line"; done < <(
            awk "$SPEC_SKIP_AWK"'
                { if (spec_skip($0)) next }
                /\]\(https?:/ { print "external child link at line " NR " requires captured context" }
                /-[0-9][0-9]+[[:space:]]*(to|through|–|—|\.\.)[[:space:]]*(P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCDN])?-[0-9]/ {
                    print "ID range at child line " NR " requires explicit complete context"
                }' "$CTX_TMP/material/$record")
        # spec_links supplies safely normalized paths. Anchored links require a unique heading;
        # unsupported forms remain gaps, never a guessed section.
        while IFS=$'\037' read -r path line target; do
            [ -n "$target" ] || continue
            count="$(sed -n "${line}p" "$CTX_TMP/material/$record" | awk '{ n=gsub(/]\(/,"&"); print n }')"
            if [ "$count" != 1 ]; then context_gap link "multiple/unsupported links at child line $line; select explicit context"; continue; fi
            anchor="$(sed -n "${line}p" "$CTX_TMP/material/$record" | sed -n 's/.*](.*#\([^ )]*\)).*/\1/p')"
            if [ -n "$anchor" ]; then context_select "$HEAD_SHA" "$target" "$anchor" link "child design/dependency/spec link"
            else context_gap link "explicit section needed for $target (child line $line); --context can supply a whole file"; fi
        done < <(spec_links "$CTX_TMP/material" "$record")
    fi
    if [ -f "$CTX_TMP/parent-ids" ]; then
        while IFS= read -r id; do context_id "$id" "selected parent obligation citation"; done \
            < <(LC_ALL=C sort -u "$CTX_TMP/parent-ids")
    fi
    # Changed canon: include complete changed sections; additions without supported headings
    # stay visible as gaps as well as in the unabridged diff.
    if ! git -C "$REPO_ROOT" diff --ignore-submodules=none -z --name-only --no-renames "$BASE_SHA" "$HEAD_SHA" > "$CTX_TMP/changed-paths"; then
        context_gap changed-spec "cannot completely enumerate changed canon"
    fi
    while IFS= read -r -d '' path; do
        case "$path" in specs/changes/*) continue ;; specs/*) ;; *) continue ;; esac
        if blob="$(context_object "$HEAD_SHA" "$path")"; then
            context_span "$HEAD_SHA" "$path" 1 all changed-spec "changed canon, unabridged"
        else context_gap changed-spec "deleted/unsafe changed canon $(printf '%q' "$path"); inspect full diff"; fi
    done < "$CTX_TMP/changed-paths"
    for path in ${CONTEXT[@]+"${CONTEXT[@]}"}; do
        blob="$(context_object "$HEAD_SHA" "$path")"
        count="$(awk 'END { print NR }' "$PACKAGE_DIR/context/$path")"; a=1
        [ "$count" != 0 ] || a=0
        printf 'context/%s\t%s\t%s\t%s\t%s\t%s\twhole-file\texplicit --context\n' \
            "$path" "$HEAD_SHA" "$path" "$blob" "$a" "$count" >> "$PACKAGE_DIR/context-manifest.tsv"
    done
    if [ "${#EVIDENCE[@]}" -eq 0 ]; then context_gap verification "no verification evidence supplied; dispatch pending"
    else context_gap verification "caller-supplied evidence requires revision/result/applicability verification; no semantic inference"; fi
}
