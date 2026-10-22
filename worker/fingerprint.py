"""Scene fingerprint journal — drives `state` delta reads and exec's returned delta.

Fingerprint per object: type, parent, rounded world transform, mesh vert/face counts,
material names; plus the collection and material name sets. Diff output scales with the
change, never the scene (FR-002/AC-2).
"""

import bpy


def _object_fp(obj):
    xform = tuple(round(v, 4) for row in obj.matrix_world for v in row)
    verts = faces = 0
    if obj.type == "MESH":
        verts = len(obj.data.vertices)
        faces = len(obj.data.polygons)
    mats = getattr(obj.data, "materials", None)
    return {
        "type": obj.type,
        "parent": obj.parent.name if obj.parent else None,
        "xform": xform,
        "verts": verts,
        "faces": faces,
        "materials": [m.name for m in mats if m] if mats is not None else [],
    }


def snapshot():
    return {
        "objects": {o.name: _object_fp(o) for o in bpy.data.objects},
        "collections": sorted(c.name for c in bpy.data.collections),
        "materials": sorted(m.name for m in bpy.data.materials if m.users > 0),
    }


def diff(old, new):
    old_objects, new_objects = old["objects"], new["objects"]
    delta = {
        "added": sorted(n for n in new_objects if n not in old_objects),
        "removed": sorted(n for n in old_objects if n not in new_objects),
        "changed": sorted(
            n for n, fp in new_objects.items() if n in old_objects and old_objects[n] != fp
        ),
    }
    for key in ("collections", "materials"):
        if old[key] != new[key]:
            old_set, new_set = set(old[key]), set(new[key])
            delta[key] = {
                "added": sorted(new_set - old_set),
                "removed": sorted(old_set - new_set),
            }
    return delta


def full():
    """Terse full inventory (the `state mode=full` payload) — no transforms."""
    snap = snapshot()
    return {
        "objects": {
            name: {k: fp[k] for k in ("type", "parent", "verts", "faces", "materials")}
            for name, fp in snap["objects"].items()
        },
        "collections": snap["collections"],
        "materials": snap["materials"],
    }


def digest():
    """Canonical scene digest — the deterministic-replay gate (R-03).

    .blend bytes embed process memory layout (new datablocks make saves differ across
    processes even for identical op sequences — measured), so the replay gate hashes the
    scene CONTENT instead.

    The bound, precisely (mirrored in specs/parts/p4-store-replay.md, P4.I-01): COVERED — object set, hierarchy,
    settled world transforms, mesh vertex coords + loop topology, light/camera params,
    used materials (name + viewport color), modifier stacks (name + type), visibility
    flags, collection names. NOT covered — material/geometry node trees, modifier
    parameter values, custom properties, and render settings (pool pre-warm touches
    those). Extending the bound is a surface change: replay PASS means equality WITHIN
    this bound.
    """
    import hashlib
    import json

    import numpy as np

    # matrix_world is depsgraph-dependent: stale values differ between an incrementally
    # built scene and a freshly loaded one. Evaluate first so transforms are settled.
    bpy.context.view_layer.update()

    h = hashlib.sha256()

    def feed(tag, payload):
        h.update(tag.encode())
        h.update(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode())

    for obj in sorted(bpy.data.objects, key=lambda o: o.name):
        feed("object", {
            "name": obj.name,
            "type": obj.type,
            "parent": obj.parent.name if obj.parent else None,
            "xform": [round(v, 5) for row in obj.matrix_world for v in row],
            "materials": [m.name for m in obj.data.materials if m]
            if getattr(obj.data, "materials", None) is not None else [],
            "modifiers": [[m.name, m.type] for m in getattr(obj, "modifiers", [])],
            "hide": [obj.hide_viewport, obj.hide_render],
        })
        if obj.type == "MESH":
            mesh = obj.data
            coords = np.empty(len(mesh.vertices) * 3, dtype=np.float64)
            mesh.vertices.foreach_get("co", coords)
            h.update(np.round(coords, 5).astype(np.float64).tobytes())
            loops = np.empty(len(mesh.loops), dtype=np.int64)
            mesh.loops.foreach_get("vertex_index", loops)
            h.update(loops.tobytes())
        elif obj.type == "LIGHT":
            light = obj.data
            feed("light", {
                "kind": light.type,
                "energy": round(light.energy, 4),
                "color": [round(c, 5) for c in light.color],
            })
        elif obj.type == "CAMERA":
            feed("camera", {"lens": round(obj.data.lens, 4)})

    for mat in sorted(bpy.data.materials, key=lambda m: m.name):
        if mat.users == 0:
            continue  # zero-user datablocks (e.g. the built-in "Dots Stroke") vanish on save
        feed("material", {
            "name": mat.name,
            "diffuse": [round(c, 5) for c in mat.diffuse_color],
        })

    feed("collections", sorted(c.name for c in bpy.data.collections))
    return h.hexdigest()


class Journal:
    """Holds the last-read snapshot; every read/exec advances the baseline."""

    def __init__(self):
        self._last = snapshot()

    def reset(self):
        self._last = snapshot()

    def advance(self):
        current = snapshot()
        delta = diff(self._last, current)
        self._last = current
        return delta
