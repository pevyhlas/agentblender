# Optional flow configuration

Unset values and absent files use the ordinary protocol. These toggles do not grant
authority or weaken verification. Engineering policy lives in the constitution;
external event delivery lives in task-ledger.md.

## Configuration

- retrospective: off
- agent-optimization: off
- adoption-manifest-sha256: c26db47e07d676dfd428a75fe3cb7c84a3f428aec3b14998d071aac79e5c635e

## Meaning

`retrospective: on` requests a delivery retrospective after confirmed integration using
the local ledger. Missing optional enrichment leaves usage unknown. Failure is advisory.

`agent-optimization: on` permits a suggestion to review capability/model effectiveness
after learning. Applying proposed configuration changes still needs applicable authority.

Product-Kit v1.2.1 (source commit `589589aeae2791d7e509214b9fb82b014bd59f82`) is
vendored in copy mode under `.prdkit/` (see `.prdkit/.manifest`); there is
no kit submodule, so a clone needs no access to the kit source. `.prdkit/scripts/bash/check-adoption.sh`
runs as a CI step and verifies the manifest against `adoption-manifest-sha256` and the installed
command surface. `adoption-pin` is intentionally unset: it checks a tracked kit gitlink, which this
repo does not have. To bump the kit, clone https://github.com/pevyhlas/product-kit at the new
tag into the gitignored `.product-kit/`, run `bash .product-kit/scripts/init.sh --upgrade`, then
update `adoption-manifest-sha256` and the version above in the same commit.
