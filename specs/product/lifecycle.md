# AgentBlender — lifecycle

**Now**: Public preview (pending confirmation) · **Next**: GA

<!-- Stages, in order: Idea, Demo, MVP, Private preview, Public preview, GA, Sunset, Retired; skip
those the product does not need. Only the owner moves Now, when the exit criteria pass. An item
needed only from a later stage carries `from: <stage>` with its maturity. Each stage's
Environment names one from architecture/environments.md. -->

```mermaid
flowchart LR
  A[MVP] --> B["Public preview (now)"] --> C[GA]
```

The owner set the project's status to experimental and publishes it as an open-source
repository; Public preview is the matching stage, pending the owner's confirmation
([inbox](../inbox.md)). The MVP stage (the owner's own use on real project data) is behind it.

## Public preview (now)

- **Goal**: anyone can run AgentBlender on their own host and agents use it for real asset work;
  learn whether the bet holds once setup friction is gone.
- **Audience**: anyone who self-hosts it, the owner and pilot projects.
- **Environment**: Local, the user's own host; the optional Desktop GPU worker; real project
  data that never leaves the host except by export.
- **Promise**: experimental, interfaces may change: the op surface, configuration, store,
  bundle and delivery formats may break in any 0.x release, without migration; the CHANGELOG
  says how to upgrade. Security fixes for the latest release only, best effort. Q-01 to Q-04
  are enforced where a test names them.
- **Exit when**: the deferred hardening the owner requires before GA is built (durable job
  queue, format stability, endpoint authentication with `render_final` output confinement,
  code-exec sandboxing; timing is an inbox question); the macOS qualification passes; and agents
  use AgentBlender beyond smoke previews. The owner decides.

## GA

- **Goal**: a supported asset pipeline that projects can depend on.
- **Audience**: anyone.
- **Environment**: Local and the optional Desktop GPU worker.
- **Promise**: SemVer; store, bundle and delivery formats change only with a migration; the
  quality bands are enforced; the op surface keeps its compatibility promise.
- **Exit when**: the owner decides to sunset it.
