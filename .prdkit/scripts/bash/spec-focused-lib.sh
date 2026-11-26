#!/usr/bin/env bash
# Focused spec grammar. Do not use this scanner for legacy navigation or budgets.

spec_focused() { # snapshot index|section|role-ranges selector expected-lines expected-bytes
    local range start=0 end=0
    if [ "$2" != role-ranges ]; then
        range="$(spec_navigation "$1" range '' "$4" "$5")" || { printf '%s\n' "$range"; return 1; }
        if [ -n "$range" ]; then read -r start end <<< "$range"; fi
    fi
    LC_ALL=C PRDKIT_FOCUS_SELECTOR="$3" awk -v mode="$2" -v omit_start="$start" -v omit_end="$end" \
        -v expected_lines="$4" -v expected_bytes="$5" \
        "$SPEC_HEADING_AWK $SPEC_PART_AWK $SPEC_RECORD_ROW_AWK"'
        function trim(s) { gsub(/^[ \t]+|[ \t]+$/, "", s); return s }
        function ltrim(s) { sub(/^[ \t]+/, "", s); return s }
        function norm(s) { s = trim(s); gsub(/[ \t]+/, " ", s); return tolower(s) }
        function fail(message) { if (!problem) { problem = message; problem_line = line_no } }
        function alias(id,   key) {
            if (!section || mode != "section" || id == "") return
            key = norm(id)
            if (!((section,key) in aliases)) {
                aliases[section,key] = 1
                ids[section] = ids[section] (ids[section] == "" ? "" : ", ") id
            }
        }
        # In LC_ALL=C, match ASCII whitespace and the UTF-8 byte suffixes for Unicode Zs.
        function trailing_space(s) {
            return s ~ /([[:space:]]|\302\240|\341\232\200|\342\200[\200-\212\257]|\342\201\237|\343\200\200)$/
        }
        function item_id(s,   span,id) {
            if (!sub(/^[-+*] \*\*/, "", s) || !match(s, /\*\*/)) return ""
            span = substr(s, 1, RSTART-1)
            if (span ~ /[][<>*`\\]/ || trailing_space(span) ||
                substr(s, RSTART+RLENGTH) !~ /^([ :.,;(]|$)/) return ""
            if (!match(span, /^((P[0-9]+(\.[0-9]+)?\.[BI]|[JRQCDN])-[0-9]+|P[0-9]+(\.[0-9]+)?)([ \t]|$)/)) return ""
            id = substr(span, 1, RLENGTH)
            sub(/[ \t]$/, "", id)
            return id
        }
        # GFM protects pipes (also in inline code) with a preceding backslash.
        # Outer pipes are optional and not counted as cells.
        function cells(s, field,   n,cell,chunk,outer) {
            s = trim(s); has_pipe = sub(/^\|/, "", s); n = 1
            while (match(s, /\|/)) {
                chunk = substr(s,1,RSTART-1); s = substr(s,RSTART+1)
                if (sub(/\\$/, "", chunk)) cell = cell chunk "|"
                else {
                    has_pipe = 1
                    field[n++] = trim(cell chunk); cell = ""
                    if (s == "") outer = 1
                }
            }
            if (outer) return n - 1
            field[n] = trim(cell s); return n
        }
        function table_header(s, at,   next_line,f,d,n,m,i) {
            if (at == NR) return 0
            next_line = raw[at+1]
            if (next_line ~ /^[ \t]/) return 0
            if (next_line ~ /^-+[ \t]*$/) return 0 # This is a Setext underline, not a delimiter.
            m = cells(next_line, d)
            for (i = 1; i <= m; i++) if (d[i] !~ /^:?-+:?$/) return 0
            n = cells(s, f)
            if (m != n) return 0
            first_cell = f[1]
            return 1
        }
        function table_row(s,   f,id,row,r) {
            cells(s, f)
            if (parts) {
                id = f[1]; sub(/[ \t].*/, "", id)
                if (spec_part_id(id)) alias(id)
            }
            if (spec_record_row(f[1], row) != "") return
            r = row["reason"]
            if (row["type"] ~ /^(HOLD|WAIVER)$/ && r ~ /^ref=/ &&
                (r !~ /^ref=specs\/authority\.md#[A-Za-z0-9][A-Za-z0-9._-]*@[0-9a-f]+$/ ||
                 length(r) - index(r, "@") != 12)) return
            alias(row["name"])
        }
        function heading(depth,   d,word) {
            for (d = 6; d >= depth; d--) if (d in open) {
                last_line[open[d]] = line_no - 1; delete open[d]
            }
            for (d = depth - 1; d > 0 && !(d in open); d--) {}
            section++
            first[section] = line_no; levels[section] = depth; titles[section] = heading_title
            tree[section] = d ? tree[open[d]] "  " : ""
            open[depth] = section
            split(heading_title,word," "); sub(/:$/, "", word[1])
            if (word[1] ~ /^[A-Za-z][A-Za-z0-9._-]*-[0-9]+$/ || spec_part_id(word[1])) alias(word[1])
        }
        function thematic(s) {
            gsub(/[ \t]/, "", s); return s ~ /^(---+|\*\*\*+|___+)$/
        }
        function fence_run(s) {
            if (!match(s, /^```+|^~~~+/)) return 0
            return RLENGTH
        }
        function atx(s) {
            return s ~ /^#+( |$)/ ? spec_heading(s) : 0
        }
        # CommonMark HTML blocks 1 and 3–7. Type 2 is the allowed comment block.
        # Type 7 needs a complete tag and is refused even after paragraph text.
        function html_block(s,   t,tag) {
            t = tolower(s)
            if (t ~ /^<(script|pre|style|textarea)([ \t>]|$)/ ||
                s ~ /^<\?|^<![A-Za-z]|^<!\[CDATA\[/) return 1
            if (match(t, /^<\/?[a-z][a-z0-9]*([ \t]|\/?>|$)/)) {
                tag = substr(t,1,RLENGTH)
                sub(/^<\/?/, "", tag); sub(/[^a-z0-9].*$/, "", tag)
                if (tag in block_tag) return 1
            }
            return s ~ closing_tag ||
                (s ~ opening_tag && t !~ /^<(script|pre|style|textarea)([ \t\/>]|$)/)
        }
        function shallow_spaces(s) { return s !~ /^    |^ *\t/ }
        function reference_boundary(s, t, at) {
            return fence_run(t) || (length(s) - length(t) <= 3 && atx(t)) ||
                (shallow_spaces(s) && (thematic(t) ||
                    t ~ /^([-+*]([ \t]|$)|[0-9]+[.)][ \t]|>|<!--|-+[ \t]*$)/ || html_block(t))) ||
                (s == t && table_header(t, at))
        }
        # A link reference at a block boundary can have a continuation destination
        # or title. A reference-like citation cannot interrupt a prose paragraph.
        function reference_title(s) {
            return s ~ /^("([^"\\]|\\.)*"|\047([^\047\\]|\\.)*\047|\(([^()\\]|\\.)*\))[ \t]*$/
        }
        function naked_destination(s,   i,c,depth,following) {
            for (i = 1; i <= length(s); i++) {
                c = substr(s,i,1)
                following = substr(s,i+1,1)
                if (c == "\\" && following != "" && index("!\"#$%&\047()*+,-./:;<=>?@[\\]^_`{|}~", following)) i++
                else if (c == "(") depth++
                else if (c == ")" && --depth < 0) return 0
            }
            return depth == 0
        }
        function reference(s,   rest,title,at,next_text,dest,i,closed) {
            if (!match(s, /^\[([^][\\]|\\.)+\]:/)) return 0
            rest = trim(substr(s, RLENGTH+1)); at = line_no
            if (rest == "" && at < NR) {
                next_text = raw[++at]
                rest = ltrim(next_text)
                if (reference_boundary(next_text, rest, at)) return 0
            }
            if (!match(rest, /^(<([^<>\\ \t]|\\.)*>|[^<> \t]+)([ \t]|$)/)) return 0
            dest = trim(substr(rest,1,RLENGTH))
            title = trim(substr(rest,RLENGTH+1))
            if (substr(dest,1,1) != "<" && !naked_destination(dest)) return 0
            if (title == "" && at < NR) {
                next_text = ltrim(raw[at+1])
                if (next_text ~ /^["\047(]/ && !reference_boundary(raw[at+1], next_text, at+1)) {
                    title = next_text; at++
                }
            }
            if (title != "") {
                if (title !~ /^["\047(]/) return 0
                closed = reference_title(title)
                for (i = at+1; i <= NR && !closed; i++) {
                    next_text = ltrim(raw[i])
                    if (next_text == "" || reference_boundary(raw[i], next_text, i)) break
                    title = title " " next_text
                    if (reference_title(title)) { at = i; closed = 1 }
                }
                if (!closed) return 0
            }
            line_no = at
            return 1
        }
        function scan(s,   t,k,depth,indent,short_spaces,id,fields) {
            t = ltrim(s); indent = length(s) - length(t)
            short_spaces = shallow_spaces(s)
            k = fence_run(t)
            if (fence != "") {
                if ((fence_indented || short_spaces) && k >= fence_size &&
                    substr(t,1,1) == fence && t ~ /^(`+|~+)[ \t]*$/) {
                    fence = ""; return
                }
                if (fence_indented && !indent) {
                    cells(s, fields)
                    if (atx(s) || item_id(s) != "" || k || s ~ /^<!--/ ||
                        has_pipe || table_header(s, line_no))
                        fail("Column-zero structure inside a possibly literal indented fence.")
                }
                return
            }
            if (comment) { if (index(s,"-->")) comment = 0; return }
            if (t == "") { previous = "blank"; table = paragraph = 0; return }
            depth = atx(t)
            if (depth && indent && indent <= 3) { fail("Indented ATX heading is ambiguous."); return }
            if (short_spaces && paragraph && t ~ /^(=+|-+)[ \t]*$/) {
                fail("Setext (underlined) headings are not supported."); return
            }
            if (short_spaces && t ~ /^<!--/) {
                comment = !index(t,"-->")
                if (s == "<!-- prdkit:navigation:v1 begin -->") footer_start = line_no
                if (s == "<!-- prdkit:navigation:v1 end -->") footer_end = line_no
                previous = "literal"; paragraph = table = 0; return
            }
            if (k && (indent || substr(t,1,1) == "~" || !index(substr(t,k+1),"`"))) {
                fence = substr(t,1,1); fence_size = k; fence_indented = indent > 0
                previous = "literal"; paragraph = table = 0; return
            }
            if (short_spaces && t ~ /^>/) { fail("Block quotes are not supported."); return }
            if (short_spaces && html_block(t)) {
                fail("HTML blocks other than comments are not supported."); return
            }
            if (short_spaces && thematic(t)) { previous = "break"; paragraph = table = 0; return }
            if (indent) {
                if (short_spaces) paragraph = 1
                previous = "opaque"; table = 0; return
            }
            if (depth) { heading(depth); previous = "heading"; paragraph = table = 0; return }
            if (t ~ /^([-+*]|[0-9]+[.)])([ \t]|$)/) {
                id = item_id(t); alias(id)
                previous = "list"; paragraph = table = 0; return
            }
            if (table) { table_row(t); previous = "table"; return }
            if (table_header(t, line_no)) {
                if (previous != "blank" && previous != "heading") {
                    fail("A table header must follow a blank line or a heading."); return
                }
                parts = tolower(first_cell) == "part"
                table = 1; paragraph = 0; previous = "table"; line_no++; return
            }
            if (!paragraph && reference(t)) { previous = "reference"; return }
            previous = "paragraph"; paragraph = 1
        }
        function label(n,   h) {
            for (h = ""; length(h) < levels[n]; h = h "#") {}
            return first[n] ": " tree[n] h " " (titles[n] == "" ? "(untitled)" : titles[n])
        }
        function index_sections(   n,count) {
            if (!section) print "No ATX headings."
            for (n = 1; n <= section; n++) {
                count = prefix[last_line[n]] - prefix[first[n]-1]
                printf "%s (%d reading line%s)\n", label(n), count, (count == 1 ? "" : "s")
            }
        }
        function cites_role(s, role,   offset,at,before,after,word) {
            word = "abcdefghijklmnopqrstuvwxyz0123456789_"
            s = tolower(s)
            for (offset = 1; (at = index(substr(s, offset), role)); offset = at + 1) {
                at += offset - 1
                before = at > 1 ? substr(s, at - 1, 1) : ""
                after = substr(s, at + length(role), 1)
                if (!(before != "" && index(word, before)) &&
                    !(after != "" && index(word, after))) return 1
            }
            return 0
        }
        function role_ranges(   role,n,end,i) {
            role = tolower(ENVIRON["PRDKIT_FOCUS_SELECTOR"])
            if (role == "") exit 1
            for (n = 1; n <= section; n++) {
                # Evidence is a nonoverlapping slice of the original bytes. Give
                # each line to its nearest heading, not also to every ancestor.
                end = n < section ? first[n+1] - 1 : NR
                for (i = first[n]; i <= end; i++) if (cites_role(raw[i], role)) {
                    printf "%d\t%d\t%s\n", first[n], end, (titles[n] == "" ? "(untitled)" : titles[n])
                    break
                }
            }
        }
        function select_section(   selector,key,n,found,count,i) {
            selector = ENVIRON["PRDKIT_FOCUS_SELECTOR"]; key = norm(selector)
            for (n = 1; n <= section; n++)
                if (key != "" && ("x" norm(titles[n]) == "x" key || (n,key) in aliases)) {
                    matched[n] = 1; found = n; count++
                }
            if (count != 1) {
                if (key == "") print "Missing section selector. Available candidates:"
                else printf "%s section selector: \"%s\". Available candidates:\n", (count ? "Ambiguous" : "No match for"), selector
                if (!section) print "  (no ATX headings)"
                for (n = 1; n <= section; n++) if (!count || matched[n])
                    print "  " label(n) (ids[n] == "" ? "" : " [IDs: " ids[n] "]")
                print "Use a unique heading or ID, or --raw to inspect the source."
                exit 10
            }
            for (i = first[found]; i <= last_line[found]; i++) if (visible(i)) printf "%d: %s\n", i, raw[i]
        }
        function visible(i) {
            return !omit_start || omit_start != footer_start || omit_end != footer_end || i < omit_start || i > omit_end
        }
        BEGIN {
            previous = "blank"
            tags = "address article aside base basefont blockquote body caption center col colgroup dd details dialog dir div dl dt fieldset figcaption figure footer form frame frameset h1 h2 h3 h4 h5 h6 head header hr html iframe legend li link main menu menuitem nav noframes ol optgroup option p param search section summary table tbody td tfoot th thead title tr track ul"
            n = split(tags, words, " ")
            for (i = 1; i <= n; i++) block_tag[words[i]] = 1
            name = "[A-Za-z][A-Za-z0-9-]*"
            attr = "[A-Za-z_:][A-Za-z0-9_.:-]*"
            value = "([^ \t\"\047=<>`]+|\"[^\"]*\"|\047[^\047]*\047)"
            opening_tag = "^<" name "([ \t]+" attr "([ \t]*=[ \t]*" value ")?)*[ \t]*/?>[ \t]*$"
            closing_tag = "^</" name "[ \t]*>[ \t]*$"
        }
        { raw[NR] = $0; bytes += length($0) + 1 }
        END {
            if (NR != expected_lines || bytes != expected_bytes) {
                printf "Incomplete focused input: expected %d lines/%d bytes, checked %d lines/%d bytes\n",
                    expected_lines, expected_bytes, NR, bytes
                exit 1
            }
            for (line_no = 1; line_no <= NR && !problem; line_no++) scan(raw[line_no])
            if (problem) { printf "%d: %s Please use --raw to inspect the source.\n", problem_line, problem; exit 10 }
            for (d in open) last_line[open[d]] = NR
            if (mode == "index") {
                for (i = 1; i <= NR; i++) {
                    s = raw[i]; gsub(/[\200-\277]/, "", s)
                    weight = length(s) > 100 ? int((length(s) + 99) / 100) : 1
                    prefix[i] = prefix[i-1] + (visible(i) ? weight : 0)
                }
                index_sections()
            } else if (mode == "role-ranges") role_ranges()
            else select_section()
        }
    ' "$1"
}
