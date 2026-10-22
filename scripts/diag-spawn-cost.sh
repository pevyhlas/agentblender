#!/usr/bin/env bash
# TEMPORARY diagnostic (to be removed before merge): times the pieces of a worker spawn on
# this runner.
set -uo pipefail
B="$ABL_BLENDER"
OUT="$RUNNER_TEMP/diag"
mkdir -p "$OUT"
nproc; free -g | head -2
run() {
    local name="$1"; shift
    local start end
    start=$(date +%s.%N)
    "$@" > "$OUT/$name.log" 2>&1
    local rc=$?
    end=$(date +%s.%N)
    printf '== %-22s rc=%s %6.1f s\n' "$name" "$rc" "$(echo "$end - $start" | bc)"
    grep -E '^DIAG|Error|error' "$OUT/$name.log" | head -8
}
NOOP="import sys; print('DIAG noop')"
DEV="import bpy, time; t=time.monotonic(); p=bpy.context.preferences.addons['cycles'].preferences; [p.get_devices_for_type(k) for k in ('CUDA','OPTIX','HIP','ONEAPI','METAL')]; print('DIAG devices', round(time.monotonic()-t,2), [(d.type,d.name) for d in p.devices]); ps=bpy.context.preferences.system; print('DIAG system', {a: getattr(ps,a) for a in dir(ps) if 'shader' in a or 'compil' in a or 'gpu' in a})"
EEVEE="import bpy, gpu, sys, time, os; s=bpy.context.scene; s.render.engine='BLENDER_EEVEE'; s.render.resolution_x=s.render.resolution_y=64; s.render.resolution_percentage=100; s.render.filepath=os.path.join(os.environ.get('RUNNER_TEMP','/tmp'),'diag.png'); t=time.monotonic(); bpy.ops.render.render(write_still=True); r1=time.monotonic()-t; t=time.monotonic(); bpy.ops.render.render(write_still=True); print('DIAG eevee first', round(r1,2), 'second', round(time.monotonic()-t,2), gpu.platform.backend_type_get(), gpu.platform.renderer_get(), gpu.platform.version_get())"
WB="import bpy, time, os; s=bpy.context.scene; s.render.engine='BLENDER_WORKBENCH'; s.render.resolution_x=s.render.resolution_y=64; s.render.filepath=os.path.join(os.environ.get('RUNNER_TEMP','/tmp'),'diag.png'); t=time.monotonic(); bpy.ops.render.render(write_still=True); print('DIAG workbench', round(time.monotonic()-t,2))"
E=(env -u DISPLAY -u WAYLAND_DISPLAY)
run startup "${E[@]}" "$B" --background --factory-startup --python-expr "$NOOP"
run startup2 "${E[@]}" "$B" --background --factory-startup --python-expr "$NOOP"
run devices "${E[@]}" "$B" --background --factory-startup --python-expr "$DEV"
run workbench "${E[@]}" "$B" --background --factory-startup --python-expr "$WB"
run eevee1 "${E[@]}" "$B" --background --factory-startup --python-expr "$EEVEE"
run eevee2 "${E[@]}" "$B" --background --factory-startup --python-expr "$EEVEE"
run eevee-nocache "${E[@]}" MESA_SHADER_CACHE_DISABLE=true "$B" --background --factory-startup --python-expr "$EEVEE"
run eevee-vulkan "${E[@]}" "$B" --background --factory-startup --gpu-backend vulkan --python-expr "$EEVEE"
run eevee-thread "${E[@]}" "$B" --background --factory-startup --python-expr "import bpy; ps=bpy.context.preferences.system; [setattr(ps, a, 'THREAD') for a in ('shader_compilation_method', 'gpu_shader_compilation_method') if hasattr(ps, a)]; $EEVEE"
du -sh ~/.cache/mesa_shader_cache* 2>/dev/null || echo "no mesa cache dir"
ls ~/.cache 2>/dev/null
grep -iE 'shader|compil' "$OUT/eevee1.log" | head -10
