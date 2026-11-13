# cartour.py out_dir Stage02 Stage03 ...: for each stage, finds the cars (Renderers whose path contains "Car"), looks at each from the
# nearest route waypoint, and saves Remix | game side by side (dev helper; the game must be running with the mod).
import subprocess, sys, time, re, math, os
from PIL import Image

D = os.path.dirname(os.path.abspath(__file__))
LOG = open(D + '/local.env', encoding='utf-8').read().split('HBG_DIR=')[1].split('\n')[0].strip('"') + '/BepInEx/LogOutput.log'  # tools/local.env: see local.env.example


def conf(*lines):
    subprocess.run(['sh', D + '/remixconf.sh', *lines], check=True)


def log_from(n):
    return open(LOG, encoding='utf-8', errors='replace').read().split('\n')[n:]


def lines():
    return len(open(LOG, encoding='utf-8', errors='replace').read().split('\n')) - 1  # the last piece is the empty tail


def wait(n, pattern, secs):
    for _ in range(secs * 2):
        time.sleep(0.5)
        hit = [l for l in log_from(n) if re.search(pattern, l)]
        if hit:
            return hit
    return []


def shot():
    p = subprocess.run(['sh', D + '/shot.sh'], capture_output=True, text=True).stdout.strip()
    return Image.open(subprocess.run(['cygpath', '-w', p], capture_output=True, text=True).stdout.strip()).convert('RGB')


out = sys.argv[1]
for stage in sys.argv[2:]:
    n = lines()
    conf('mod.timescale = 1', 'mod.cam = off', 'mod.remix = 1', 'mod.stage = ' + stage)
    if not wait(n, r'\[%s\] メッシュを GPU' % stage, 120) or not wait(n, r'読み戻しが終わりました: Remix のメッシュ [1-9]', 120):
        print(stage, 'did not load'); continue
    time.sleep(8)
    n = lines()
    conf('mod.waypoints = 1', 'mod.find = Car')
    wait(n, 'を含む Renderer', 60)  # written after the waypoints
    time.sleep(2)
    wp = [l for l in log_from(n) if '道順: ' in l]
    way = [[float(v) for v in p.split()] for p in wp[-1].split('道順: ')[1].split(';')] if wp else []
    cars = {}
    for l in log_from(n):
        m = re.match(r'\s+(\S.*?)(（無効）)? ライトマップ .*位置 \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)', l)
        if not m or m.group(2) or 'Wheel' in m.group(1) or 'Ef_' in m.group(1):
            continue
        root = m.group(1).split('/')
        key = '/'.join(root[:next((i for i, s in enumerate(root) if 'Car' in s), len(root) - 1) + 1])
        cars.setdefault(key, [float(m.group(i)) for i in (3, 4, 5)])
    print(stage, len(cars), 'cars,', len(way), 'waypoints', flush=True)
    conf('mod.timescale = 0')
    done = []
    for name, c in cars.items():
        if any(math.dist(c, d) < 3 for d in done):
            continue  # one view per spot (cars parked together)
        done.append(c)
        # from the nearest waypoint that is not on top of the car; shortcut: no visibility test, a wall in between just wastes one view
        cand = sorted((w for w in way if 4 < math.hypot(w[0] - c[0], w[2] - c[2])), key=lambda w: math.hypot(w[0] - c[0], w[2] - c[2]))
        if not cand or math.hypot(cand[0][0] - c[0], cand[0][2] - c[2]) > 25:
            continue
        w = cand[0]
        dx, dz = c[0] - w[0], c[2] - w[2]
        dist = math.hypot(dx, dz)
        k = max(0, dist - 7) / dist  # move in to 7 m
        x, z = w[0] + dx * k, w[2] + dz * k
        conf('mod.remix = 1', 'mod.cam = %.2f, 1.6, %.2f, %.1f, 5' % (x, z, math.degrees(math.atan2(dx, dz))))
        time.sleep(5)
        a = shot()
        conf('mod.remix = 0'); time.sleep(2.5)
        b = shot()
        o = Image.new('RGB', (a.width, a.height // 2))
        o.paste(a.resize((a.width // 2, a.height // 2)), (0, 0)); o.paste(b.resize((a.width // 2, a.height // 2)), (a.width // 2, 0))
        f = '%s/%s_car%02d.jpg' % (out, stage, len(done))
        o.resize((o.width // 2, o.height // 2)).save(f, quality=85)
        print(' ', f, name.split('/')[-1], flush=True)
    conf('mod.remix = 1')
