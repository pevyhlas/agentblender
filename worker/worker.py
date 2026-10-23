"""AgentBlender warm worker — runs INSIDE Blender:

    blender --background --factory-startup --python worker.py -- <socket-path>
    blender --background --factory-startup --python worker.py -- --tcp <host> <port>

Single-threaded blocking request loop over a socket the HOST listens on (the host binds
before spawning; the worker connects — no filesystem polling). The default transport is a
Unix domain socket (local host worker); `--tcp <host> <port>` connects over TCP to a
launcher on the desktop tier (feature 003) — Windows CPython has no AF_UNIX, so remote
workers must use TCP. The blocking loop IS Blender's main thread: no timers, no threads,
one in-flight request. Framing: 4-byte big-endian length + UTF-8 JSON, both directions
(docs/worker-protocol.md). The host enforces per-op timeouts and kills on breach;
checkpoints make workers disposable.

GPL boundary: worker/ is the only directory that imports bpy (D-10).
"""

import base64
import json
import os
import socket
import struct
import sys
import tempfile
import time
import traceback
import hashlib

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fingerprint  # noqa: E402
import sheet  # noqa: E402
import blobs  # noqa: E402
import inspection  # noqa: E402
import cycles_devices  # noqa: E402

ENGINES = {
    "eevee": "BLENDER_EEVEE",
    "cycles_gpu": "CYCLES",
    "cycles_cpu": "CYCLES",
    "workbench": "BLENDER_WORKBENCH",
}

TRACEBACK_LIMIT = 4000
MAX_FRAME_BYTES = 64 * 1024 * 1024

journal = fingerprint.Journal()

# Temp files a remote batch (T005b) retains across ops — the shipped base `.blend` and each
# kept variant PNG. They live in the shared temp dir, so nothing else reaps them; the worker
# deletes them itself when the connection ends (main's finally). A hard kill (host timeout →
# launcher SIGKILL) can still orphan them — the OS temp policy is the backstop for that path.
_kept_temps = []
transfers = blobs.Transfers(_kept_temps)


def _reap_kept_temps():
    while _kept_temps:
        path = _kept_temps.pop()
        try:
            os.remove(path)
        except OSError:
            pass


class OpError(Exception):
    """A structured op failure. `abl_kind` becomes the host error kind (e.g.
    "bad_request" for caller mistakes) instead of the raw Python exception name."""

    def __init__(self, kind, message):
        super().__init__(message)
        self.abl_kind = kind


def _cycles_prefs():
    return bpy.context.preferences.addons["cycles"].preferences


def _enable_gpu(scene):
    """Select and check the GPU devices for one cycles_gpu render (feature 009, plan D4).

    Module-level on purpose: the 006 fault-injection test replaces `__main__._enable_gpu`.
    Returns the configured `{backend, name}` device for the render reply."""
    prefs = _cycles_prefs()
    backend = cycles_devices.detect(prefs)["backend"]
    try:
        cycles_devices.select(prefs, scene.cycles, backend)
        return cycles_devices.check(prefs, backend)
    except cycles_devices.GpuUnavailable as exc:
        raise OpError("gpu_unavailable", str(exc)) from None


def op_hello(req):
    try:
        prefs = _cycles_prefs()
    except (KeyError, AttributeError):
        prefs = None  # no Cycles add-on: no GPU backend
    # Any detection fault fails hello on purpose (fail loud): the worker never advertises
    # a GPU capability it could not read. `detect` already absorbs a backend this build
    # does not know.
    gpu = cycles_devices.none() if prefs is None else cycles_devices.detect(prefs)
    source = hashlib.sha256(b"abl.worker-source-observation.v1\n")
    modules = ("worker.py", "fingerprint.py", "sheet.py", "blobs.py", "cycles_devices.py")
    if "inspection" in globals():
        modules += ("inspection.py",)
    for name in modules:
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), name)
        with open(path, "rb") as script:
            data = script.read(1024 * 1024 + 1)
        if len(data) > 1024 * 1024:
            raise OpError("bad_request", "worker source observation exceeds file bound")
        source.update(name.encode("ascii") + b"\n" + hashlib.sha256(data).digest())
    return {
        "blender_version": bpy.app.version_string,
        "blender_build_hash": bpy.app.build_hash.decode("ascii"),
        "worker_source_sha256": source.hexdigest(),
        "python_version": sys.version.split()[0],
        "gpu": gpu,
        "capabilities": {"blob_chunks": True},
    }


def op_warm(req):
    started = time.monotonic()
    if req.get("prewarm_render", True):
        scene = bpy.context.scene
        saved = (scene.render.engine, scene.render.resolution_x, scene.render.resolution_y,
                 scene.render.resolution_percentage, scene.render.filepath,
                 scene.eevee.taa_render_samples)
        try:
            scene.render.engine = ENGINES["eevee"]
            scene.render.resolution_x = 64
            scene.render.resolution_y = 64
            scene.render.resolution_percentage = 100
            scene.render.filepath = os.path.join(tempfile.gettempdir(), f"abl-prewarm-{os.getpid()}.png")
            # One sample compiles and binds the same shaders as the default 64; the other 63
            # only repeat the pipeline, which costs seconds on a software renderer (llvmpipe).
            scene.eevee.taa_render_samples = 1
            bpy.ops.render.render(write_still=True)
        finally:
            # Restored whether or not the render succeeds, as op_render restores its overrides.
            (scene.render.engine, scene.render.resolution_x, scene.render.resolution_y,
             scene.render.resolution_percentage, scene.render.filepath,
             scene.eevee.taa_render_samples) = saved
    journal.reset()
    return {"warmed_ms": round((time.monotonic() - started) * 1000, 1)}


def op_exec(req):
    started = time.monotonic()
    # exec-by-source (feature 003 / T005b): a remote worker cannot read the host's tmp
    # script file, so the host ships the source inline as "source". A local worker keeps
    # passing "script" (a path) — either way the compiled name is the script's identity.
    source = req.get("source")
    script_path = req.get("script")
    try:
        if source is not None:
            code = compile(source, "<abl-exec>", "exec")
            script_name = "<abl-exec>"
        else:
            with open(script_path, encoding="utf-8") as f:
                code = compile(f.read(), script_path, "exec")
            script_name = script_path
        namespace = {
            "__name__": "__abl_exec__",
            "__file__": script_name,
            "params": req.get("params") or {},
        }
        exec(code, namespace)  # noqa: S102 — executing caller scripts IS the product
    except Exception:
        # FR-003 atomicity: restore the pre-op state before reporting the failure.
        recover_to = req.get("recover_to")
        if recover_to:
            try:
                bpy.ops.wm.open_mainfile(filepath=recover_to)
                journal.reset()
            except Exception:
                # Recovery itself failed: "state is unchanged" would be a lie. Die so the
                # host reports worker_died + lastCheckpoint (the honest recovery path).
                traceback.print_exc()
                os._exit(70)
        raise
    return {
        "state_delta": journal.advance(),
        "duration_ms": round((time.monotonic() - started) * 1000, 1),
    }


def op_state(req):
    if req.get("mode", "delta") == "full":
        state = fingerprint.full()
        journal.reset()
        return {"state": state}
    return {"state": journal.advance()}


def op_save(req):
    path = req["path"]
    bpy.ops.wm.save_as_mainfile(filepath=path, copy=True, compress=True)
    # digest = the canonical scene content hash; the replay gate compares digests,
    # never .blend bytes (which embed process memory layout). blender_version rides
    # along so checkpoints/frozen artifacts record what produced them (FR-007, 002).
    return {
        "bytes": os.path.getsize(path),
        "digest": fingerprint.digest(),
        "blender_version": bpy.app.version_string,
    }


def op_load(req):
    bpy.ops.wm.open_mainfile(filepath=req["path"])
    journal.reset()
    return {}


def op_load_bytes(req):
    """Load a checkpoint shipped as base64 bytes over the connection (feature 003): a
    remote worker cannot read the host's store filesystem, so the host sends the .blend.

    With "keep" (T005b), the .blend is retained on the worker's disk and its path returned,
    so a batch can reset to the base before each variant with a cheap local `load` instead
    of re-shipping the bytes N times."""
    data = base64.b64decode(req["blend_b64"])
    keep = bool(req.get("keep"))
    path = os.path.join(tempfile.gettempdir(), f"abl-load-{os.getpid()}.blend")
    with open(path, "wb") as f:
        f.write(data)
    if keep:
        _kept_temps.append(path)  # own it even if Blender rejects the load
    try:
        bpy.ops.wm.open_mainfile(filepath=path)
        journal.reset()
    finally:
        if not keep:
            try:
                os.remove(path)
            except OSError:
                pass
    if keep:
        return {"path": path}
    return {}


def _capture_render_settings(scene, engine):
    # Restore the engine/format before dependent enum values (e.g. EXR's 32-bit
    # depth is coerced when selecting PNG). Capture before the first override.
    properties = [
        (scene.render, ("engine", "resolution_x", "resolution_y",
                        "resolution_percentage", "filepath"), False),
        (scene.render.image_settings, ("file_format", "color_mode", "color_depth"), False),
        (scene.render, ("film_transparent", "use_border", "use_crop_to_border", "pixel_aspect_x", "pixel_aspect_y", "dither_intensity"), False),
        (scene.display_settings, ("display_device",), True),
        (scene.view_settings, ("view_transform", "look", "exposure", "gamma", "use_curve_mapping"), True),
        (scene.render.image_settings, ("color_management",), True),
    ]
    if engine in ("cycles_gpu", "cycles_cpu"):
        properties.append((scene.cycles, ("device", "samples", "seed", "denoiser"), True))
        if hasattr(scene.cycles, "denoising_use_gpu"):
            properties.append((scene.cycles, ("denoising_use_gpu",), True))
    elif engine == "eevee":
        properties.append((scene.eevee, ("taa_render_samples",), False))
    settings = [
        (owner, name, getattr(owner, name), not preserve_unset or owner.is_property_set(name))
        for owner, names, preserve_unset in properties for name in names
    ]
    gpu = None
    if engine == "cycles_gpu":
        prefs = bpy.context.preferences.addons["cycles"].preferences
        gpu = (prefs, prefs.compute_device_type,
               {(device.id, device.type): device.use for device in prefs.devices})
    return settings, gpu


def _restore_render_settings(saved):
    settings, gpu = saved
    for owner, name, value, was_set in settings:
        if was_set:
            setattr(owner, name, value)
        else:
            # An unset Cycles enum can read empty in background mode. Restoring
            # its unset state avoids assigning an invalid empty enum identifier.
            owner.property_unset(name)
    if gpu is not None:
        prefs, compute_device_type, enabled = gpu
        prefs.compute_device_type = compute_device_type
        for device in prefs.devices:
            # Device enumeration may populate new capability-cache entries. They
            # were not selected before this call, so do not leave them enabled.
            device.use = enabled.get((device.id, device.type), False)


def op_render(req):
    started = time.monotonic()
    engine = req["engine"]
    if engine not in ENGINES:
        raise OpError(
            "bad_request",
            f"unknown engine '{engine}'; valid: {', '.join(sorted(ENGINES))}")
    scene = bpy.context.scene
    saved = _capture_render_settings(scene, engine)
    try:
        return _render(req, scene, engine, started)
    finally:
        try:
            _restore_render_settings(saved)
        except Exception:
            # An ordinary op error would leave the host advertising a ready
            # session with uncertain state. Match exec's fatal recovery contract.
            try:
                traceback.print_exc()
            finally:
                # Caller scripts can replace/close stderr. Diagnostics must never
                # turn failed restoration into a recoverable request error.
                os._exit(70)


def _render(req, scene, engine, started):
    scene.render.engine = ENGINES[engine]

    res = req.get("res", 512)
    res_x, res_y = (res if isinstance(res, (list, tuple)) else (res, res))
    scene.render.resolution_x = int(res_x)
    scene.render.resolution_y = int(res_y)
    scene.render.resolution_percentage = 100

    samples = req.get("samples")
    is_cycles = engine in ("cycles_gpu", "cycles_cpu")
    device = None
    if engine == "cycles_gpu":
        # Devices, scene.cycles.device and the denoiser come from the detected backend; a
        # rejected value raises gpu_unavailable and is never swallowed (plan D5).
        device = _enable_gpu(scene)
    elif engine == "cycles_cpu":
        scene.cycles.device = "CPU"
        device = {"backend": "cpu", "name": None}
        try:
            # 5.2 gotcha: the denoiser enum can read empty in --background.
            scene.cycles.denoiser = "OPENIMAGEDENOISE"
        except Exception:
            pass
    if is_cycles:
        if samples:
            scene.cycles.samples = int(samples)
        scene.cycles.seed = int(req.get("seed", 0))
    elif engine == "eevee" and samples:
        scene.eevee.taa_render_samples = int(samples)

    # return_bytes (feature 003): a remote worker renders to its own temp and ships the
    # PNG bytes back — the host cannot see the worker's filesystem, and pixels must land
    # on the host's disk. Local workers keep writing straight to the host-provided path.
    # A UNIQUE temp per call so batch variants (T005b) coexist on the worker's disk long
    # enough for a remote contact sheet; "keep" retains the file for that composition and
    # registers it for reaping when the connection ends (main's finally).
    return_bytes = bool(req.get("return_bytes"))
    return_blob = bool(req.get("return_blob"))
    keep = bool(req.get("keep"))
    if return_bytes or return_blob:
        fd, out = tempfile.mkstemp(suffix=".png", prefix=f"abl-render-{os.getpid()}-")
        os.close(fd)
        _kept_temps.append(out)  # own the path before render/description can fail
    else:
        out = req["out"]
    scene.render.filepath = out
    scene.render.image_settings.file_format = "PNG"

    # Delivery profile (feature 004 / FR-001): emit an AGD Mode-A still — 8-bit RGBA, sRGB,
    # straight (non-premultiplied) alpha over a transparent film. Host-side verify confirms
    # the byte-visible parts (RGBA / 8-bit); colour space + alpha are DECLARED in the
    # descriptor because they are set here, not recoverable from PNG bytes alone.
    if req.get("profile"):
        img = scene.render.image_settings
        img.color_mode = "RGBA"
        img.color_depth = "8"
        # Fail closed; no warning-and-continue interpretation claims.
        img.color_management = "FOLLOW_SCENE"
        scene.display_settings.display_device = "sRGB"
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
        scene.view_settings.exposure = 0
        scene.view_settings.gamma = 1
        scene.view_settings.use_curve_mapping = False
        scene.render.film_transparent = True
        scene.render.use_border = False
        scene.render.use_crop_to_border = False
        scene.render.pixel_aspect_x = 1
        scene.render.pixel_aspect_y = 1
        scene.render.dither_intensity = 0

    bpy.ops.render.render(write_still=True)
    result = {"duration_ms": round((time.monotonic() - started) * 1000, 1), "out": out}
    if device is not None:
        # Configured and checked before the render (plan D4), outside `settings`.
        result["device"] = device
    if req.get("profile"):
        result["settings"] = {
            "engine": scene.render.engine, "width": scene.render.resolution_x, "height": scene.render.resolution_y,
            "percentage": scene.render.resolution_percentage, "format": img.file_format, "mode": img.color_mode,
            "depth": img.color_depth, "display": scene.display_settings.display_device,
            "view": scene.view_settings.view_transform, "look": scene.view_settings.look,
            "exposure": scene.view_settings.exposure, "gamma": scene.view_settings.gamma,
            "transparent": scene.render.film_transparent, "border": scene.render.use_border,
            "crop": scene.render.use_crop_to_border, "colorManagement": img.color_management,
            "pixelAspect": [scene.render.pixel_aspect_x, scene.render.pixel_aspect_y],
            "dither": scene.render.dither_intensity,
            "samples": scene.cycles.samples if engine.startswith("cycles") else scene.eevee.taa_render_samples,
            "seed": scene.cycles.seed if engine.startswith("cycles") else req.get("seed", 0),
        }
    if return_blob:
        result["blob"] = transfers.describe(out, keep)
    elif return_bytes:
        try:
            with open(out, "rb") as f:
                result["png_b64"] = base64.b64encode(f.read()).decode("ascii")
        finally:
            if keep:
                pass  # already connection-owned
            else:
                try:
                    os.remove(out)
                except OSError:
                    pass
    return result


def op_contact_sheet(req):
    # return_bytes (T005b): a remote batch composes the sheet from the worker's own
    # variant PNGs and ships the finished sheet back — the host never sees those paths.
    return_bytes = bool(req.get("return_bytes"))
    return_blob = bool(req.get("return_blob"))
    if return_bytes or return_blob:
        fd, out_path = tempfile.mkstemp(suffix=".png", prefix=f"abl-sheet-{os.getpid()}-")
        os.close(fd)
        _kept_temps.append(out_path)  # own it before composition/description
    else:
        out_path = req["out"]
    out = sheet.compose(
        req["inputs"], req.get("labels", []), req.get("columns", 3), req.get("cell", 256), out_path
    )
    result = {"out": out}
    if return_blob:
        result["blob"] = transfers.describe(out)
    elif return_bytes:
        try:
            with open(out, "rb") as f:
                result["png_b64"] = base64.b64encode(f.read()).decode("ascii")
        finally:
            try:
                os.remove(out)
            except OSError:
                pass
    return result


def op_quit(req):
    return {}


HANDLERS = {
    "hello": op_hello,
    "warm": op_warm,
    "exec": op_exec,
    "state": op_state,
    "inspect": inspection.inspect,
    "save": op_save,
    "load": op_load,
    "load_bytes": op_load_bytes,
    "render": op_render,
    "contact_sheet": op_contact_sheet,
    "blob_put_begin": transfers.put_begin,
    "blob_put_chunk": transfers.put_chunk,
    "blob_put_commit": lambda req: transfers.put_commit(
        req, lambda path: op_load({"path": path})),
    "blob_get_chunk": transfers.get_chunk,
    "blob_get_finish": transfers.get_finish,
    "blob_abort": transfers.abort,
    "quit": op_quit,
}


def recv_exact(conn, count):
    data = bytearray()
    while len(data) < count:
        chunk = conn.recv(count - len(data))
        if not chunk:
            return None
        data.extend(chunk)
    return bytes(data)


def send_message(conn, payload):
    encoded = json.dumps(payload).encode("utf-8")
    conn.sendall(struct.pack(">I", len(encoded)) + encoded)


def _connect(args):
    """Connect back to the host's listener. Default: AF_UNIX at args[0]. TCP (feature
    003, and the only option on Windows): `--tcp <host> <port>`."""
    if args and args[0] == "--tcp":
        conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        target = (args[1], int(args[2]))
    else:
        conn = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        target = args[0]
    deadline = time.monotonic() + 30
    while True:
        try:
            conn.connect(target)
            return conn
        except OSError:
            if time.monotonic() > deadline:
                raise
            time.sleep(0.05)


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    conn = _connect(args)

    try:
        while True:
            header = recv_exact(conn, 4)
            if header is None:
                break  # host went away; nothing left to serve
            (length,) = struct.unpack(">I", header)
            if length > MAX_FRAME_BYTES:
                break  # bound control messages before allocating; files use chunks
            raw = recv_exact(conn, length)
            if raw is None:
                break

            request = json.loads(raw.decode("utf-8"))
            op = request.get("op")
            try:
                handler = HANDLERS[op]
                response = {"id": request.get("id"), "ok": True, **handler(request)}
            except Exception as exc:
                response = {
                    "id": request.get("id"),
                    "ok": False,
                    "error": {
                        "kind": getattr(exc, "abl_kind", None) or type(exc).__name__,
                        "message": str(exc),
                        # A deliberate structured error (OpError) is a caller mistake, not a
                        # worker fault — no traceback. Keep it for unexpected exceptions.
                        "traceback": None if isinstance(exc, (OpError, blobs.BlobError, inspection.InspectionError))
                        else traceback.format_exc()[-TRACEBACK_LIMIT:],
                    },
                }
            send_message(conn, response)
            if op == "quit" and response["ok"]:
                break
    finally:
        # Reap any batch temps we retained (T005b). Graceful close (host disposed the worker →
        # recv returns None) always lands here; a SIGKILL does not, so the OS temp policy is the
        # backstop for that path.
        transfers.close()
        _reap_kept_temps()
        conn.close()


if __name__ == "__main__":
    main()
