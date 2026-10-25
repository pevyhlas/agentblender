---
description: Turn a request into a change, continue an open change, or answer from the spec.
---

# Product-Kit

## Input

```text
$ARGUMENTS
```

Read `.prdkit/workflows/protocol.md`, then route the request: owner direction goes to
`specs/inbox.md` and into a change or the spec; bounded work opens or continues a change
(`.prdkit/workflows/change.md`); product-to-feature decomposition uses
`.prdkit/workflows/planning.md`, ready work uses `.prdkit/workflows/implementation.md`;
questions are answered from the spec and code; overflow, drift
or a 0.x layout goes to `.prdkit/workflows/compact.md`. Every spec edit follows
`.prdkit/workflows/spec.md`. `--explore` means no edits, `--plan` a change record and proportional
execution plan without code,
`--execute` the bounded change; `continue` resumes from the change's State after checking
actual repository state.
