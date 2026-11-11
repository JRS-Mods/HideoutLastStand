"""Blender, headless: cuts the charge model down to something a game can afford, bakes the original's detail back onto it, and sets its size and pivot.
Afterwards it's the height of a real charge, its origin is the middle of its back (the side that goes on the door), and its front faces +Z in the game.
Writes charge.mesh for the mod to read: vertex count, index count, the lamp's position, then positions, normals, tangents, UVs and triangles.
The baked textures go in the work folder for textures.py to pack."""
import array, math, os, struct, sys
import bmesh, bpy
from mathutils import Vector

TRIANGLES = 10000
HEIGHT = .42
BAKE_SIZE = 1024
# The red lamp on the front panel, where the model has it.
LAMP = (-.0652, -.1888, .258)

arguments = sys.argv[sys.argv.index('--') + 1:]
src, out, work_folder = arguments[:3]
pictures = arguments[3] if len(arguments) > 3 else None

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
scene = bpy.context.scene
high = next(o for o in scene.objects if o.type == 'MESH')
bpy.context.view_layer.objects.active = high
high.select_set(True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
before = sum(len(p.vertices) - 2 for p in high.data.polygons)

low = high.copy()
low.data = high.data.copy()
low.name = low.data.name = 'BreachCharge'
scene.collection.objects.link(low)
bpy.ops.object.select_all(action='DESELECT')
bpy.context.view_layer.objects.active = low
low.select_set(True)
mesh = low.data

# The file's vertices are split wherever its UVs are, and collapsing those separately would open cracks. UVs live on the corners, so joining them is safe.
bpy.ops.mesh.customdata_custom_splitnormals_clear()
work = bmesh.new()
work.from_mesh(mesh)
bmesh.ops.remove_doubles(work, verts=work.verts, dist=1e-5)
work.to_mesh(mesh)
work.free()

decimate = low.modifiers.new('cut', 'DECIMATE')
decimate.decimate_type = 'COLLAPSE'
decimate.ratio = TRIANGLES / before
decimate.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=decimate.name)

# Hard edges stay hard, curved parts stay smooth.
work = bmesh.new()
work.from_mesh(mesh)
for face in work.faces:
    face.smooth = True
for edge in work.edges:
    edge.smooth = not (edge.is_manifold and edge.calc_face_angle(0) > math.radians(50))
work.to_mesh(mesh)
work.free()
after = sum(len(p.vertices) - 2 for p in mesh.polygons)

# ---------- Bake: cutting triangles bends the textures across what's left, and projecting the original onto the result straightens them out again.
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 8
scene.render.bake.margin = 6
original = high.data.materials[0]
nodes = original.node_tree.nodes
links = original.node_tree.links
surface = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL').inputs['Surface']
principled = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
base_image_node = principled.inputs['Base Color'].links[0].from_node
metal_image_node = principled.inputs['Roughness'].links[0].from_node.inputs[0].links[0].from_node
shine = nodes.new('ShaderNodeEmission')

target = bpy.data.materials.new('BreachCharge')
target.use_nodes = True
target_node = target.node_tree.nodes.new('ShaderNodeTexImage')
target.node_tree.nodes.active = target_node
mesh.materials.clear()
mesh.materials.append(target)

high.select_set(True)
low.select_set(True)
bpy.context.view_layer.objects.active = low


def bake(name, kind, colour_space):
    image = bpy.data.images.new(name, BAKE_SIZE, BAKE_SIZE, alpha=False)
    image.colorspace_settings.name = colour_space
    target_node.image = image
    bpy.ops.object.bake(type=kind, use_selected_to_active=True, cage_extrusion=.012, max_ray_distance=.04, margin=6, normal_space='TANGENT')
    image.filepath_raw = os.path.join(work_folder, name + '.png')
    image.file_format = 'PNG'
    image.save()
    return image


links.new(base_image_node.outputs['Color'], shine.inputs['Color'])
links.new(shine.outputs[0], surface)
base_baked = bake('baked_base', 'EMIT', 'sRGB')
links.new(metal_image_node.outputs['Color'], shine.inputs['Color'])
metal_baked = bake('baked_rm', 'EMIT', 'Non-Color')
links.new(principled.outputs[0], surface)
normal_baked = bake('baked_normal', 'NORMAL', 'Non-Color')

# ---------- Size and pivot
xs, ys, zs = zip(*[v.co for v in mesh.vertices])
scale = HEIGHT / (max(zs) - min(zs))
middle_x, back_y, middle_z = (max(xs) + min(xs)) / 2, max(ys), (max(zs) + min(zs)) / 2


def place(x, y, z):
    return (x - middle_x) * scale, (y - back_y) * scale, (z - middle_z) * scale


for v in mesh.vertices:
    v.co = place(*v.co)
mesh.update()


# Blender's right-handed, Z up. The game's left-handed, Y up: one axis flips, so triangles run the other way round and the tangent's handedness turns over.
def to_game(x, y, z):
    return -x, z, -y


mesh.calc_loop_triangles()
mesh.calc_tangents()
uvs = mesh.uv_layers.active.uv
normals = mesh.corner_normals
seen, positions, directions, tangents, coords, triangles = {}, array.array('f'), array.array('f'), array.array('f'), array.array('f'), array.array('i')
for triangle in mesh.loop_triangles:
    corners = []
    for index in triangle.loops:
        loop = mesh.loops[index]
        n, uv, t = normals[index].vector, uvs[index].vector, loop.tangent
        key = (loop.vertex_index, round(n.x, 3), round(n.y, 3), round(n.z, 3), round(uv.x, 5), round(uv.y, 5), round(t.x, 2), round(t.y, 2), round(t.z, 2), loop.bitangent_sign)
        if key not in seen:
            seen[key] = len(seen)
            positions.extend(to_game(*mesh.vertices[loop.vertex_index].co))
            directions.extend(to_game(*n))
            tangents.extend(to_game(*t))
            tangents.append(-loop.bitangent_sign)
            coords.extend((uv.x, uv.y))
        corners.append(seen[key])
    triangles.extend((corners[0], corners[2], corners[1]))

lamp = to_game(*place(*LAMP))
with open(os.path.join(out, 'charge.mesh'), 'wb') as f:
    f.write(struct.pack('<ii3f', len(seen), len(triangles), *lamp))
    for part in (positions, directions, tangents, coords, triangles):
        part.tofile(f)
print(f'OPTIMISED {before} -> {after} triangles, {len(seen)} vertices, lamp {[round(v, 3) for v in lamp]}')

if pictures:
    # The finished thing, with only the baked textures on it, to check it by.
    bpy.data.objects.remove(high)
    tree = target.node_tree
    final = next(n for n in tree.nodes if n.type == 'BSDF_PRINCIPLED')
    colour_node = tree.nodes.new('ShaderNodeTexImage')
    colour_node.image = base_baked
    tree.links.new(colour_node.outputs['Color'], final.inputs['Base Color'])
    metal_node = tree.nodes.new('ShaderNodeTexImage')
    metal_node.image = metal_baked
    split = tree.nodes.new('ShaderNodeSeparateColor')
    tree.links.new(metal_node.outputs['Color'], split.inputs[0])
    tree.links.new(split.outputs[1], final.inputs['Roughness'])
    tree.links.new(split.outputs[2], final.inputs['Metallic'])
    normal_node = tree.nodes.new('ShaderNodeTexImage')
    normal_node.image = normal_baked
    map_node = tree.nodes.new('ShaderNodeNormalMap')
    tree.links.new(normal_node.outputs['Color'], map_node.inputs['Color'])
    tree.links.new(map_node.outputs[0], final.inputs['Normal'])

    scene.cycles.samples = 24
    scene.render.resolution_x = scene.render.resolution_y = 700
    scene.world = bpy.data.worlds.new('w')
    scene.world.color = (.25, .25, .25)
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN'))
    scene.collection.objects.link(sun)
    sun.data.energy = 3
    sun.rotation_euler = (.9, .2, -.5)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    aim = Vector((0, -.08, 0))
    for name, where in {'front': (.28, -.7, .12), 'side': (-.75, -.25, .15), 'back': (.2, .75, .1)}.items():
        cam.location = Vector(where)
        cam.rotation_euler = (aim - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(pictures, name + '.png')
        bpy.ops.render.render(write_still=True)
