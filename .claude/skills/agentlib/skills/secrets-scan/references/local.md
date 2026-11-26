# secrets-scan: local settings (agentblender)

Consumer-owned; library updates never touch this file. Each entry below is reviewed known noise,
not a secret.

## Allowlist

- allow-fingerprint: …7684a44b00f3 # .prdkit/scripts/bash/agent-skills-lib.sh: Product-Kit shell variable, not a credential
- allow-fingerprint: …0e3bf386fa07 # .prdkit/scripts/bash/bounds.sh: Product-Kit shell variable, not a credential
- allow-fingerprint: …b3cc0475bb78 # tests/Abl.Core.Tests/DesktopTransportTests.cs: fake launcher token in unit tests
- allow-fingerprint: …57cd4b484e15 # tests/Abl.Core.Tests/DesktopTransportTests.cs: fake probe token in unit tests
- allow-fingerprint: …ac050cae7843 # tests/Abl.Core.Tests/LifecycleCoreTests.cs: fake status token in unit tests
- allow-fingerprint: …fafe5a555fc6 # tests/Abl.Integration.Tests: fake loopback token in integration tests
