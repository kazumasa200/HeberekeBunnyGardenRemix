# hdrview.py shots_dir tag out.jpg [x0 x1 y0 y1]: the game's and Remix's HDR dumps of one frame (mod.hdrdump = tag), side by side
# (left game, right Remix; plain sRGB encoding, no tone mapping), optionally cropped. Both are from the same frame, so poses match.
import sys, glob, os, struct
import numpy as np
from PIL import Image


def load(p, flip):
    d = open(p, 'rb').read()
    w, h = struct.unpack_from('<ii', d, 0)
    a = np.frombuffer(d[8:], dtype=np.float16).reshape(h, w, 4)[:, :, :3].astype(np.float32)
    return a[::-1] if flip else a


d, tag, out = sys.argv[1:4]
g = sorted(glob.glob(os.path.join(d, '*_%s_hdr_game.bin' % tag)), key=os.path.getmtime)[-1]
game, remix = load(g, True), load(g.replace('_hdr_game', '_hdr_remix'), False)
h, w = game.shape[:2]
x0, x1, y0, y1 = [float(v) for v in sys.argv[4:8]] if len(sys.argv) >= 8 else (0, 1, 0, 1)
s = (slice(int(y0 * h), int(y1 * h)), slice(int(x0 * w), int(x1 * w)))
both = np.concatenate([game[s], remix[s]], axis=1)
srgb = np.where(both <= 0.0031308, both * 12.92, 1.055 * np.power(np.clip(both, 0, None), 1 / 2.4) - 0.055)
img = Image.fromarray((np.clip(srgb, 0, 1) * 255).astype(np.uint8))
if img.width > 1900:
    img = img.resize((1900, int(img.height * 1900 / img.width)))
img.save(out, quality=88)
print(out, img.size)
