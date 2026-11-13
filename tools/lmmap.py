# lmmap.py lmmap_StageNN.bin out.png [gain]: the top-down ground lightmap dump (mod.lmmap) as a picture. +z is up, +x is right; grid lines every 10 m.
import sys, struct
import numpy as np
from PIL import Image, ImageDraw

d = open(sys.argv[1], 'rb').read()
w, h, x0, z0 = struct.unpack_from('<iiff', d, 0)
a = np.frombuffer(d[16:], dtype=np.float32).reshape(h, w, 3)
gain = float(sys.argv[3]) if len(sys.argv) > 3 else 0.5
img = Image.fromarray((np.clip(a * gain, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8)[::-1])
draw = ImageDraw.Draw(img)
for gx in range(int(np.ceil(x0 / 10)) * 10, int(x0 + w * 0.25), 10):
    px = (gx - x0) / 0.25
    draw.line([(px, 0), (px, h)], fill=(70, 70, 70))
    draw.text((px + 2, 2), str(gx), fill=(255, 255, 0))
for gz in range(int(np.ceil(z0 / 10)) * 10, int(z0 + h * 0.25), 10):
    py = h - (gz - z0) / 0.25
    draw.line([(0, py), (w, py)], fill=(70, 70, 70))
    draw.text((2, py + 2), str(gz), fill=(255, 255, 0))
img.save(sys.argv[2])
print(sys.argv[2], img.size, 'x', x0, 'to', x0 + w * 0.25, 'z', z0, 'to', z0 + h * 0.25)
