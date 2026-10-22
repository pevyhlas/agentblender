# P1 Host — rendering

**Code**: `src/Abl.Core/Rendering/`, `src/Abl.Core/Workers/GpuCapability.cs` · **Decisions**:
D-06, D-17

Shares the local GPU between renders and answers every GPU request honestly (R-13, J-05).

- **P1.B-21 Single-slot GPU lease**: one render on the local GPU at a time; a caller waits for
  the lease or accepts the CPU (`lease=cpu_fallback`), recorded as `device.fallback`. Test:
  `GpuLeaseTests`.
- **P1.B-22 GPU guard**: one predicate decides every `cycles_gpu` path (`render_final`,
  `scene_preview`, `batch_run`, `delivery_export`) before the lease and on desktop bringup; on a
  worker without a backend it fails `gpu_unavailable` (REST 503) with a hint naming only that
  path's parameters, or renders on the CPU under `cpu_fallback`; a worker-raised
  `gpu_unavailable` is retried once on the CPU under `cpu_fallback`. Test: `GpuContractTests`,
  `GpuReplyAndGuardTests`.
- **P1.B-23 Device provenance**: every Cycles result and sidecar carries `device` (backend and
  name, `cpu` for `cycles_cpu`, the fallback when the CPU served); `batch_run` reports the
  batch's device. Test: `GpuContractTests.RenderFinalDeviceParity`,
  `GpuContractTests.BatchRunDeviceParity`.
- **P1.I-07 Bounded GPU replies**: a hello or render reply's GPU fields are validated (bounded,
  control-character-free names); the older `gpu.optix` shape is `worker_upgrade_required`.
  Enforced by: `GpuReplyAndGuardTests`.

## Left open

- GPU-only device selection for OptiX (enable-all today; D-17).
