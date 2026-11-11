# The sandbags' burlap: a tileable weave with dirt, and a normal map from the same weave. python textures.py <out folder>
import math
import os
import random
import sys

from PIL import Image, ImageFilter

out = sys.argv[1]
SIZE, THREADS = 512, 48
random.seed(11)

height = Image.new('F', (SIZE, SIZE))
colour = Image.new('RGB', (SIZE, SIZE))
h, c = height.load(), colour.load()
# Low-frequency dirt that tiles: a few wrapped sine waves.
waves = [(random.randint(1, 4), random.randint(1, 4), random.uniform(0, 6.28), random.uniform(.3, 1)) for _ in range(6)]
for y in range(SIZE):
    for x in range(SIZE):
        u, v = x / SIZE, y / SIZE
        # Over-under weave: warp and weft alternate which is on top.
        wx = (u * THREADS) % 1.0
        wy = (v * THREADS) % 1.0
        over = (int(u * THREADS) + int(v * THREADS)) % 2
        warp = math.sin(wx * math.pi) ** .6
        weft = math.sin(wy * math.pi) ** .6
        thread = warp if over else weft
        gap = min(math.sin(wx * math.pi), math.sin(wy * math.pi))
        dirt = sum(a * math.sin(2 * math.pi * (i * u + j * v) + p) for i, j, p, a in waves) / 6
        fibre = random.uniform(-.08, .08)
        value = .55 + .35 * thread - .25 * (gap < .15) + fibre
        h[x, y] = value
        shade = .78 + .22 * value + .10 * dirt
        c[x, y] = tuple(max(0, min(255, int(base * shade))) for base in (156, 132, 94))
colour = colour.filter(ImageFilter.GaussianBlur(.4))
os.makedirs(out, exist_ok=True)
colour.save(os.path.join(out, 'sandbags_base.jpg'), quality=90)

# The weave's slope, as a tangent-space normal map (wrapping round the edges, so it tiles).
normal = Image.new('RGB', (SIZE, SIZE))
n = normal.load()
STRENGTH = 2.5
for y in range(SIZE):
    for x in range(SIZE):
        dx = (h[(x + 1) % SIZE, y] - h[(x - 1) % SIZE, y]) * STRENGTH
        dy = (h[x, (y + 1) % SIZE] - h[x, (y - 1) % SIZE]) * STRENGTH
        length = math.sqrt(dx * dx + dy * dy + 1)
        nx, ny, nz = -dx / length, -dy / length, 1 / length
        n[x, y] = (int((nx * .5 + .5) * 255), int((ny * .5 + .5) * 255), int((nz * .5 + .5) * 255))
normal.save(os.path.join(out, 'sandbags_normal.png'))
print('TEXTURES done')
