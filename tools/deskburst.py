# deskburst.py out.jpg x y w h [n=8] [gap seconds=0.05]: grabs the desktop n times in a row (DXGI duplication, HDR aware) and tiles the same crop of each.
# Unlike shot.sh / capture.req this does not go through the game, so it shows what is really on screen from frame to frame (flicker).
# Uses capture_hdr.py (Duplicator / tonemap) from the folder HBG_CAPTURE_DIR in tools/local.env.
import sys, time
import os
sys.path.insert(0, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'local.env'), encoding='utf-8').read().split('HBG_CAPTURE_DIR=')[1].split('\n')[0].strip('"'))  # the folder of capture_hdr.py
import numpy as np
from PIL import Image
import capture_hdr as c

out = sys.argv[1]
x, y, w, h = [float(v) for v in sys.argv[2:6]]
n = int(sys.argv[6]) if len(sys.argv) > 6 else 8
gap = float(sys.argv[7]) if len(sys.argv) > 7 else 0.05
d = c.Duplicator()
frames = []
for i in range(n):
    f = d.grab()
    H, W = f.shape[:2]
    frames.append(np.array(f[int(H * y):int(H * (y + h)), int(W * x):int(W * (x + w)), :3]))
    time.sleep(gap)
tiles = [Image.fromarray(c.tonemap(f, 450, d.hdr)) for f in frames]
cw, ch = tiles[0].size
cols = 4
o = Image.new('RGB', (cw * cols, ch * ((n + cols - 1) // cols)))
for i, t in enumerate(tiles):
    o.paste(t, ((i % cols) * cw, (i // cols) * ch))
if o.width > 1900:
    o = o.resize((1900, int(o.height * 1900 / o.width)))
o.save(out, quality=90)
print(out, o.size, 'hdr', d.hdr)
