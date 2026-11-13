# blushcount.py x y w h [n=24] [gap=0.02]: grabs the desktop n times and prints how red the crop is in each grab (dev helper).
# The cheek blush layer shows as a jump in redness; a stable picture prints near-identical numbers.
import sys, time
import os
sys.path.insert(0, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'local.env'), encoding='utf-8').read().split('HBG_CAPTURE_DIR=')[1].split('\n')[0].strip('"'))  # the folder of capture_hdr.py
import numpy as np
import capture_hdr as c

x, y, w, h = [float(v) for v in sys.argv[1:5]]
n = int(sys.argv[5]) if len(sys.argv) > 5 else 24
gap = float(sys.argv[6]) if len(sys.argv) > 6 else 0.02
d = c.Duplicator()
vals = []
for i in range(n):
    f = d.grab()
    H, W = f.shape[:2]
    p = c.tonemap(np.array(f[int(H * y):int(H * (y + h)), int(W * x):int(W * (x + w)), :3]), 450, d.hdr).astype(np.float32)
    vals.append(float((p[:, :, 0] - p[:, :, 1]).mean()))  # red minus green
    time.sleep(gap)
print(' '.join('%.0f' % v for v in vals), '| min %.0f max %.0f' % (min(vals), max(vals)))
