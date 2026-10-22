# Adoption

**Inputs**: repository, owner intent, installed kit.
**Writes**: `.prdkit/memory/constitution.md`, the first `specs/` files.
**Result**: a filled constitution, a current spec, open owner questions.

Run `.prdkit/scripts/bash/doctor.sh`; a 0.x layout needs the cutover ([compact.md](compact.md))
first. Infer stack, commands and present behavior from the repository; ask only about purpose,
authority and real constraints, each with a recommendation. Fill the constitution from its
template without broadening the owner's instructions. Start a small product in `specs/SPEC.md`
(`.prdkit/templates/spec-template.md`), a larger one in `product.md` and `architecture.md`
(`.prdkit/templates/product-template.md`, `.prdkit/templates/architecture-template.md`),
describing what is true now, inferred intent as `hypothesis`. Report what was created, inferred
and still needs a decision.
