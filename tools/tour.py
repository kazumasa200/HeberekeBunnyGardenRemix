# tour.py cams "x y z; x y z; ..." max   -> lines "index x, y, z, yaw, pitch": eye height at each waypoint, looking at the next one
# tour.py sheet shots_dir name out.jpg  -> contact sheet (Remix | game | heat of log2(Remix/game)) and per-view stats for *_name-N_hdr_*.bin
import sys, math, glob, struct, re


def cams(route, limit):
    pts = [tuple(float(v) for v in p.split()) for p in route.split(';')]
    # 近すぎる目印は飛ばす（同じ眺めになる）
    keep = [pts[0]]
    for p in pts[1:]:
        if math.dist(p, keep[-1]) > 6: keep.append(p)
    step = max(1, math.ceil((len(keep) - 1) / limit))
    for n, i in enumerate(range(0, len(keep) - 1, step)):
        a, b = keep[i], keep[i + 1]
        yaw = math.degrees(math.atan2(b[0] - a[0], b[2] - a[2])) % 360
        print(f"{n} {a[0]:.2f}, {a[1] + 1.6:.2f}, {a[2]:.2f}, {yaw:.1f}, 6")


def sheet(shots, name, out):
    import numpy as np
    from PIL import Image

    def load(p, flip):
        d = open(p, 'rb').read()
        w, h = struct.unpack_from('<ii', d, 0)
        a = np.frombuffer(d[8:], dtype=np.float16).reshape(h, w, 4)[:, :, :3].astype(np.float32)
        return a[::-1] if flip else a

    lum = lambda a: (a * [0.2126, 0.7152, 0.0722]).sum(-1)
    show = lambda a: Image.fromarray((np.clip(a * 1.5, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8)).resize((640, 360))
    views = {}
    for p in glob.glob(f"{shots}/*_{name}-*_hdr_game.bin"):
        views[int(re.search(rf"_{re.escape(name)}-(\d+)_hdr_game", p).group(1))] = p  # 同じ番号は新しい物が残る（glob は名前順 = 時刻順）
    rows = []
    for i in sorted(views):
        g = load(views[i], True)
        r = load(views[i].replace('_hdr_game', '_hdr_remix'), False)
        ratio = np.log2(np.maximum(lum(r), 1e-3) / np.maximum(lum(g), 1e-3))
        c = np.clip(ratio / 3, -1, 1)  # 赤 = Remix が明るい、青 = 暗い（±3 段で振り切る）
        heat = Image.fromarray((np.stack([np.clip(c, 0, 1), np.zeros_like(c) + 0.15, np.clip(-c, 0, 1)], -1) * 255).astype(np.uint8)).resize((640, 360))
        rows.append((show(r), show(g), heat))
        q = np.percentile(ratio, [5, 50, 95])
        print(f"{name}-{i}: median {q[1]:+.2f}  5% {q[0]:+.2f}  95% {q[2]:+.2f}  mean remix {lum(r).mean():.3f} game {lum(g).mean():.3f}  |ratio|>1.5: {100 * (abs(ratio) > 1.5).mean():.0f}%")
    if not rows: sys.exit("no dumps")
    img = Image.new('RGB', (1920, 360 * len(rows)))
    for y, row in enumerate(rows):
        for x, im in enumerate(row): img.paste(im, (640 * x, 360 * y))
    img.save(out, quality=85)
    print(out, "(Remix | game | heat)")


if sys.argv[1] == 'cams': cams(sys.argv[2], int(sys.argv[3]))
else: sheet(*sys.argv[2:5])
