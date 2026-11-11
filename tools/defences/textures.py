# The Defences tab's textures, drawn here rather than shipped: python textures.py <out folder>
#   steel_base.jpg, steel_metal.png                    olive paint, chipped to bare steel, with rust in the chips and grime low down
#   wire_base.jpg, wire_metal.png                      galvanised wire going to rust
# The metal maps are as the charge's: metallic in RGB, smoothness in alpha. Every texture tiles.
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(31)


def noise(size, scale, octaves=4):
    """Tiling value noise in 0..1: random grids, laid three by three, scaled up smoothly, and the middle one kept."""
    total, weight, amp = np.zeros((size, size)), 0.0, 1.0
    for o in range(octaves):
        cells = max(2, int(scale * 2 ** o))
        grid = np.tile(rng.random((cells, cells)), (3, 3))
        img = Image.fromarray((grid * 255).astype(np.uint8)).resize((size * 3, size * 3), Image.BICUBIC)
        total += amp * np.asarray(img, dtype=np.float64)[size:2 * size, size:2 * size] / 255
        weight += amp
        amp *= .5
    total /= weight
    return (total - total.min()) / (total.max() - total.min() + 1e-9)


def save_rgb(a, name, quality=90):
    Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8), 'RGB').save(os.path.join(out, name), quality=quality)


def normal_from(height, strength):
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    n = np.stack([-dx, -dy, np.ones_like(dx)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * .5 + .5


# ---- steel --------------------------------------------------------------------------------------------------
S = 1024
paint = np.array([.40, .42, .28])
bare = np.array([.44, .44, .45])
rust = np.array([.36, .19, .09])
grime = noise(S, 3, 5)
chips_n = noise(S, 34, 3) * .7 + noise(S, 6, 2) * .3
chips = chips_n > .80
edge = (chips_n > .76) & ~chips
# Scratches: short bright strokes at random angles.
scratch = np.zeros((S, S))
for _ in range(140):
    x, y = rng.integers(0, S, 2)
    a = rng.uniform(0, np.pi)
    length = rng.integers(20, 140)
    for t in range(length):
        scratch[int(y + np.sin(a) * t) % S, int(x + np.cos(a) * t) % S] = 1
scratch = np.asarray(Image.fromarray((scratch * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(.6)), dtype=np.float64) / 255
# Rust runs down from the chips (the texture's v runs up the plate, so down is +rows in the image).
runs = np.zeros((S, S))
drip = chips.astype(np.float64)
for k in range(1, 40):
    runs = np.maximum(runs, np.roll(drip, k, 0) * (1 - k / 40))
runs *= noise(S, 30, 2) > .45

colour = paint * (.82 + .3 * grime[..., None])
colour = colour * (1 - .5 * runs[..., None]) + rust * .5 * runs[..., None]
colour = np.where(edge[..., None], rust * (.8 + .4 * grime[..., None]), colour)
colour = np.where(chips[..., None], bare * (.75 + .5 * noise(S, 60, 2)[..., None]), colour)
colour = colour + scratch[..., None] * .12
save_rgb(colour, 'steel_base.jpg')

metal = np.where(chips, .85, np.where(edge, .15, .05)) + scratch * .5
smooth = np.where(chips, .55, np.where(edge, .1, .32 - .12 * grime)) + scratch * .2
m8 = (np.clip(metal, 0, 1) * 255).astype(np.uint8)
Image.merge('RGBA', [Image.fromarray(m8)] * 3 + [Image.fromarray((np.clip(smooth, 0, 1) * 255).astype(np.uint8))]).save(os.path.join(out, 'steel_metal.png'), optimize=True)

# ---- wire ---------------------------------------------------------------------------------------------------
W = 256
zinc = np.array([.55, .56, .55])
brown = np.array([.33, .18, .08])
spots = noise(W, 6, 4)
rusty = np.clip((spots - .45) * 3, 0, 1)
colour = zinc * (.8 + .3 * noise(W, 20, 2))[..., None] * (1 - rusty[..., None]) + brown * (.8 + .4 * spots)[..., None] * rusty[..., None]
save_rgb(colour, 'wire_base.jpg')
metal = (1 - rusty) * .8 + .05
smooth = (1 - rusty) * .45 + .08
m8 = (metal * 255).astype(np.uint8)
Image.merge('RGBA', [Image.fromarray(m8)] * 3 + [Image.fromarray((smooth * 255).astype(np.uint8))]).save(os.path.join(out, 'wire_metal.png'), optimize=True)
print('DEFENCES textures written')
