#!/usr/bin/env bash
# Portable JSON object validation/injection for local ledger envelopes. No jq dependency.
# Existing fields/content are preserved; only absent top-level metadata is appended.
json_object_normalize() {
    LC_ALL=C PRDKIT_JSON_EVENT="${1:-}" PRDKIT_JSON_OPERATOR="${2:-}" PRDKIT_JSON_PROJECT="${3:-}" awk '
        BEGIN { for(byte=0;byte<256;byte++) ordinal[sprintf("%c",byte)]=byte }
        function whitespace(   c) { while((c=substr(doc,pos,1)) != "" && index(" \t\r\n",c)) pos++ }
        function hex_value(h,   n,i) {
            if(length(h) != 4 || h ~ /[^0-9a-fA-F]/) return -1
            n=0
            for(i=1;i<=4;i++) n=n*16+(index("0123456789abcdef0123456789ABCDEF",substr(h,i,1))-1)%16
            return n
        }
        function utf8_value(text,start,   first,n,count,i,b) {
            first=ordinal[substr(text,start,1)]; consumed=1
            if(first < 128) return first
            if(first >= 194 && first <= 223) { count=1; n=first-192 }
            else if(first >= 224 && first <= 239) { count=2; n=first-224 }
            else if(first >= 240 && first <= 244) { count=3; n=first-240 }
            else return -1
            for(i=1;i<=count;i++) {
                if(start+i > length(text)) return -1
                b=ordinal[substr(text,start+i,1)]
                if(b < 128 || b > 191) return -1
                if(i == 1 && ((first == 224 && b < 160) || (first == 237 && b > 159) ||
                             (first == 240 && b < 144) || (first == 244 && b > 143))) return -1
                n=n*64+b-128
            }
            consumed=count+1; return n
        }
        function ascii_identity(text,   result,i) {
            result=""
            for(i=1;i<=length(text);i++) result=result sprintf("%x;",ordinal[substr(text,i,1)])
            return result
        }
        function string_value(   c,e,h,n,low,start) {
            if(substr(doc,pos++,1) != "\"") return 0
            decoded=""; identity=""
            while(pos <= length(doc)) {
                c=substr(doc,pos++,1)
                if(c == "\"") return 1
                if(c != "\\") {
                    start=pos-1; n=utf8_value(doc,start)
                    if(n < 32) return 0
                    identity=identity sprintf("%x;",n)
                    decoded=decoded substr(doc,start,consumed); pos+=consumed-1
                    continue
                }
                e=substr(doc,pos++,1)
                if(e == "u") {
                    h=substr(doc,pos,4); n=hex_value(h)
                    if(n < 0) return 0
                    pos+=4
                    if(n >= 55296 && n <= 56319) {
                        if(substr(doc,pos,2) != "\\u") return 0
                        low=hex_value(substr(doc,pos+2,4))
                        if(low < 56320 || low > 57343) return 0
                        n=65536+(n-55296)*1024+low-56320; pos+=6
                    } else if(n >= 56320 && n <= 57343) return 0
                    if(n < 128) decoded=decoded sprintf("%c",n)
                    else decoded=decoded "\\u" h
                } else {
                    if(e == "\"" || e == "\\" || e == "/") n=ordinal[e]
                    else if(e == "b") n=8
                    else if(e == "f") n=12
                    else if(e == "n") n=10
                    else if(e == "r") n=13
                    else if(e == "t") n=9
                    else return 0
                    decoded=decoded sprintf("%c",n)
                }
                identity=identity sprintf("%x;",n)
            }
            return 0
        }
        function array_value(depth,   c) {
            pos++; whitespace()
            if(substr(doc,pos,1) == "]") { pos++; return 1 }
            while(pos <= length(doc)) {
                if(!value(depth+1)) return 0
                whitespace(); c=substr(doc,pos++,1)
                if(c == "]") return 1
                if(c != ",") return 0
                whitespace()
            }
            return 0
        }
        function object_value(depth,   key,key_identity,c,is_string) {
            pos++; whitespace()
            if(substr(doc,pos,1) == "}") {
                if(depth == 0) root_end=pos
                pos++; return 1
            }
            while(pos <= length(doc)) {
                if(!string_value()) return 0
                key=decoded; key_identity=identity; whitespace()
                if(substr(doc,pos++,1) != ":") return 0
                whitespace(); is_string=(substr(doc,pos,1) == "\"")
                if(!value(depth+1)) return 0
                if(depth == 0) {
                    if(key_identity in seen) return 0
                    seen[key_identity]=1; fields++
                    if(key == "event" && (!is_string || decoded == "")) return 0
                }
                whitespace(); c=substr(doc,pos,1)
                if(c == "}") {
                    if(depth == 0) root_end=pos
                    pos++; return 1
                }
                if(c != ",") return 0
                pos++; whitespace()
            }
            return 0
        }
        function value(depth,   c,rest) {
            if(depth > 64) return 0
            whitespace(); c=substr(doc,pos,1)
            if(c == "\"") return string_value()
            if(c == "{") return object_value(depth)
            if(c == "[") return array_value(depth)
            rest=substr(doc,pos)
            if(match(rest,/^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?/)) { pos+=RLENGTH; return 1 }
            if(substr(rest,1,4) == "true" || substr(rest,1,4) == "null") { pos+=4; return 1 }
            if(substr(rest,1,5) == "false") { pos+=5; return 1 }
            return 0
        }
        function escape(s,   out,i,c,n) {
            out=""
            for(i=1;i<=length(s);i++) {
                c=substr(s,i,1)
                if(c == "\\" || c == "\"") out=out "\\" c
                else {
                    n=utf8_value(s,i)
                    if(n < 0) { invalid=1; return "" }
                    if(n < 32) out=out sprintf("\\u%04x",n)
                    else out=out substr(s,i,consumed)
                    i+=consumed-1
                }
            }
            return out
        }
        function inject(key, supplied) {
            if(supplied == "" || ascii_identity(key) in seen) return
            added=added (fields++ ? ", " : "") "\"" key "\": \"" escape(supplied) "\""
        }
        { doc=doc $0 "\n" }
        END {
            pos=1; whitespace()
            if(substr(doc,pos,1) != "{" || !object_value(0)) exit 1
            whitespace(); if(pos <= length(doc)) exit 1
            inject("event",ENVIRON["PRDKIT_JSON_EVENT"])
            inject("operator",ENVIRON["PRDKIT_JSON_OPERATOR"])
            inject("project",ENVIRON["PRDKIT_JSON_PROJECT"])
            if(invalid) exit 1
            line=substr(doc,1,root_end-1) added substr(doc,root_end)  # awk-bytes-ok: root_end is the closing brace, and this program runs under LC_ALL=C
            gsub(/[\r\n]/," ",line); sub(/[[:space:]]+$/,"",line)
            print line
        }
    '
}
