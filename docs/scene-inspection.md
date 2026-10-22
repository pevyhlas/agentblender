# Structured scene inspection

`scene_inspect` (MCP/REST) and `abl scene inspect SESSION --request request.json`
read selected scene facts without creating objects or advancing the session head,
exec sequence, trajectory journal or state-delta baseline. Output is structured JSON.

```json
{
  "objects": ["Cube"],
  "camera": "Camera",
  "landmarks": [{ "name": "origin", "world": [0, 0, 0] }],
  "geometry": true,
  "materials": true,
  "modifiers": true
}
```

MCP uses `scene_inspect({session, request})`; REST is `POST /api/ops/scene_inspect`
with the same two fields. The CLI uses the daemon at `ABL_URL`, not an offline scene
reader. The omitted camera uses the active scene camera; an object-only scene with
no camera is supported, but projecting landmarks requires a camera. Missing selected
objects/cameras and unsupported panoramic/singular cameras reject as `bad_request`.

Profile `abl.scene-inspection.v1` includes the current `sourceCheckpoint`, object
world matrices (rows), parent and visibility, camera right/up/forward basis and profile,
and full render framing. Landmark pixels are continuous **top-left pixel-edge**
coordinates in the full render canvas; `depth`, `inFront`, `inFrame` and
`occlusionTested=false` distinguish projection from visibility. Border/crop flags and
rectangles are reported separately: coordinates are not rebased to cropped output.
No image or render is produced. Perspective and orthographic cameras are supported.

Optional raw mesh observations hash vertex coordinates, edge endpoints (including loose
edges), loop indices and face
boundaries/material/smoothing under `abl.raw-mesh-observation.v1`. They omit evaluated
geometry, UVs, attributes and shape keys. Material observations include bounded node
types/mute/input defaults/links and selected scalar parameters, with an independent
`abl.material-observation.v1` hash. Nested groups, textures, scripts, drivers and custom
properties are explicitly unsupported. Modifier facts are declared scalar/enum/small
array values; pointer/collection/custom properties are reported unsupported. None of
these hashes is the replay digest, a rights certificate or visual equivalence proof.

## Bounds and failure behavior

- Request: 16 KiB, 32 selected objects, 64 landmarks, 128-character nonblank names
  without controls, finite world coordinates with magnitude <=1e9.
- Optional aggregate work: 100,000 raw vertices/faces, 400,000 edges/loops, 64 material slots,
  256 nodes, 1,024 links, 2,048 sockets, 64 modifiers and 4,096 modifier properties.
- Response: 256 KiB, no silent truncation. Unsupported kinds are explicit; work/size
  overflow rejects the read without committing state. Transport-level limits remain
  separate, and do not imply zero allocation before host decoding.
- Dependency-graph evaluation uses the existing host timeout/worker cancellation
  guard. Selected raw-fact limits are not a hard memory bound on Blender's evaluation
  of an arbitrary authored scene. Reads may refresh derived Blender caches, not source
  intent or checkpoint state. Caller handlers/driver side effects are outside the
  inspection transaction guarantee, like other trusted-local Blender evaluation.
- Malformed worker success is worker-fatal; ordinary caller errors preserve the
  session. The operation adds no unrestricted execution or property-path endpoint.

Existing replay PASS records retain their original bounded coverage. A stronger
appearance-replay profile requires a separate versioned contract and qualification.
