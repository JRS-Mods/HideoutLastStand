# Run by make_defences.ps1 inside Blender (blender -b --factory-startup --python make_defences.py -- <out folder>).
# Builds the Defences tab's models and writes each in the same packed form as the charge and the sandbags (the mod reads
# them directly). Each has its origin in the middle of its foot, its length along the game's x and its depth along z.
#
#   steel.mesh  A steel barricade: a 1.2 by 1.5 m plate with a firing slit, on skids, propped from behind. Its front is the
#               game's +z, and the props reach 0.6 m behind it.
#   wire.mesh   A 2 m concertina coil of barbed wire on three angle-iron pickets.
import array
import math
import os
import random
import struct
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

out = sys.argv[sys.argv.index('--') + 1]
os.makedirs(out, exist_ok=True)


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def box(lo, hi, bevel=0.0):
    """A box between two corners (Blender's axes: x across, y back, z up), its edges chamfered."""
    centre = [(a + b) / 2 for a, b in zip(lo, hi)]
    size = [b - a for a, b in zip(lo, hi)]
    bpy.ops.mesh.primitive_cube_add(size=1, location=centre)
    o = bpy.context.active_object
    o.scale = size
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=True)
    if bevel > 0:
        m = o.modifiers.new('chamfer', 'BEVEL')
        m.width = bevel
        m.segments = 1
        m.limit_method = 'ANGLE'
        bpy.ops.object.modifier_apply(modifier=m.name)
    return o


def bar(a, b, thick):
    """A square bar from a to b."""
    a, b = Vector(a), Vector(b)
    o = box((-thick / 2, -thick / 2, 0), (thick / 2, thick / 2, (b - a).length), bevel=thick * .12)
    o.matrix_world = Matrix.Translation(a) @ (b - a).to_track_quat('Z', 'Y').to_matrix().to_4x4()
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def join(objects):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    return bpy.context.active_object


def uv_and_triangulate(o, cube):
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.cube_project(cube_size=cube)
    bpy.ops.object.mode_set(mode='OBJECT')
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()


# Blender's right-handed, Z up. The game's left-handed, Y up: one axis flips, so triangles run the other way round.
def to_game(x, y, z):
    return -x, z, -y


def write(o, name):
    mesh = o.data
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
    with open(os.path.join(out, name + '.mesh'), 'wb') as f:
        # Counts, then a point of the model's own: here the top of it, in the middle.
        f.write(struct.pack('<ii3f', len(seen), len(triangles), 0.0, top, 0.0))
        for part in (positions, directions, tangents, coords, triangles):
            part.tofile(f)
    xs = [v.co.x for v in mesh.vertices]
    ys = [v.co.y for v in mesh.vertices]
    print(f'DEFENCES {name}: {len(triangles) // 3} triangles, {len(seen)} vertices, {max(xs) - min(xs):.2f} wide, {max(ys) - min(ys):.2f} deep, {top:.2f} high')


# ---- the steel barricade ------------------------------------------------------------------------------------

WIDTH, HEIGHT, PLATE = 1.2, 1.5, .03
SLIT_LOW, SLIT_HIGH, SLIT_HALF = 1.20, 1.32, .2
FOOT = .05

clear()
parts = []
# The plate, standing on the skids, with the slit cut through it.
plate = box((-WIDTH / 2, -PLATE / 2, FOOT), (WIDTH / 2, PLATE / 2, HEIGHT), bevel=.006)
slit = box((-SLIT_HALF, -.2, SLIT_LOW), (SLIT_HALF, .2, SLIT_HIGH))
cut = plate.modifiers.new('slit', 'BOOLEAN')
cut.operation = 'DIFFERENCE'
cut.object = slit
cut.solver = 'EXACT'
bpy.context.view_layer.objects.active = plate
bpy.ops.object.modifier_apply(modifier=cut.name)
bpy.data.objects.remove(slit, do_unlink=True)
parts.append(plate)
# Its edges folded back, so it's a stiff panel and not a sheet.
FLANGE = .045
parts.append(box((-WIDTH / 2, PLATE / 2, HEIGHT - .012), (WIDTH / 2, PLATE / 2 + FLANGE, HEIGHT), bevel=.003))
for side in (-1, 1):
    x0 = side * WIDTH / 2
    parts.append(box((min(x0, x0 - side * .012), PLATE / 2, FOOT), (max(x0, x0 - side * .012), PLATE / 2 + FLANGE, HEIGHT), bevel=.003))
# A hood over the slit on the front, to keep what comes down the barrel out of your eyes.
hood = box((-SLIT_HALF - .04, -.09, -.006), (SLIT_HALF + .04, 0, .006), bevel=.002)
hood.matrix_world = Matrix.Translation((0, -PLATE / 2, SLIT_HIGH + .02)) @ Matrix.Rotation(math.radians(-18), 4, 'X')
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
parts.append(hood)
for side in (-1, 1):
    parts.append(box((side * (SLIT_HALF + .04) - .006, -.09, SLIT_HIGH), (side * (SLIT_HALF + .04) + .006, -PLATE / 2, SLIT_HIGH + .05), bevel=.002))
# Skids along the floor, and props from high on the back down to their ends.
for side in (-1, 1):
    x = side * (WIDTH / 2 - .1)
    parts.append(box((x - .03, -.06, 0), (x + .03, .6, FOOT), bevel=.006))
    parts.append(bar((x, PLATE / 2 + .01, 1.08), (x, .57, FOOT), .045))
    # A plate welded where each prop meets the skid.
    parts.append(box((x - .045, .5, FOOT), (x + .045, .6, FOOT + .008), bevel=.002))
# A grab handle either side of the slit. Nothing across between the props: you stand in there, against the plate.
for side in (-1, 1):
    x = side * .32
    parts.append(bar((x - .09, PLATE / 2 + .06, 1.0), (x + .09, PLATE / 2 + .06, 1.0), .022))
    for end in (-1, 1):
        parts.append(bar((x + end * .09, PLATE / 2, 1.0), (x + end * .09, PLATE / 2 + .07, 1.0), .02))
steel = join(parts)
bpy.ops.object.shade_flat()
uv_and_triangulate(steel, 1.0)
write(steel, 'steel')

# ---- the barbed wire ----------------------------------------------------------------------------------------

LENGTH, COIL, WIRE = 2.0, .34, .0032
random.seed(23)
clear()
bm = bmesh.new()


def tube(points, radius, sides=4):
    """A thin tube along the points, its rings turned to follow it."""
    rings = []
    prev_side = None
    for i, p in enumerate(points):
        ahead = points[min(i + 1, len(points) - 1)] - points[max(i - 1, 0)]
        ahead.normalize()
        side = ahead.cross(Vector((0, 0, 1))) if prev_side is None else prev_side - ahead * prev_side.dot(ahead)
        if side.length < 1e-4:
            side = ahead.cross(Vector((1, 0, 0)))
        side.normalize()
        prev_side = side
        up = ahead.cross(side)
        rings.append([bm.verts.new(p + (side * math.cos(a) + up * math.sin(a)) * radius) for a in (2 * math.pi * k / sides for k in range(sides))])
    for a, b in zip(rings, rings[1:]):
        for k in range(sides):
            bm.faces.new((a[k], a[(k + 1) % sides], b[(k + 1) % sides], b[k]))


def needle(at, way, length, radius):
    """A barb: a thin spike either side of the wire."""
    way = way.normalized()
    side = way.orthogonal().normalized()
    up = way.cross(side)
    tip_a, tip_b = bm.verts.new(at + way * length), bm.verts.new(at - way * length)
    ring = [bm.verts.new(at + (side * math.cos(a) + up * math.sin(a)) * radius) for a in (0, 2.1, 4.2)]
    for k in range(3):
        bm.faces.new((ring[k], ring[(k + 1) % 3], tip_a))
        bm.faces.new((ring[(k + 1) % 3], ring[k], tip_b))


# Two coils wound opposite ways, as concertina wire pulls out: rings that lean, crossing each other in diamonds.
LOOPS, STEP = 18, .05
for hand in (1, -1):
    points = []
    circumference = 2 * math.pi * COIL
    count = int(LOOPS * circumference / STEP)
    phase = random.uniform(0, math.pi)
    for i in range(count + 1):
        t = i / count
        a = hand * 2 * math.pi * LOOPS * t + phase
        wobble = 1 + .035 * math.sin(a * 3.7 + hand) + .02 * math.sin(t * 40)
        r = COIL * wobble
        points.append(Vector((-LENGTH / 2 + LENGTH * t, r * math.cos(a), COIL + .02 + r * math.sin(a))))
    tube(points, WIRE)
    # Barbs every 15 cm along it.
    every = max(1, int(.15 / STEP))
    for i in range(every // 2, len(points) - 1, every):
        p, ahead = points[i], points[i + 1] - points[i]
        twist = ahead.orthogonal().normalized()
        twist.rotate(Matrix.Rotation(random.uniform(0, math.pi), 3, ahead.normalized()))
        needle(p, twist, .014, WIRE * .9)
        twist.rotate(Matrix.Rotation(math.pi / 2, 3, ahead.normalized()))
        needle(p, twist, .014, WIRE * .9)
# A straight strand along the bottom, where the coil's pegged down.
tube([Vector((-LENGTH / 2 + LENGTH * k / 60, 0, .025)) for k in range(61)], WIRE)

bm.to_mesh(bpy.data.meshes.new('wire'))
wire_mesh = bpy.data.meshes['wire']
bm.free()
wire = bpy.data.objects.new('wire', wire_mesh)
bpy.context.scene.collection.objects.link(wire)
bpy.context.view_layer.objects.active = wire
wire.select_set(True)
bpy.ops.object.shade_smooth()
pieces = [wire]
# Angle-iron pickets inside the coil, driven into the floor and poking out of its top.
for x in (-.85, 0, .85):
    for o in (box((x - .002, -.02, 0), (x + .002, .02, .82)), box((x - .002, -.02, 0), (x + .038, -.016, .82))):
        bpy.ops.object.select_all(action='DESELECT')
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.shade_flat()
        pieces.append(o)
wire = join(pieces)
uv_and_triangulate(wire, .5)
write(wire, 'wire')
