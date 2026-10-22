<!--
Open this pull request as a draft. Only the maintainer merges, and review is best effort.
For anything beyond a small fix, open an issue first and agree on the change there.
-->

## What and why

Closes #

## Checklist

- [ ] Opened as a draft, and linked to its issue above (a small fix may skip the issue)
- [ ] One concern; user-visible changes noted at the top of `CHANGELOG.md`; versions left alone
- [ ] `dotnet build agentblender.slnx -warnaserror` passes
- [ ] The unit lane passes: `dotnet test agentblender.slnx --no-build --filter "Category!=BlenderIntegration&Category!=MetalQualification&Category!=OptixQualification"`
- [ ] The integration lane passes with Blender 5.2.1, or CI ran it: `dotnet test agentblender.slnx --no-build --filter "Category=BlenderIntegration&Category!=OptixQualification"`
- [ ] `python3 tests/test_blob_transfers.py`, `python3 tests/test_cycles_devices.py` and `bash scripts/check-ci-policy.sh` pass
- [ ] `git diff --check 4b825dc642cb6eb9a060e54bf8d69288fbee4904 HEAD` prints nothing
- [ ] `bpy` and other Blender imports stay under `worker/`; any new dependency is MIT-compatible and passes `dotnet list agentblender.slnx package --vulnerable --include-transitive`
- [ ] No secrets, tokens, host names, private-network addresses or private paths in code, docs or tests
