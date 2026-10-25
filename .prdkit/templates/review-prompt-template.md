# Independent review: {CHANGE_ID}

You did not implement this change. Read the change record and the spec sections it touches,
{SPEC_PATHS} (by file and section, never a glob over `specs/`), the constitution's review policy,
then the full change package at {REVIEW_PACKAGE_PATH} for {BASE_SHA}..{HEAD_SHA}. Never judge
from a summary. Your only write is {REPORT_PATH}; do not modify code, the index or HEAD.

Return:

- **Verdict**: PASS or FAIL against the change's spec edits and acceptance, for {HEAD_SHA}.
- **Counts**: `<n> critical, <n> high, <n> medium, <n> low`.
- Findings ranked critical, high, medium, low; each gives where, what, why it matters and the
  evidence. A blocker names the current requirement or correctness or risk obligation and an
  observable consequence. Distinguish a defect, missing evidence this change needs, and an
  optional improvement; later scope is not a blocker.
- **Bounds**: run `.prdkit/scripts/bash/bounds.sh {CHANGE_ID}` and check the diff against every
  item it lists, not only those the record names. A violated constraint or non-goal is high; a
  quality the change weakens without its check is medium.
- Anything not verifiable from the package; never guess a pass.
- Check the upstream intent/design references, dependency outcomes and required PR gates. Reject
  requirements invented from implementation convenience, demoted failing checks, or unknown runtime
  observations presented as PASS. Non-gating observations do not require a cleanup PR. Verify the
  same PR retains useful context, evidence and explicit remaining work before temporary files go.

On re-review, mark earlier findings addressed or not, inspect the behavior the fix affects, and
keep unchanged approvals. For a design judgment between concepts you did not produce, report
compliance per concept, real tradeoffs and a recommendation; difference alone is not a defect.
