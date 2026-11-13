# hdrstat.py shots_dir tag x0 x1 y0 y1 [...]: per screen region (fractions, y from top), mean RGB and luminance percentiles
# of the game's and Remix's HDR dumps (mod.hdrdump = tag). The percentile spread is the "texture" (bright facets vs dark facets).
import sys, glob, os, struct
import numpy as np


def load(p, flip):
    d = open(p, 'rb').read()
    w, h = struct.unpack_from('<ii', d, 0)
    a = np.frombuffer(d[8:], dtype=np.float16).reshape(h, w, 4)[:, :, :3].astype(np.float32)
    return a[::-1] if flip else a


def stat(a):
    lum = a @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    p = np.percentile(lum, [10, 50, 90, 99])
    return 'mean %s lum p10 %.3f p50 %.3f p90 %.3f p99 %.3f' % (a.mean(0).round(3), *p)


d, tag = sys.argv[1], sys.argv[2]
g = sorted(glob.glob(os.path.join(d, '*_%s_hdr_game.bin' % tag)), key=os.path.getmtime)[-1]
game, remix = load(g, True), load(g.replace('_hdr_game', '_hdr_remix'), False)
h, w = game.shape[:2]
v = [float(x) for x in sys.argv[3:]]
for i in range(0, len(v), 4):
    x0, x1, y0, y1 = v[i:i + 4]
    s = (slice(int(y0 * h), int(y1 * h)), slice(int(x0 * w), int(x1 * w)))
    print('%s (%.2f-%.2f, %.2f-%.2f)\n  game  %s\n  remix %s' % (tag, x0, x1, y0, y1, stat(game[s].reshape(-1, 3)), stat(remix[s].reshape(-1, 3))))
