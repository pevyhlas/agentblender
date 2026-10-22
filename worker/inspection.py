"""Bounded observations, not replay equality. Runs only inside the Blender worker."""

import hashlib
import json
import math
import struct

import bpy
from mathutils import Vector

PROFILE = "abl.scene-inspection.v1"
MAX_RESPONSE = 256 * 1024


class InspectionError(Exception):
    abl_kind = "bad_request"


def _name(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 128 or any(ord(c) < 32 for c in value):
        raise InspectionError("inspection names must be nonblank, <=128 characters, without controls")
    return value


def _vector(values):
    result = [float(v) for v in values]
    if len(result) > 16 or any(not math.isfinite(v) for v in result):
        raise InspectionError("unsupported/nonfinite inspection vector")
    return result


def _charge(budget, key, amount, limit):
    budget[key] = budget.get(key, 0) + amount
    if budget[key] > limit:
        raise InspectionError(f"inspection {key} work limit {limit} exceeded; split objects or disable the fact section")


def _primitive(owner, prop):
    value = getattr(owner, prop.identifier)
    if prop.type in ("BOOLEAN", "INT", "FLOAT"):
        if getattr(prop, "is_array", False):
            return _vector(value)
        if prop.type == "FLOAT" and not math.isfinite(value):
            raise InspectionError("nonfinite inspection value")
        return value
    if prop.type == "ENUM":
        return sorted(value) if prop.is_enum_flag else value
    # Never traverse pointer/collection/custom/string properties or external paths.
    raise InspectionError("unsupported property kind")


def _mesh(mesh, budget):
    _charge(budget, "vertices", len(mesh.vertices), 100000)
    _charge(budget, "edges", len(mesh.edges), 400000)
    _charge(budget, "loops", len(mesh.loops), 400000)
    _charge(budget, "faces", len(mesh.polygons), 100000)
    h = hashlib.sha256(b"abl.raw-mesh-observation.v1\n")
    h.update(struct.pack("<QQQQ", len(mesh.vertices), len(mesh.edges), len(mesh.loops), len(mesh.polygons)))
    for vertex in mesh.vertices:
        h.update(struct.pack("<ddd", *_vector(vertex.co)))
    for loop in mesh.loops:
        h.update(struct.pack("<Q", loop.vertex_index))
    for edge in mesh.edges:
        h.update(struct.pack("<QQ", *edge.vertices))
    for face in mesh.polygons:
        h.update(struct.pack("<QQQ?", face.loop_start, face.loop_total, face.material_index, face.use_smooth))
    return {"profile": "abl.raw-mesh-observation.v1", "vertices": len(mesh.vertices), "edges": len(mesh.edges),
            "loops": len(mesh.loops), "faces": len(mesh.polygons), "sha256": h.hexdigest(),
            "unsupported": ["evaluated-mesh", "uvs", "attributes", "shape-keys"]}


def _material(material, budget):
    result = {"name": _name(material.name), "diffuseColor": _vector(material.diffuse_color),
              "useNodes": material.use_nodes, "unsupported": []}
    tree = material.node_tree if material.use_nodes else None
    if tree is None:
        result["nodes"] = []
        result["links"] = []
    else:
        _charge(budget, "nodes", len(tree.nodes), 256)
        _charge(budget, "links", len(tree.links), 1024)
        nodes = []
        for node in sorted(tree.nodes, key=lambda n: n.name):
            _charge(budget, "sockets", len(node.inputs) + len(node.outputs), 2048)
            facts = {"name": _name(node.name), "type": node.bl_idname, "mute": node.mute, "inputs": []}
            if node.type in ("GROUP", "TEX_IMAGE", "SCRIPT"):
                result["unsupported"].append(node.name + ":nested-or-external")
            for socket in node.inputs:
                fact = {"name": _name(socket.name), "identifier": _name(socket.identifier),
                        "linked": socket.is_linked}
                if hasattr(socket, "default_value"):
                    value = socket.default_value
                    if isinstance(value, (float, int, bool)):
                        if isinstance(value, float) and not math.isfinite(value):
                            raise InspectionError("nonfinite material socket")
                        fact["value"] = value
                    elif socket.type in ("RGBA", "VECTOR"):
                        fact["value"] = _vector(value)
                    else:
                        fact["unsupported"] = "default-value-kind"
                facts["inputs"].append(fact)
            # Bounded scalar node parameters cover e.g. Noise Scale/Math operation.
            facts["parameters"] = {}
            for prop in node.bl_rna.properties:
                if prop.identifier in ("operation", "blend_type", "distribution", "interpolation",
                                       "noise_dimensions", "normalize", "data_type"):
                    facts["parameters"][prop.identifier] = _primitive(node, prop)
            nodes.append(facts)
        result["nodes"] = nodes
        result["links"] = sorted([
            [link.from_node.name, link.from_socket.identifier, link.to_node.name, link.to_socket.identifier]
            for link in tree.links
        ])
    result["unsupported"].extend(["nested-node-trees", "textures", "drivers", "custom-properties"])
    canonical = json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False)
    result["observationSha256"] = hashlib.sha256(
        b"abl.material-observation.v1\n" + canonical.encode("utf-8")).hexdigest()
    return result


def _modifiers(obj, budget):
    _charge(budget, "modifiers", len(obj.modifiers), 64)
    result = []
    for modifier in obj.modifiers:
        fact = {"name": _name(modifier.name), "type": modifier.type, "values": {}, "unsupported": []}
        for prop in modifier.bl_rna.properties:
            _charge(budget, "modifier-properties", 1, 4096)
            if prop.is_readonly or prop.identifier in ("name", "type"):
                continue
            if prop.type in ("BOOLEAN", "INT", "FLOAT", "ENUM") and (not getattr(prop, "is_array", False) or prop.array_length <= 16):
                fact["values"][prop.identifier] = _primitive(modifier, prop)
            else:
                fact["unsupported"].append(prop.identifier)
        result.append(fact)
    return result


def inspect(req):
    if any(type(req.get(field, False)) is not bool for field in ("geometry", "materials", "modifiers")):
        raise InspectionError("inspection section flags must be booleans")
    names = req.get("objects", [])
    landmarks = req.get("landmarks", [])
    if not isinstance(names, list) or len(names) > 32 or not isinstance(landmarks, list) or len(landmarks) > 64:
        raise InspectionError("inspection count limit exceeded")
    if len(set(_name(n) for n in names)) != len(names):
        raise InspectionError("duplicate object names")
    scene = bpy.context.scene
    selected = [scene.objects.get(name) for name in names]
    if any(obj is None for obj in selected):
        raise InspectionError("selected object does not exist in the active scene")
    camera_name = req.get("camera")
    camera = scene.objects.get(_name(camera_name)) if camera_name is not None else scene.camera
    if camera_name is not None and camera is None:
        raise InspectionError("selected camera does not exist")
    if camera is not None and (camera.type != "CAMERA" or camera.data.type not in ("PERSP", "ORTHO")):
        raise InspectionError("camera must be perspective or orthographic")
    if landmarks and camera is None:
        raise InspectionError("landmark projection requires an active or selected camera")
    point_names = set()
    for point in landmarks:
        if not isinstance(point, dict) or set(point) != {"name", "world"}:
            raise InspectionError("invalid landmark shape")
        name = _name(point["name"])
        world = point["world"]
        if name in point_names or not isinstance(world, list) or len(world) != 3 or any(
                isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or abs(v) > 1e9 for v in world):
            raise InspectionError("invalid or duplicate landmark coordinates")
        point_names.add(name)
    # No journal advance, save, operator execution, marker objects or source changes.
    # Dependency-graph evaluation uses the host's existing timeout/worker-death guard;
    # selected raw-fact budgets are not a hard memory bound on Blender evaluation.
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    budget = {}
    width = max(1, scene.render.resolution_x * scene.render.resolution_percentage // 100)
    height = max(1, scene.render.resolution_y * scene.render.resolution_percentage // 100)
    framing = {"width": width, "height": height, "pixelAspect": [scene.render.pixel_aspect_x, scene.render.pixel_aspect_y],
               "coordinates": "top-left-pixel-edges-x-right-y-down", "border": scene.render.use_border,
               "cropToBorder": scene.render.use_crop_to_border,
               "borderRect": [scene.render.border_min_x, scene.render.border_min_y,
                              scene.render.border_max_x, scene.render.border_max_y]}
    objects = []
    for obj in selected:
        evaluated = obj.evaluated_get(depsgraph)
        fact = {"name": obj.name, "type": obj.type, "parent": obj.parent.name if obj.parent else None,
                "matrixWorld": [_vector(row) for row in evaluated.matrix_world],
                "hideViewport": obj.hide_viewport, "hideRender": obj.hide_render}
        if req.get("geometry"):
            fact["geometry"] = _mesh(obj.data, budget) if obj.type == "MESH" else {"unsupported": ["not-a-mesh"]}
        if req.get("materials"):
            _charge(budget, "material-slots", len(obj.material_slots), 64)
            fact["materials"] = [_material(slot.material, budget) if slot.material else None for slot in obj.material_slots]
        if req.get("modifiers"):
            fact["modifiers"] = _modifiers(obj, budget)
        objects.append(fact)
    camera_fact = None
    projected = []
    if camera is not None:
        camera = camera.evaluated_get(depsgraph)
        matrix = camera.matrix_world.normalized()
        if abs(matrix.determinant()) < 1e-12:
            raise InspectionError("camera transform is singular")
        data = camera.data
        camera_fact = {"name": camera.name, "type": data.type, "matrixWorld": [_vector(row) for row in camera.matrix_world],
                       "right": _vector(matrix.col[0][:3]), "up": _vector(matrix.col[1][:3]),
                       "forward": _vector(-matrix.col[2].to_3d()), "lens": data.lens, "orthoScale": data.ortho_scale,
                       "shift": [data.shift_x, data.shift_y], "sensorFit": data.sensor_fit,
                       "sensor": [data.sensor_width, data.sensor_height], "clip": [data.clip_start, data.clip_end]}
        projection = camera.calc_matrix_camera(depsgraph, x=width, y=height,
                                              scale_x=scene.render.pixel_aspect_x, scale_y=scene.render.pixel_aspect_y)
        inverse = matrix.inverted()
        for point in landmarks:
            local = inverse @ Vector([*point["world"], 1])
            clip = projection @ local
            pixels = None if abs(clip.w) < 1e-12 else [
                (clip.x / clip.w + 1) * width / 2, (1 - clip.y / clip.w) * height / 2]
            projected.append({"name": point["name"], "pixels": pixels, "depth": -local.z,
                              "inFront": local.z < 0,
                              "inFrame": pixels is not None and local.z < 0 and
                              0 <= pixels[0] <= width and 0 <= pixels[1] <= height and -1 <= clip.z / clip.w <= 1,
                              "occlusionTested": False})
    result = {"profile": PROFILE, "objects": objects, "camera": camera_fact, "framing": framing,
              "landmarks": projected, "coverage": "selected-raw-facts-not-replay-or-visual-equivalence"}
    if len(json.dumps(result, allow_nan=False).encode("utf-8")) > MAX_RESPONSE:
        raise InspectionError("inspection response exceeds 256 KiB; split objects or disable material/modifier facts")
    return {"inspection": result}
