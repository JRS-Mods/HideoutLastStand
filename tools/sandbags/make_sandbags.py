# Run by make_sandbags.ps1 inside Blender (blender -b --factory-startup --python make_sandbags.py -- <out folder>).
# Builds a chest-high wall of sandbags, six courses of three, each bag a softened, slightly lumpy pillow, and writes it in the
# same packed form as the charge (charge.mesh): the mod reads it directly. Its origin is the middle of the wall's foot,
# its length along the game's x, its depth along z.
import array
import math
import os
import random
import struct
import sys

import bmesh
import bpy
from mathutils import Vector

out = sys.argv[sys.argv.index('--') + 1]
random.seed(7)

bpy.ops.wm.read_factory_settings(use_empty=True)

BAG_LONG, BAG_DEEP, BAG_HIGH = .56, .33, .18
COURSES, PER_COURSE = 6, 3
# Each course settles into the one below.
SETTLE = .78
LENGTH = 1.5
bags = []
for course in range(COURSES):
    stagger = .13 if course % 2 else -.13
    for i in range(PER_COURSE):
        x = (i - (PER_COURSE - 1) / 2) * (LENGTH / PER_COURSE) + stagger
        x = max(-LENGTH / 2 + BAG_LONG / 2 - .04, min(LENGTH / 2 - BAG_LONG / 2 + .04, x))
        squash = random.uniform(.88, 1.08)
        bpy.ops.mesh.primitive_cube_add(size=1)
        bag = bpy.context.active_object
        bag.scale = (BAG_LONG * random.uniform(.95, 1.04), BAG_DEEP * random.uniform(.94, 1.05), BAG_HIGH * squash)
        bag.rotation_euler = (random.uniform(-.03, .03), random.uniform(-.04, .04), random.uniform(-.07, .07))
        bag.location = (x, random.uniform(-.02, .02), course * BAG_HIGH * SETTLE + BAG_HIGH * squash / 2)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        bevel = bag.modifiers.new('soft', 'BEVEL')
        bevel.width = .055
        bevel.segments = 3
        bpy.ops.object.modifier_apply(modifier=bevel.name)
        # Filled bags bulge in the middle and sag at the ends, and no two are quite alike.
        mesh = bag.data
        for v in mesh.vertices:
            co = v.co
            ends = abs(co.x) / (BAG_LONG / 2)
            co.z *= 1.0 - .14 * ends ** 4
            co.y *= 1.0 - .08 * ends ** 4
            # Pressed flat where it rests on the bag below.
            if co.z < 0: co.z *= .8
            co += Vector((random.uniform(-.004, .004), random.uniform(-.004, .004), random.uniform(-.004, .004)))
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        bags.append(bag)

bpy.ops.object.select_all(action='DESELECT')
for b in bags:
    b.select_set(True)
bpy.context.view_layer.objects.active = bags[0]
bpy.ops.object.join()
wall = bpy.context.active_object
bpy.ops.object.shade_smooth()

# Burlap tiles every 0.6 m, so the weave is the same size on every bag.
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.cube_project(cube_size=.6)
bpy.ops.object.mode_set(mode='OBJECT')
bm = bmesh.new()
bm.from_mesh(wall.data)
bmesh.ops.triangulate(bm, faces=bm.faces)
bm.to_mesh(wall.data)
bm.free()
mesh = wall.data
mesh.update()


# Blender's right-handed, Z up. The game's left-handed, Y up: one axis flips, so triangles run the other way round.
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

top = max(v.co.z for v in mesh.vertices)
os.makedirs(out, exist_ok=True)
with open(os.path.join(out, 'sandbags.mesh'), 'wb') as f:
    # The same header as the charge's: counts, then a point (here the top of the wall, in the middle).
    f.write(struct.pack('<ii3f', len(seen), len(triangles), 0.0, top, 0.0))
    for part in (positions, directions, tangents, coords, triangles):
        part.tofile(f)
print(f'SANDBAGS {len(triangles) // 3} triangles, {len(seen)} vertices, {top:.2f} m high')
