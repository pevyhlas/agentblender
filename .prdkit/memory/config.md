# Optional settings

Optional unset values use the defaults. Engineering policy lives in the constitution.

## Configuration

- adoption-manifest-sha256: 2500ce063f0f4c3a07a0419cbe5610a168d7809040b81b4055486cd1e412f333

## Meaning

Product-Kit v3.1.5 (source commit `2ffad1d81364c12d794fff18b7b52f96157c1c9e`) is
vendored in copy mode under `.prdkit/` (see `.prdkit/.manifest`); there is
no kit submodule, so a clone needs no access to the kit source. `.prdkit/scripts/bash/check-adoption.sh`
runs as a CI step and verifies the manifest against `adoption-manifest-sha256` and the installed
command surface. `adoption-pin` is intentionally unset: it checks a tracked kit gitlink, which this
repo does not have. To bump the kit, clone https://github.com/pevyhlas/product-kit at the new
tag into the gitignored `.product-kit/`, run `bash .product-kit/scripts/init.sh --upgrade`, then
update `adoption-manifest-sha256` and the version above in the same commit.

The native review gate (`check-review.sh --pr`) is not configured: it needs trusted
`review-host`, `review-publishers` and `review-owner` entries naming separate reviewer
identities, which is an owner decision. Until then reviewer verdicts are recorded as PR comments.
