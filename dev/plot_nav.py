import sys
from PIL import Image, ImageDraw
path, out = sys.argv[1], sys.argv[2]
half = float(sys.argv[3]) if len(sys.argv) > 3 else 50
extra = [tuple(map(float, a.split(','))) for a in sys.argv[4:]]
vs, fs = [], []
for line in open(path):
    p = line.split()
    if not p:
        continue
    if p[0] == 'v':
        vs.append((float(p[1]), float(p[2]), float(p[3])))
    elif p[0] == 'f':
        fs.append((int(p[1]) - 1, int(p[2]) - 1, int(p[3]) - 1))
S = 1000
im = Image.new('RGB', (S, S), (20, 20, 24))
d = ImageDraw.Draw(im)


def xy(v):
    return ((v[0] + half) / (2 * half) * S, S - (v[2] + half) / (2 * half) * S)


ys = [v[1] for v in vs]
print('verts', len(vs), 'faces', len(fs), 'y', min(ys), max(ys), 'x', min(v[0] for v in vs), max(v[0] for v in vs), 'z', min(v[2] for v in vs), max(v[2] for v in vs))
for f in fs:
    h = sum(vs[i][1] for i in f) / 3
    c = int(70 + min(max(h + 4, 0), 12) * 14)
    d.polygon([xy(vs[i]) for i in f], fill=(c, 120, 240 - c), outline=(255, 255, 255))
for g in range(-int(half), int(half) + 1, 10):
    a, b = xy((g, 0, -half)), xy((g, 0, half))
    d.line([a, b], fill=(70, 70, 70))
    a, b = xy((-half, 0, g)), xy((half, 0, g))
    d.line([a, b], fill=(70, 70, 70))
    d.text(xy((g, 0, 0)), str(g), fill=(255, 255, 0))
    d.text(xy((0, 0, g)), str(g), fill=(255, 255, 0))
for e in extra:
    x, y = xy((e[0], 0, e[1]))
    d.ellipse((x - 6, y - 6, x + 6, y + 6), fill=(255, 40, 40))
im.save(out)
