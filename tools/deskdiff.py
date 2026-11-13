# deskdiff.py out.jpg x y w h: grabs the desktop until it has one frame with and one without the cheek blush (redness of the crop),
# then writes frame A | frame B | amplified difference, to see what else differs between the two kinds of frame (dev helper).
import sys, time
import os
sys.path.insert(0, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'local.env'), encoding='utf-8').read().split('HBG_CAPTURE_DIR=')[1].split('\n')[0].strip('"'))  # the folder of capture_hdr.py
import numpy as np
from PIL import Image
import capture_hdr as c

out = sys.argv[1]
x, y, w, h = [float(v) for v in sys.argv[2:6]]
d = c.Duplicator()
lo = hi = None
for i in range(80):
    f = np.array(d.grab()[:, :, :3])
    H, W = f.shape[:2]
    p = c.tonemap(f[int(H * y):int(H * (y + h)), int(W * x):int(W * (x + w))], 450, d.hdr).astype(np.float32)
    r = float((p[:, :, 0] - p[:, :, 1]).mean())
    img = c.tonemap(f, 450, d.hdr)
    if lo is None or r < lo[0]: lo = (r, img)
    if hi is None or r > hi[0]: hi = (r, img)
    if hi[0] - lo[0] > 10 and i > 6: break
    time.sleep(0.02)
a, b = lo[1].astype(np.int16), hi[1].astype(np.int16)
diff = np.clip(np.abs(a - b) * 6, 0, 255).astype(np.uint8)
print('redness low %.0f high %.0f; differing pixels (>8): %.2f%%' % (lo[0], hi[0], 100.0 * (np.abs(a - b).max(axis=2) > 8).mean()))
tiles = [Image.fromarray(lo[1]), Image.fromarray(hi[1]), Image.fromarray(diff)]
tw = 640
th = int(tiles[0].height * tw / tiles[0].width)
o = Image.new('RGB', (tw * 3, th))
for i, t in enumerate(tiles):
    o.paste(t.resize((tw, th)), (i * tw, 0))
o.save(out, quality=90)
