# AgentBlender — data

| Data | Owner part | Lives in | Kept for | Removed by |
|------|------------|----------|----------|------------|
| Checkpoints, tags, journals, frozen artifacts | P4 | the store root (plain files, content-addressed) | until pruned; disposable | `checkpoint_prune` (P4.I-03 protects heads, frozen references and tags) |
| Live session heads | P1 (sessions) | daemon memory, pointing into the store | the daemon's life | session close or daemon stop |
| Renders and contact sheets | P1 | the renders directory, unique path per invocation | until the user removes them; regenerable | the user |
| Bundles | P4 | the project repository | the repository's history | the user, in git |
| Delivery generations | P1 (delivery) | the delivery root, immutable and never replaced | the user's choice | the user |
| Configuration and token file | P1 (operability) | the per-user config directory, private | until changed | `abl setup --reset`, the user |
| Daemon state, lock and logs | P1 (operability) | the store (lock, instance record) and the state directory (log) | the daemon's life; logs until removed | daemon stop, the user |

While experimental, the store, bundle and delivery formats may break without migration
(lifecycle Promise); a breaking bundle change bumps `formatVersion` and import refuses unknown
versions.
