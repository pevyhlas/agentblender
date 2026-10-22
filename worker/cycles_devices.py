"""Cycles GPU backend detection, selection and the pre-render device check (feature 009).

Runs inside Blender but never imports bpy: the worker passes in the Cycles add-on
preferences (`prefs`) and `scene.cycles` (`cycles`), so CPython unit tests can drive this
module with fake objects (tests/test_cycles_devices.py). The name deliberately avoids a
`gpu` prefix, which the CI GPL-boundary grep reserves for Blender's own module.

- `detect` is hello-time and read-only: it assigns no preference property. Blender's
  device enumeration may add entries to `prefs.devices`; entries that existed before
  keep their values.
- `select` configures one `cycles_gpu` render: Metal enables only METAL devices; OptiX
  keeps the historical enable-all behaviour.
- `check` is the precondition immediately before that render; it proves the
  configuration, not the execution.
"""

BACKENDS = ("OPTIX", "METAL")
MAX_DEVICES = 8
MAX_NAME = 128
METALRT = ("OFF", "ON", "AUTO")
_REPLACEMENT = "�"


class GpuUnavailable(Exception):
    """No usable GPU device for `cycles_gpu`; the worker reports kind `gpu_unavailable`."""


def clean_name(name):
    """Replace C0/C1 control characters and bound the length (reply hygiene).

    The bound counts UTF-16 code units, the unit of the host's `Text` length checks, so a
    name with characters outside the BMP never passes here and fails host validation. A
    surrogate pair is never split."""
    kept = []
    units = 0
    for ch in str(name):
        if ord(ch) < 0x20 or 0x7F <= ord(ch) <= 0x9F:
            ch = _REPLACEMENT
        width = 2 if ord(ch) > 0xFFFF else 1
        if units + width > MAX_NAME:
            break
        kept.append(ch)
        units += width
    return "".join(kept)


def none():
    return {"backend": "none", "devices": [], "metalrt": None}


def detect(prefs):
    """Return `{backend, devices, metalrt}` for the first backend with a device of its type.

    Probe order is OPTIX, then METAL. The CPU entry that `get_devices_for_type` appends is
    never counted. No preference property is assigned."""
    for kind in BACKENDS:
        try:
            entries = prefs.get_devices_for_type(kind)
        except Exception:
            # A backend this Blender build or platform does not know is simply absent.
            continue
        devices = [entry for entry in entries if entry.type == kind]
        if devices:
            return {
                "backend": kind.lower(),
                "devices": [{"name": clean_name(d.name), "type": kind} for d in devices[:MAX_DEVICES]],
                "metalrt": _metalrt(prefs) if kind == "METAL" else None,
            }
    return none()


def _metalrt(prefs):
    value = getattr(prefs, "metalrt", None)
    return value if value in METALRT else None


def _kind(backend):
    kind = str(backend).upper()
    if kind not in BACKENDS:
        raise GpuUnavailable(f"cycles_gpu: no GPU backend available (backend={backend})")
    return kind


def _assign(owner, name, value):
    try:
        setattr(owner, name, value)
    except (TypeError, ValueError, AttributeError) as exc:
        raise GpuUnavailable(f"cycles_gpu: {name}={value!r} was rejected ({exc})") from None


def select(prefs, cycles, backend):
    """Configure preferences and `scene.cycles` for one `cycles_gpu` render on `backend`."""
    kind = _kind(backend)
    _assign(prefs, "compute_device_type", kind)
    for entry in prefs.get_devices_for_type(kind):
        # OptiX: enable every returned entry, the CPU included (unchanged behaviour, plan
        # D3). Metal: only the METAL devices; the CPU entry is switched off.
        entry.use = True if kind == "OPTIX" else entry.type == kind
    _assign(cycles, "device", "GPU")
    if kind == "OPTIX":
        _assign(cycles, "denoiser", "OPTIX")
    else:
        _assign(cycles, "denoiser", "OPENIMAGEDENOISE")
        if not hasattr(cycles, "denoising_use_gpu"):
            raise GpuUnavailable("cycles_gpu: scene.cycles.denoising_use_gpu is not available")
        _assign(cycles, "denoising_use_gpu", True)


def check(prefs, backend):
    """The pre-render precondition; returns the configured `{backend, name}` device."""
    kind = _kind(backend)
    if prefs.compute_device_type != kind:
        raise GpuUnavailable(
            f"cycles_gpu: compute_device_type is {prefs.compute_device_type!r}, expected {kind}")
    # The set `select` configured: the backend's current list, not every entry in
    # `prefs.devices`, so a stale or script-added entry is never the reported device.
    enabled = [d for d in prefs.get_devices_for_type(kind) if d.use and d.type == kind]
    has_active_device = getattr(prefs, "has_active_device", None)
    if not callable(has_active_device):
        raise GpuUnavailable("cycles_gpu: preferences lack has_active_device")
    active = bool(has_active_device())
    if not enabled or not active:
        raise GpuUnavailable(
            f"cycles_gpu: no enabled {kind} device (has_active_device={str(active).lower()})")
    return {"backend": kind.lower(), "name": clean_name(enabled[0].name)}
