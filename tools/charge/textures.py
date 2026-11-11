"""Packs the baked textures the way the game's shader reads them.
Metal goes in red and smoothness in alpha. A normal map's X goes in alpha, which is what it unpacks at runtime."""
import os, sys
from PIL import Image

work, out = sys.argv[1], sys.argv[2]
Image.open(os.path.join(work, 'baked_base.png')).convert('RGB').save(os.path.join(out, 'charge_base.jpg'), quality=90)

normal = Image.open(os.path.join(work, 'baked_normal.png')).convert('RGB')
nx, ny, _ = normal.split()
Image.merge('RGBA', (Image.new('L', normal.size, 255), ny, ny, nx)).save(os.path.join(out, 'charge_normal.png'), optimize=True)

_, rough, metal = Image.open(os.path.join(work, 'baked_rm.png')).convert('RGB').resize((512, 512), Image.LANCZOS).split()
smooth = rough.point(lambda v: 255 - v)
Image.merge('RGBA', (metal, metal, metal, smooth)).save(os.path.join(out, 'charge_metal.png'), optimize=True)
print('textures done')
