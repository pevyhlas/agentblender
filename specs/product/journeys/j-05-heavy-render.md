# J-05 Heavy render (hypothesis)

**Who**: asset agent · **Area**: Render · **Surfaces**: MCP tools or the `abl` CLI

## Intent

Get a final render or a batch at full quality without blocking interactive design work on the
host's small GPU.

## Situation

The host serves interactive sessions with a single-slot GPU lease. An optional desktop GPU
worker on the private network may be on or off; the agent does not know which.

## Scenario

1. The agent asks for a final render or a batch.
2. With a desktop configured and reachable, the work runs there; the base checkpoint ships in,
   the pixels come back to the host's disk.
3. The result names the serving tier and the device that was configured and checked.

## Variations

- **Desktop unreachable, wrong token, timed out or needing an upgrade**: the job runs locally
  and the result carries a bounded `fallbackReason`.
- **No desktop configured**: everything runs locally, exactly as without the tier.
- **The local GPU is busy**: the caller waits for the lease or accepts the CPU
  (`lease=cpu_fallback`), and the result records the fallback.
- **A host without a GPU backend**: `cycles_gpu` is refused with `gpu_unavailable` and a hint,
  never silently rendered on the CPU (R-13).
- **An Apple silicon host**: Cycles renders on Metal locally; the first render compiles kernels
  for about a minute, and the desktop tier is unsupported there (C-12).

## Success

Heavy work never blocks iteration and never silently loses a job, and the serving tier and
device are recorded (R-07, R-13). For the business: full-quality finals without a bigger
iteration host.
