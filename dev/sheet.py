import glob, os, sys
from PIL import Image
out = sys.argv[1]
start = int(sys.argv[2]) if len(sys.argv) > 2 else 0
count = int(sys.argv[3]) if len(sys.argv) > 3 else 12
cols = int(sys.argv[4]) if len(sys.argv) > 4 else 3
allf = sorted(glob.glob(r"F:/SPT/BepInEx/plugins/HideoutSiege.Dev/shots/*.png"), key=os.path.getmtime)
files = allf[start:][:count]
W, H = 640, 360
rows = (len(files) + cols - 1) // cols
sheet = Image.new('RGB', (W * cols, H * rows))
for i, f in enumerate(files):
    im = Image.open(f).convert('RGB').resize((W, H))
    sheet.paste(im, ((i % cols) * W, (i // cols) * H))
sheet.save(out)
print(len(files), 'shots; total', len(glob.glob(r'F:/SPT/BepInEx/plugins/HideoutSiege.Dev/shots/*.png')))
