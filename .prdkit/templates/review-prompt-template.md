# Independent review: {CHANGE_ID}

You did not implement this change. Read the change record and the spec sections it touches,
{SPEC_PATHS} (by file and section, never a glob over `specs/`), the constitution's review policy,
then the package index and full change diff at {REVIEW_PACKAGE_PATH} for {BASE_SHA}..{HEAD_SHA}.
Never judge from a summary. Capabilities: {ALLOWED_READ_PATHS}, shell {YES_OR_NO}, network
{YES_OR_NO}, report {REPORT_PATH_OR_RETURN_TEXT}. Use only those capabilities; do not modify code,
the index or HEAD. Missing/inaccessible required input means review pending with the exact gap,
not PASS or an implementation defect. The coordinator persists a returned report verbatim.

Return:

- **Verdict**: PASS or FAIL against the change's spec edits and acceptance, for {HEAD_SHA}.
- **Counts**: `<n> critical, <n> high, <n> medium, <n> low`.
- Findings ranked critical, high, medium, low; each gives where, what, why it matters and the
  evidence. A blocker names the current requirement or correctness or risk obligation and an
  observable consequence. Distinguish a defect, missing evidence this change needs, and an
  optional improvement; later scope is not a blocker.
- **Bounds**: with shell access, run `.prdkit/scripts/bash/bounds.sh {CHANGE_ID}`; otherwise read
  the packaged output and its bound revision/inputs. Check every listed item, not only those the
  record names. Missing/stale bounds output leaves that obligation unverified. A violated
  constraint or non-goal is high; a quality the change weakens without its check is medium.
- Anything not verifiable from the package; never guess a pass.
- Check the upstream intent/design references, dependency outcomes and required PR gates. Reject
  requirements invented from implementation convenience, demoted failing checks, or unknown runtime
  observations presented as PASS. Non-gating observations do not require a cleanup PR. Verify the
  same PR retains useful context, evidence and explicit remaining work before temporary files go.

On re-review, use {SINCE_SHA_OR_INITIAL} and the finding-to-fix/affected-obligation map. Mark earlier
findings addressed or not, inspect the behavior the fix affects and cross-boundary consequences,
and keep unchanged approvals. The full diff remains available for context; do not re-ingest every
prior transcript. State which concern/range the verdict covers. For a design judgment between concepts you did not produce, report
compliance per concept, real tradeoffs and a recommendation; difference alone is not a defect.
