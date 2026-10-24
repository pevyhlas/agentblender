"""Original AgentBlender emblem; execute through abl scene exec, never host Python."""
import bpy
from mathutils import Vector

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)

def material(name, color, metallic, roughness):
    result = bpy.data.materials.new(name)
    result.diffuse_color = (*color, 1)
    result.use_nodes = True
    bsdf = result.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (*color, 1)
    bsdf.inputs['Metallic'].default_value = metallic
    bsdf.inputs['Roughness'].default_value = roughness
    return result

ceramic = material('Cool ceramic', (0.12, 0.40, 0.62), 0.36, 0.23)
core = material('Warm core', (0.95, 0.47, 0.13), 0.22, 0.25)

def prism(name, points, depth, surface):
    count = len(points)
    verts = [(x, y, z) for z in (0, depth) for x, y in points]
    faces = [tuple(reversed(range(count))), tuple(range(count, count * 2))]
    faces += [(i, (i + 1) % count, (i + 1) % count + count, i + count) for i in range(count)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(surface)
    bevel = obj.modifiers.new('Machined edge', 'BEVEL')
    bevel.width = 0.075
    bevel.segments = 4
    obj.modifiers.new('Weighted normals', 'WEIGHTED_NORMAL')
    return obj

points = [(-1.4, 1.4), (0.95, 1.4), (0.40, 0.85), (-0.85, 0.85), (-0.85, -0.40), (-1.4, -0.95)]
prism('Northwest bracket', list(reversed(points)), 0.48, ceramic)
prism('Southeast bracket', [(-x, -y) for x, y in reversed(points)], 0.48, ceramic)
prism('Editable core', [(0, -0.48), (0.48, 0), (0, 0.48), (-0.48, 0)], 0.70, core)

def aim(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()

bpy.ops.object.camera_add(location=(3.6, -5.8, 10.5))
camera = bpy.context.object
camera.name = 'Brand camera'
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 4.9
aim(camera, (0, 0, 0.24))
bpy.context.scene.camera = camera

for name, position, energy, size, color in [
    ('Key softbox', (-3, -4, 7), 350, 5, (0.82, 0.91, 1)),
    ('Warm edge', (4, 2, 5), 450, 4, (1, 0.78, 0.54)),
    ('Top fill', (-2, 5, 6), 280, 3, (0.55, 0.78, 1)),
]:
    bpy.ops.object.light_add(type='AREA', location=position)
    light = bpy.context.object
    light.name = name
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    light.data.color = color
    aim(light, (0, 0, 0))

scene = bpy.context.scene
scene.world.color = (0.18, 0.18, 0.18)
scene.render.film_transparent = True
scene.render.resolution_x = 1024
scene.render.resolution_y = 1024
scene.render.resolution_percentage = 100
scene.view_settings.view_transform = 'AgX'
