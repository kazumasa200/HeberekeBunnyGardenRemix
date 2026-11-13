using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 焼き込みにだけある明かりを、Remix の本物の光源（点光源・スポットライト）に置き換える。このゲームの通りの照明は、焼いたあとで Light を消していて、
/// 壁・看板・木の明かりはライトマップにしか無い（Remix はライトマップを使わないので、その光が無かった）。
/// 1. 場面に入ったとき、照明の素材（光る素材で、名前に light / lamp）を持つ Renderer の周りを覚える（<see cref="FindLamps"/>）
/// 2. メッシュを読み戻すとき、照明の素材の面を島（つながった三角形）に分けて 1 灯ずつの照明にし（<see cref="AddEmitter"/>）、
///    その周りのライトマップのある面を、ワールド座標とライトマップの UV で取っておく（<see cref="AddSurface"/>）
/// 3. 全部そろったら、ライトマップを読み戻し、別のスレッドで、照明から視線を飛ばして当たった面のライトマップの値を読み、
///    「値 = 周りの明るさ + 強さ × cos / 距離²」（URP の点光源・スポットの式）に最小二乗で当てはめる（<see cref="Solve"/>）
///    ・面に付いた照明（壁・天井・足元）: 点光源。面から前へ出す距離も残差で選ぶ
///    ・面から離れた照明（看板を照らす器具など）: 明るい向きへ向けたスポットライト。広がりも明るさの分布から決める
///    ・木: 器具は無いが、ゲームの幹は根元から照らされている。根元の周りの 8 か所に上向きのスポットを仮に置き、幹と葉のライトマップにいちばん合う 1 つを選ぶ
/// 照明の無い看板（LED の看板）だけは、ライトマップの明るさで地の絵を光らせる（<see cref="BoardLight"/>）
/// </summary>
internal sealed partial class BakedLamps
{
    internal struct Result
    {
        public string Name;
        public int Id;
        public Vector3 Pos, Axis;
        public float Radius, Outer, Inner; // Outer > 0 ならスポット（半角、度）
        public float Area;                 // > 0 なら面光源（Axis の向きに光る、面積 m²）。場面にもとからある光を、補助光の計算で引くときだけ使う
        public Color Intensity;
        // 当てはめに使った点（場所・面の向き・周りの明るさを引いたライトマップの明るさ）。光源どうしの取り合いを直すのに使う（Balance）
        public List<(Vector3 P, Vector3 N, float E)> Samples;
    }

    private sealed class Lamp
    {
        public int Id, Tree; // Tree: 木の Renderer の ID（0 は器具のある照明）
        public string Name;
        public Vector3 Center;
        public float Size;
    }

    private sealed class Surface
    {
        public int RendererId, Lightmap;
        // 切り抜きの素材（木の葉の板）。焼くときは抜けた所を光が通るが、ここでは板の形しか分からないので、光を遮る物としては数えない
        // （遮ると数えていた頃は、木の下が「空の光が届かないのに明るい」ことになり、空の色の偽の光源が木ごとに置かれた）
        public bool Cutout;
        public Material Material; // 切り抜きの素材（別のスレッドでは、区別に使うだけで中は読まない）
        public int Cull = 2;      // その素材の _Cull（0: 両面を描く、1: 裏だけ描く、2: 表だけ描く）。木の葉は 1 だった
        public bool Leaf;         // 木の葉（MaterialLibrary.IsLeaf）: Renderer ごとに地の色の倍率を決める
        public Vector3[] Pos;
        public Vector2[] Uv;
        public int[] Idx;
        public Vector3 Min, Max;
    }

    private struct Tri
    {
        public Vector3 A, E1, E2;
        public Surface S;
        public int I;
    }

    private struct Hit
    {
        public Vector3 P, N, D;
        public float T;
        public Color L;
        public int Renderer;
    }

    // Reach: 面に付いた照明が照らす範囲。FarReach: 面から離れた照明（高い街灯は灯具が地面から 7 m 上）
    private const float Reach = 6f, FarReach = 14f, MinLamp = 0.03f, Mounted = 0.3f;

    private readonly ManualLogSource _log;
    private readonly List<Lamp> _lamps = new();
    private readonly List<Surface> _surfaces = new();
    private readonly List<Bounds> _regions = new();
    private readonly List<Vector3> _lights = new();
    // 看板の絵の面（素材の名前に billboard か signboard）: 素材、ライトマップ、三角形ごとの場所・ライトマップの UV・面の向き・面積
    private readonly List<(Material material, int renderer, int lightmap, List<(Vector3 p, Vector2 uv, Vector3 n, float area)> texels)> _boards = new();

    /// <summary>看板の絵の素材か（照明の素材は除く）。</summary>
    public static bool IsBoardMaterial(Material m, string rendererName) =>
        m != null && (Has(m.name, "billboard") || Has(m.name, "signboard")) && !IsLampMaterial(m, rendererName);

    public BakedLamps(ManualLogSource log) => _log = log;

    public int Count => _lamps.Count;

    /// <summary>照明の素材か: 光っていて、素材か Renderer の名前に light / lamp を含む。店の天井の器具（Ceilinglight）は SceneGeometry.CollectFixtures が受け持つ。</summary>
    public static bool IsLampMaterial(Material m, string rendererName)
    {
        if (m == null || !m.IsKeywordEnabled("_EMISSION") || !m.HasProperty("_EmissionColor") || m.GetColor("_EmissionColor").maxColorComponent <= 0.01f) return false;
        if (Has(m.name, "Ceilinglight")) return false;
        return Has(m.name, "light") || Has(m.name, "lamp") || Has(rendererName, "light") || Has(rendererName, "lamp");
    }

    private static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>メインスレッド。場面に入ったとき: 照明の素材を持つ Renderer の周り・看板・木・いまある Light の場所を覚える。</summary>
    public void FindLamps(HashSet<Renderer> coarse)
    {
        _lamps.Clear();
        Sky = null;
        _boards.Clear();
        _regions.Clear();
        _lights.Clear();
        lock (_surfaces) _surfaces.Clear();
        if (Cached) return;
        _lights.AddRange(Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(l => l.gameObject.activeInHierarchy).Select(l => l.transform.position));
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || coarse.Contains(r)) continue;
            var mats = r.sharedMaterials;
            bool lightmapped = r.lightmapIndex >= 0 && r.lightmapIndex < LightmapSettings.lightmaps.Length;
            bool lamp = mats.Any(m => IsLampMaterial(m, r.name));
            // 看板は、絵の面のライトマップを読むので、自分の面を取っておく
            if (lightmapped && mats.Any(m => IsBoardMaterial(m, r.name))) _regions.Add(r.bounds);
            // 木（SM_tree）: 根元に上向きのスポットを仮に置く。幹は箱の真ん中、根元は箱の下
            if (lightmapped && Has(r.name, "tree") && mats.Any(m => m != null && Has(m.name, "bark")))
            {
                var b = r.bounds;
                _lamps.Add(new Lamp { Id = unchecked(r.GetInstanceID() * 397), Tree = r.GetInstanceID(), Name = "木/" + r.name, Center = new Vector3(b.center.x, b.min.y, b.center.z) });
                lamp = true;
            }
            if (!lamp) continue;
            var region = r.bounds;
            region.Expand(2f * FarReach);
            _regions.Add(region);
        }
    }

    /// <summary>この箱の面を取っておくか（照明の届く範囲にあるか）。</summary>
    public bool Wants(Bounds b) => !Cached && _regions.Any(r => r.Intersects(b));

    /// <summary>この場面は、保存してある結果（SceneGeometry の lamps_〜.txt）を使う。照明の候補もライトマップの面も集めない。</summary>
    public bool Cached;

    /// <summary>
    /// メインスレッド（メッシュの読み戻し）。照明の素材の面（ワールド座標）を、つながった三角形の島に分け、近い島（0.4 m 以内）をまとめて 1 灯にする。
    /// 看板の照明は、建物や看板のメッシュの中に器具がいくつも入っているので、Renderer 単位では 1 灯ずつにならない。
    /// 差し渡し 2 m を超える物（光る壁など）と、0.75 m 以内に Light がある物（自分の Light を持つ照明）は外す
    /// </summary>
    public void AddEmitter(int rendererId, string name, Vector3[] world, int[] idx)
    {
        // 同じ場所の頂点（UV の切れ目で分かれた物）をまとめてから、三角形でつなぐ
        var weld = new Dictionary<(int, int, int), int>();
        var parent = new int[world.Length];
        for (int i = 0; i < world.Length; i++)
        {
            var key = (Mathf.RoundToInt(world[i].x * 500f), Mathf.RoundToInt(world[i].y * 500f), Mathf.RoundToInt(world[i].z * 500f));
            parent[i] = weld.TryGetValue(key, out int first) ? first : weld[key] = i;
        }
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int a = Find(idx[i]);
            parent[Find(idx[i + 1])] = a;
            parent[Find(idx[i + 2])] = a;
        }
        var islands = new Dictionary<int, (Vector3 min, Vector3 max)>();
        foreach (int v in idx.Distinct())
        {
            int root = Find(v);
            islands[root] = islands.TryGetValue(root, out var box) ? (Vector3.Min(box.min, world[v]), Vector3.Max(box.max, world[v])) : (world[v], world[v]);
        }
        var clusters = new List<(Vector3 min, Vector3 max)>();
        foreach (var box in islands.Values)
        {
            var center = (box.min + box.max) * 0.5f;
            int at = clusters.FindIndex(c => ((c.min + c.max) * 0.5f - center).sqrMagnitude < 0.4f * 0.4f);
            if (at < 0) clusters.Add(box);
            else clusters[at] = (Vector3.Min(clusters[at].min, box.min), Vector3.Max(clusters[at].max, box.max));
        }
        for (int k = 0; k < clusters.Count; k++)
        {
            var center = (clusters[k].min + clusters[k].max) * 0.5f;
            float size = (clusters[k].max - clusters[k].min).magnitude;
            if (size > 2f || _lights.Any(p => (p - center).sqrMagnitude < 0.75f * 0.75f)) continue;
            lock (_lamps) _lamps.Add(new Lamp { Id = unchecked(rendererId * 397 + k + 1), Name = name, Center = center, Size = size });
        }
    }

    /// <summary>
    /// メインスレッド（メッシュの読み戻し）。看板の絵の面（その素材のサブメッシュ）を覚える。Renderer のライトマップの範囲には看板の裏や枠も入っているので、
    /// 範囲の平均では絵の面の明るさにならなかった（赤いカラオケの看板: 範囲の平均 0.23、ゲームの絵の面は 0.85）
    /// </summary>
    public void AddBoardFace(Material material, int renderer, int lightmap, Vector4 st, Vector3[] world, Vector2[] uv, int[] idx)
    {
        if (idx.Length < 3) return;
        var texels = new List<(Vector3, Vector2, Vector3, float)>();
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            Vector3 a = world[idx[i]], b = world[idx[i + 1]], c = world[idx[i + 2]];
            // Unity は表から見て時計回り。左手系なので cross(b - a, c - a) が表の向き
            var cross = Vector3.Cross(b - a, c - a);
            float area = cross.magnitude * 0.5f;
            if (area < 1e-8f) continue;
            var center = (uv[idx[i]] + uv[idx[i + 1]] + uv[idx[i + 2]]) / 3f;
            texels.Add(((a + b + c) / 3f, new Vector2(center.x * st.x + st.z, center.y * st.y + st.w), cross.normalized, area));
        }
        if (texels.Count > 0) _boards.Add((material, renderer, lightmap, texels));
    }

    /// <summary>メインスレッド（メッシュの読み戻し）。ライトマップのある面を、ワールド座標とライトマップの UV で取っておく。</summary>
    public void AddSurface(int rendererId, int lightmap, Vector4 st, Matrix4x4 toWorld, Vector3[] pos, Vector2[] uv, int[] idx, Material cutout, bool leaf)
    {
        if (pos.Length == 0) return;
        var world = new Vector3[pos.Length];
        var lm = new Vector2[pos.Length];
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        for (int i = 0; i < pos.Length; i++)
        {
            world[i] = toWorld.MultiplyPoint3x4(pos[i]);
            lm[i] = new Vector2(uv[i].x * st.x + st.z, uv[i].y * st.y + st.w);
            min = Vector3.Min(min, world[i]);
            max = Vector3.Max(max, world[i]);
        }
        lock (_surfaces) _surfaces.Add(new Surface { RendererId = rendererId, Lightmap = lightmap, Pos = world, Uv = lm, Idx = idx, Min = min, Max = max, Cutout = cutout != null, Material = cutout, Leaf = leaf,
            Cull = cutout != null && cutout.HasProperty("_Cull") ? (int)cutout.GetFloat("_Cull") : 2 });
    }

    /// <summary>
    /// メインスレッド。ライトマップ（色と向き）を読み戻してから、別のスレッドで照明の強さを決め、done に光源を返す。
    /// boardsDone には、近くに照明の無い看板の明るさを返す。場面が変わっていたら（stillCurrent が false）何もしない。
    /// </summary>
    public void Solve(List<(Vector3 forward, Color radiance)> suns, (Color up, Color side, Color down) ambient, List<Result> known, Vector3[] probes,
        Func<bool> stillCurrent, Action<List<Result>> done, Action<List<(Material material, int renderer, Color lightmap, float lit)>> boardsDone)
    {
        if (_lamps.Count == 0 && _boards.Count == 0) return;
        List<Surface> surfaces;
        lock (_surfaces) surfaces = _surfaces.ToList();
        var boards = _boards.ToList();
        var used = surfaces.Select(s => s.Lightmap).Concat(boards.Select(b => b.lightmap)).Distinct().ToList();
        var maps = new Dictionary<int, (int w, int h, byte[] color, byte[] dir)>();
        int pending = used.Count * 2;
        foreach (var index in used)
        {
            var data = LightmapSettings.lightmaps[index];
            ReadBack(data.lightmapColor, RenderTextureFormat.ARGBHalf, TextureFormat.RGBAHalf, bytes =>
            {
                maps[index] = (data.lightmapColor.width, data.lightmapColor.height, bytes, maps.TryGetValue(index, out var m) ? m.dir : null);
                Next();
            });
            if (data.lightmapDir != null)
                ReadBack(data.lightmapDir, RenderTextureFormat.ARGB32, TextureFormat.RGBA32, bytes =>
                {
                    maps[index] = maps.TryGetValue(index, out var m) ? (m.w, m.h, m.color, bytes) : (data.lightmapDir.width, data.lightmapDir.height, null, bytes);
                    Next();
                });
            else Next();
        }

        void Next()
        {
            if (--pending > 0) return;
            if (!stillCurrent()) return;
            List<Lamp> lamps;
            lock (_lamps) lamps = _lamps.ToList();
            Task.Run(() =>
            {
                var results = new List<Result>();
                var bvh = new Bvh(surfaces);
                _lastFar = null;
                Far far = null;
                try
                {
                    // 空の光: 屋上で測れたら、その値から場面の焼き込みの平行光源の分を引く。測れなければ、ゲームの環境光の値
                    if (MeasureSky(surfaces, maps, lamps, bvh, suns, probes) is { } sky)
                    {
                        Sky = sky;
                        ambient = (sky.up, sky.side, ambient.down);
                    }
                    far = new Far(bvh, suns, ambient);
                }
                catch (Exception e) { _log.LogWarning($"空の光を測れません: {e}"); }
                if (LogFarField) try { _log.LogInfo(FarField(surfaces, maps, lamps)); } catch (Exception e) { _log.LogWarning($"遠くの光を測れません: {e}"); }
                try
                {
                    var stats = new int[3];
                    Parallel.ForEach(lamps, lamp =>
                    {
                        var r = lamp.Tree != 0 ? FitTree(lamp, bvh, far, maps, stats) : FitLamp(lamp, bvh, far, maps, stats);
                        if (r is { } ok) lock (results) results.Add(ok);
                    });
                    Balance(results);
                    int before = results.Count;
                    results.RemoveAll(r => r.Intensity.grayscale < MinLamp);
                    LastStats = $"三角形 {bvh.Triangles} 個、当たりが少ない {stats[0]}、当てはまらない {stats[1]}、弱い {stats[2]}、ほかの光源で足りていた {before - results.Count}";
                    if (far != null && Fills) results.AddRange(Fill(probes, surfaces, bvh, far, maps, results, known));
                    Shades = far != null ? Shade(surfaces, bvh, far, maps, results.Concat(known).ToList()) : new List<(Material, int, float)>();
                }
                catch (Exception e) { _log.LogWarning($"焼き込みの照明の強さを決められません: {e}"); }
                _lastMaps = maps;
                _lastSurfaces = surfaces;
                _lastBvh = bvh;
                _lastFar = far;
                _lastResults = results;
                if (stillCurrent()) done(results);
                try
                {
                    if (stillCurrent()) boardsDone(BoardLight(boards, maps, results));
                }
                catch (Exception e) { _log.LogWarning($"看板のライトマップを読めません: {e}"); }
            });
        }
    }

    /// <summary>
    /// 開発用: 照明から離れた面のライトマップの値を、面の向き（上・下・横 8 方位）ごとに、面積で重みを付けた中央と上から 1 割の値で書く。
    /// 焼いたときの空と平行光源の強さ（場面から消されている物も）を見るため。
    /// </summary>
    private static string FarField(List<Surface> surfaces, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Lamp> lamps)
    {
        var bins = new List<(Color l, float area)>[40];
        for (int i = 0; i < bins.Length; i++) bins[i] = new List<(Color, float)>();
        foreach (var (p, n, area, l) in FarTriangles(surfaces, maps, lamps))
        {
            int bin = n.y > 0.9f ? 0 : n.y < -0.9f ? 1 : Mathf.Abs(n.y) < 0.3f ? 2 + Mathf.RoundToInt(Mathf.Atan2(n.x, n.z) / (Mathf.PI / 4f) + 8) % 8 : -1;
            if (bin >= 0) bins[bin + 10 * (p.y < 3f ? 0 : p.y < 10f ? 1 : p.y < 20f ? 2 : 3)].Add((l, area));
        }
        var names = new[] { "上", "下", "横 +z", "横 +x+z", "横 +x", "横 +x-z", "横 -z", "横 -x-z", "横 -x", "横 -x+z" };
        var bands = new[] { "〜3 m", "3〜10 m", "10〜20 m", "20 m〜" };
        var text = new System.Text.StringBuilder("照明から離れた面のライトマップ（向きごと: 面積、中央の値、上から 1 割の値）:");
        for (int k = 0; k < bins.Length; k++)
        {
            float total = bins[k].Sum(x => x.area);
            if (total <= 0f) continue;
            Color At(float q)
            {
                var c = new Color(0, 0, 0, 1);
                for (int ch = 0; ch < 3; ch++)
                {
                    float acc = 0f;
                    foreach (var x in bins[k].OrderBy(x => x.l[ch])) { acc += x.area; if (acc >= total * q) { c[ch] = x.l[ch]; break; } }
                }
                return c;
            }
            Color mid = At(0.5f), high = At(0.9f);
            text.Append($"\n  {bands[k / 10]} {names[k % 10]}: {total:0} m²、({mid.r:0.###}, {mid.g:0.###}, {mid.b:0.###})、({high.r:0.###}, {high.g:0.###}, {high.b:0.###})");
        }
        return text.ToString();
    }

    /// <summary>照明（候補も）から 4〜8 m 以上離れた三角形の、重心・向き・面積・ライトマップの値。</summary>
    private static IEnumerable<(Vector3 p, Vector3 n, float area, Color l)> FarTriangles(List<Surface> surfaces, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Lamp> lamps)
    {
        var cells = new HashSet<(int, int, int)>();
        foreach (var l in lamps)
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                cells.Add((Mathf.FloorToInt(l.Center.x / 4f) + dx, Mathf.FloorToInt(l.Center.y / 4f) + dy, Mathf.FloorToInt(l.Center.z / 4f) + dz));
        foreach (var s in surfaces)
        {
            if (!maps.TryGetValue(s.Lightmap, out var map) || map.color == null) continue;
            for (int i = 0; i + 2 < s.Idx.Length; i += 3)
            {
                Vector3 a = s.Pos[s.Idx[i]], b = s.Pos[s.Idx[i + 1]], c = s.Pos[s.Idx[i + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                float area = cross.magnitude * 0.5f;
                if (area < 0.02f) continue;
                var p = (a + b + c) / 3f;
                if (cells.Contains((Mathf.FloorToInt(p.x / 4f), Mathf.FloorToInt(p.y / 4f), Mathf.FloorToInt(p.z / 4f)))) continue;
                var n = cross.normalized;
                yield return (p, n, area, Sample(map, (s.Uv[s.Idx[i]] + s.Uv[s.Idx[i + 1]] + s.Uv[s.Idx[i + 2]]) / 3f, n));
            }
        }
    }

    /// <summary>
    /// 焼いたときの空の光（上向きの面と横向きの面が、遮る物なしで受ける分。場面の焼き込みの平行光源の分は引いてある）。なければ null。
    /// SceneGeometry が、これで環境光を作り直す。
    /// RenderSettings の環境光（Trilight）から計算した値は、ステージによっては焼いた値と合わない（Stage09 の屋上: 緑 0.87、計算は 0.15。Remix の絵が青紫に寄って暗かった）
    /// </summary>
    public (Color up, Color side)? Sky { get; private set; }

    // 空が開けているかを見る向き: 真上と、仰角 45° の 8 方位
    private static readonly Vector3[] SkyDirs = new[] { Vector3.up }.Concat(Enumerable.Range(0, 8).Select(k =>
        new Vector3(Mathf.Cos(k * Mathf.PI / 4f), 1f, Mathf.Sin(k * Mathf.PI / 4f)).normalized)).ToArray();

    /// <summary>
    /// 空が開けている面（見える側の空の向きに、遮る物が 1 つも無い面）のライトマップの値の、面積で重みを付けた中央の値。照明の近くと、遊ぶ範囲の外は使わない。
    /// ・上: 平行光源（suns）も全部当たっている上向きの面。その分を引く
    /// ・横: 平行光源が当たらない向きの横向きの面。場面に平行光源が無ければ（焼いたあとで消してある）、4 方位のうち暗い 2 つの平均
    /// 「高い面ほど屋上」と決めていた頃は、遊ぶ範囲を囲む背景の高い筒（Stage02）に引っぱられて 0 になった。遊ぶ範囲は、ライトプローブの近く（40 m の升目）で決める。
    /// </summary>
    private static (Color up, Color side)? MeasureSky(List<Surface> surfaces, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Lamp> lamps,
        Bvh bvh, List<(Vector3 forward, Color radiance)> suns, Vector3[] probes)
    {
        var play = new HashSet<(int, int)>();
        foreach (var p in probes)
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) play.Add((Mathf.FloorToInt(p.x / 40f) + dx, Mathf.FloorToInt(p.z / 40f) + dz));
        var stack = new int[128];
        bool Open(Vector3 p, Vector3 n)
        {
            var from = p + n * 0.05f;
            foreach (var d in SkyDirs)
                if (Vector3.Dot(n, d) > 0.1f && bvh.Closest(from, d, 0.02f, 300f, stack, out _, out _, out _, out _, solidOnly: true)) return false;
            return true;
        }
        var roofs = new List<(Color l, float area)>();
        var walls = new List<(Color l, float area)>[4];
        for (int k = 0; k < 4; k++) walls[k] = new List<(Color, float)>();
        var quadrants = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        foreach (var t in FarTriangles(surfaces, maps, lamps))
        {
            if (t.area < 0.25f || (play.Count > 0 && !play.Contains((Mathf.FloorToInt(t.p.x / 40f), Mathf.FloorToInt(t.p.z / 40f))))) continue;
            if (t.n.y > 0.9f)
            {
                if (!Open(t.p, t.n)) continue;
                var from = t.p + t.n * 0.05f;
                var light = t.l;
                bool lit = true;
                foreach (var (forward, radiance) in suns)
                {
                    float cos = -Vector3.Dot(t.n, forward);
                    if (cos <= 0f) continue;
                    if (bvh.Closest(from, -forward, 0.02f, 300f, stack, out _, out _, out _, out _, solidOnly: true)) { lit = false; break; }
                    light -= radiance * cos;
                }
                if (lit) roofs.Add((Max0(light), t.area));
            }
            else if (Mathf.Abs(t.n.y) < 0.3f)
            {
                int q = Array.FindIndex(quadrants, d => Vector3.Dot(t.n, d) > 0.7f);
                if (q < 0 || suns.Any(sun => Vector3.Dot(t.n, sun.forward) < -0.05f) || !Open(t.p, t.n)) continue;
                walls[q].Add((t.l, t.area));
            }
        }
        Color? Median(List<(Color l, float area)> list, float minArea)
        {
            float total = list.Sum(t => t.area);
            if (total < minArea) return null;
            var c = new Color(0f, 0f, 0f, 1f);
            for (int ch = 0; ch < 3; ch++)
            {
                float sum = 0f;
                foreach (var t in list.OrderBy(t => t.l[ch])) { sum += t.area; if (sum >= total * 0.5f) { c[ch] = t.l[ch]; break; } }
            }
            return c;
        }
        var up = Median(roofs, 100f);
        var sides = walls.Select(w => Median(w, 50f)).Where(c => c != null).Select(c => c.Value).OrderBy(c => c.grayscale).ToList();
        if (up == null || sides.Count == 0) return null;
        // 平行光源が当たらない向きだけを集めてあれば全部の平均。平行光源が場面に無ければ、暗い 2 つ（焼いたときの平行光源が当たっていない側）
        var side = suns.Count > 0 ? sides.Aggregate((a, b) => a + b) / sides.Count : sides.Take(2).Aggregate((a, b) => a + b) / Mathf.Min(sides.Count, 2);
        LastSky = $"屋上 {roofs.Sum(t => t.area):0} m²、壁 {string.Join(" / ", walls.Select(w => $"{w.Sum(t => t.area):0}"))} m²";
        return (up.Value, side);
    }

    /// <summary>空の光を測った面の広さ（ログ用）。</summary>
    public static string LastSky = "";

    private static void ReadBack(Texture tex, RenderTextureFormat rtFormat, TextureFormat format, Action<byte[]> done)
    {
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, rtFormat, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        AsyncGPUReadback.Request(rt, 0, format, req =>
        {
            RenderTexture.ReleaseTemporary(rt);
            using (Timing.Measure("ライトマップの写し")) done(req.hasError ? null : req.GetData<byte>().ToArray());
        });
    }

    // ---- 別のスレッド（Unity の API は使わない） ----

    /// <summary>当てはめで外れた理由の数（ログ用）。</summary>
    public static string LastStats = "";

    /// <summary>開発用（mod.farfield = 1）: 次に場面を読むとき、照明から離れた面のライトマップの値を向きと高さごとに書く。</summary>
    public static bool LogFarField;

    /// <summary>
    /// 開発用（mod.lmmap）: 上向きの面（高さ maxY まで）のライトマップの値を、上から見た地図にして書く（1 画素 0.25 m。先頭に幅・高さ・左下の x と z、続けて float の RGB）。
    /// 重なった所は低い面を残す。焼いたあとで消された光の形（丸い光だまり・一様な明るさ）を見るため。
    /// </summary>
    public string DumpGroundMap(string path, float maxY)
    {
        if (_lastMaps == null) return "ライトマップをまだ読んでいません";
        const float cell = 0.25f;
        var up = new List<(Surface s, int i)>();
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (var s in _lastSurfaces)
            for (int i = 0; i + 2 < s.Idx.Length; i += 3)
            {
                Vector3 a = s.Pos[s.Idx[i]], b = s.Pos[s.Idx[i + 1]], c = s.Pos[s.Idx[i + 2]];
                if (Vector3.Cross(b - a, c - a).normalized.y < 0.7f || a.y > maxY) continue;
                up.Add((s, i));
                min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            }
        int w = Mathf.CeilToInt((max.x - min.x) / cell) + 1, h = Mathf.CeilToInt((max.z - min.z) / cell) + 1;
        var rgb = new float[w * h * 3];
        var height = new float[w * h];
        for (int k = 0; k < height.Length; k++) height[k] = float.MaxValue;
        foreach (var (s, i) in up)
        {
            if (!_lastMaps.TryGetValue(s.Lightmap, out var map) || map.color == null) continue;
            Vector3 a = s.Pos[s.Idx[i]], b = s.Pos[s.Idx[i + 1]], c = s.Pos[s.Idx[i + 2]];
            Vector2 ua = s.Uv[s.Idx[i]], ub = s.Uv[s.Idx[i + 1]], uc = s.Uv[s.Idx[i + 2]];
            float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(det) < 1e-9f) continue;
            int x0 = Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - min.x) / cell), x1 = Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - min.x) / cell);
            int z0 = Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - min.z) / cell), z1 = Mathf.CeilToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - min.z) / cell);
            for (int z = z0; z <= z1 && z < h; z++)
                for (int x = x0; x <= x1 && x < w; x++)
                {
                    float px = min.x + x * cell, pz = min.z + z * cell;
                    float wa = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / det, wb = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / det, wc = 1f - wa - wb;
                    if (wa < -0.01f || wb < -0.01f || wc < -0.01f) continue;
                    float y = wa * a.y + wb * b.y + wc * c.y;
                    int k = z * w + x;
                    if (y >= height[k]) continue;
                    height[k] = y;
                    var l = Sample(map, ua * wa + ub * wb + uc * wc, Vector3.up);
                    rgb[k * 3] = l.r; rgb[k * 3 + 1] = l.g; rgb[k * 3 + 2] = l.b;
                }
        }
        using (var file = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            file.Write(w); file.Write(h); file.Write(min.x); file.Write(min.z);
            var bytes = new byte[rgb.Length * 4];
            Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
            file.Write(bytes);
        }
        return $"地面のライトマップの地図を書きました: {path}（{w} × {h}、左下 x {min.x:0.#} z {min.z:0.#}、1 画素 {cell} m）";
    }

    private Dictionary<int, (int w, int h, byte[] color, byte[] dir)> _lastMaps;
    private List<Surface> _lastSurfaces;
    private Bvh _lastBvh;
    private Far _lastFar;
    private List<Result> _lastResults;

    /// <summary>
    /// 開発用（mod.lmpick）: 視線が最初に当たるライトマップの面（6 m まで）の、場所・向き・ライトマップの値と、置いた光源がそこへ届ける予想（強い順、遮りは見ない）。
    /// </summary>
    public string Probe(Vector3 origin, Vector3 direction)
    {
        if (_lastMaps == null) return "ライトマップをまだ読んでいません";
        var hits = Cast(origin, new[] { direction.normalized }, _lastBvh, _lastMaps, 30f);
        var farLight = hits.Count > 0 && _lastFar != null ? _lastFar.At(hits[0].P, hits[0].N, new int[128]) : default;
        if (hits.Count == 0) return $"{origin} から {direction} の向きに、30 m までライトマップの面がありません";
        var h = hits[0];
        var lit = _lastResults.Select(r => (r, v: Lit(r, h.P, h.N))).Where(x => x.v > 1e-3f).OrderByDescending(x => x.v).ToList();
        return $"ライトマップの面: 場所 {h.P}、向き {h.N}、値 ({h.L.r:0.###}, {h.L.g:0.###}, {h.L.b:0.###})、空と平行光源の予想 ({farLight.r:0.###}, {farLight.g:0.###}, {farLight.b:0.###})。置いた光源の予想の合計 {lit.Sum(x => x.v):0.###}" +
               string.Concat(lit.Take(6).Select(x => $"\n    {x.r.Name}: {x.v:0.###}（{Vector3.Distance(x.r.Pos, h.P):0.0} m、強さ {x.r.Intensity.grayscale:0.##}{(x.r.Outer > 0f ? $"、スポット {x.r.Outer:0}°" : "")}）"));
    }

    private static readonly Vector3[] Sphere = Fibonacci(160);

    private static Result? FitLamp(Lamp lamp, Bvh bvh, Far far, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, int[] stats)
    {
        var hits = Cast(lamp.Center, Sphere, bvh, maps, Reach, far);
        if (hits.Count < 12) { lock (stats) stats[0]++; return null; }
        var groups = Groups(hits);
        int nearest = 0;
        for (int i = 1; i < hits.Count; i++) if (hits[i].T < hits[nearest].T) nearest = i;
        float radius = Mathf.Clamp(lamp.Size * 0.4f, 0.05f, 0.3f); // 光る球は部品を覆う大きさ（部品自身が光を遮らないように）
        var result = new Result { Name = lamp.Name, Id = lamp.Id, Radius = radius };

        if (hits[nearest].T < Mounted)
        {
            // 面に付いた照明: 点光源。焼いたときの光源は部品より前にあったらしい（部品の位置だと、すぐ周りだけが強く光り、ゲームの広く柔らかい明かりにならない）ので、
            // 面から前へ出す距離も、残差がいちばん小さくなるように選ぶ
            float best = float.MaxValue;
            foreach (float offset in new[] { 0f, 0.1f, 0.2f, 0.35f, 0.5f, 0.75f, 1f })
            {
                var pos = lamp.Center + hits[nearest].N * offset;
                var samples = new List<(Vector3, Vector3, float)>();
                if (!Pooled(hits, groups, pos, null, 0f, out var intensity, out float error, samples) || error >= best) continue;
                best = error;
                result.Intensity = intensity;
                result.Samples = samples;
                result.Pos = lamp.Center + hits[nearest].N * Mathf.Max(offset, radius * 0.5f);
            }
            if (best == float.MaxValue) { lock (stats) stats[1]++; return null; }
        }
        else
        {
            hits = Cast(lamp.Center, Sphere, bvh, maps, FarReach, far);
            groups = Groups(hits);
            // 面から離れた照明: 向きごとの強さ（周りの明るさを引いた分 × 距離² / cos）で、明るい向きを探す
            var each = new float[hits.Count];
            foreach (var members in groups)
            {
                var sorted = members.Select(i => hits[i].L.grayscale).OrderBy(x => x).ToArray();
                float floor = sorted[(int)(sorted.Length * 0.15f)];
                foreach (int i in members)
                    each[i] = Mathf.Max(hits[i].L.grayscale - floor, 0f) * hits[i].T * hits[i].T / Mathf.Max(-Vector3.Dot(hits[i].N, hits[i].D), 0.1f);
            }
            float total = each.Sum();
            if (total <= 0f) { lock (stats) stats[1]++; return null; }
            var sum = Vector3.zero;
            for (int i = 0; i < hits.Count; i++) sum += hits[i].D * each[i];
            result.Pos = lamp.Center;
            if (sum.magnitude / total < 0.35f)
            {
                result.Samples = new List<(Vector3, Vector3, float)>();
                if (!Pooled(hits, groups, lamp.Center, null, 0f, out result.Intensity, out _, result.Samples)) { lock (stats) stats[1]++; return null; }
            }
            else
            {
                // スポット: 向きは強さの重心、広がりは強さの 9 割が入る角度
                var axis = sum.normalized;
                var byAngle = Enumerable.Range(0, hits.Count).OrderBy(i => Vector3.Angle(axis, hits[i].D)).ToArray();
                float acc = 0f, outer = 70f;
                foreach (int i in byAngle)
                {
                    acc += each[i];
                    if (acc >= total * 0.9f) { outer = Vector3.Angle(axis, hits[i].D); break; }
                }
                outer = Mathf.Clamp(outer, 25f, 70f);
                // 強さは、円錐の中へ飛ばし直した視線で当てはめる（球の 160 本だと、細い円錐には数本しか入らない）
                var inCone = Cast(lamp.Center, Cone(axis, outer, 240), bvh, maps, FarReach, far);
                result.Samples = new List<(Vector3, Vector3, float)>();
                if (!Pooled(inCone, Groups(inCone), lamp.Center, axis, outer, out result.Intensity, out _, result.Samples)) { lock (stats) stats[1]++; return null; }
                result.Axis = axis;
                result.Outer = outer;
                result.Inner = outer * 0.6f;
            }
        }
        if (result.Intensity.grayscale < MinLamp) { lock (stats) stats[2]++; return null; }
        return result;
    }

    /// <summary>
    /// 木の根元の上向きのスポット。根元の周り 8 方向 × 3 通りの距離に仮に置き、その木の幹と葉のライトマップの値が「強さ × cos / 距離²」といちばん相関する 1 つを選ぶ。
    /// </summary>
    private static Result? FitTree(Lamp lamp, Bvh bvh, Far far, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, int[] stats)
    {
        const float outer = 50f;
        Result? best = null;
        float bestCorrelation = 0.5f;
        bool any = false;
        // 近すぎると根元だけが白く飛ぶので、幹からの距離も 3 通り試す
        foreach (float away in new[] { 0.5f, 1f, 1.6f, 2.5f })
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.PI / 4f;
            var side = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var pos = lamp.Center + side * away + Vector3.up * 0.25f;
            var axis = (Vector3.up * 2f - side * away).normalized; // 幹の 2 m ほど上を狙う
            var hits = Cast(pos, Cone(axis, outer, 240), bvh, maps, Reach, far).Where(h => h.Renderer == lamp.Tree).ToList();
            if (hits.Count < 10) continue;
            any = true;
            var xs = hits.Select(h => Mathf.Max(-Vector3.Dot(h.N, h.D), 0.2f) / (h.T * h.T)).ToArray();
            float mx = xs.Average(), ml = hits.Average(h => h.L.grayscale);
            float vx = 0f, vl = 0f;
            Color cov = default, mean = default;
            foreach (var h in hits) mean += h.L / hits.Count;
            for (int i = 0; i < hits.Count; i++)
            {
                vx += (xs[i] - mx) * (xs[i] - mx);
                vl += (hits[i].L.grayscale - ml) * (hits[i].L.grayscale - ml);
                cov += (hits[i].L - mean) * (xs[i] - mx);
            }
            if (vx < 1e-8f || vl < 1e-8f) continue;
            float correlation = cov.grayscale / Mathf.Sqrt(vx * vl);
            if (correlation <= bestCorrelation) continue;
            bestCorrelation = correlation;
            var intensity = Cap(new Color(Mathf.Max(cov.r / vx, 0f), Mathf.Max(cov.g / vx, 0f), Mathf.Max(cov.b / vx, 0f), 1f), xs, hits);
            float floor = (mean - intensity * mx).grayscale;
            best = new Result
            {
                Name = lamp.Name, Id = lamp.Id, Pos = pos, Axis = axis, Radius = 0.08f, Outer = outer, Inner = outer * 0.6f, Intensity = intensity,
                Samples = hits.Select(h => (h.P, h.N, h.L.grayscale - floor)).ToList(),
            };
        }
        if (best is not { } found) { lock (stats) stats[any ? 1 : 0]++; return null; }
        // 弱い当てはまりは、空の明るさの上下の差を拾っただけ（青く弱い光になった）
        if (found.Intensity.grayscale < 0.5f) { lock (stats) stats[2]++; return null; }
        return found;
    }

    /// <summary>
    /// 光源どうしの取り合いを直す。1 灯ずつ当てはめると、並んだ照明（看板の器具の列、木のスポットと足元の照明）が同じ明るさをそれぞれ自分の分と数え、
    /// 合わせると明るすぎた（木の幹がゲームの 2〜3 倍）。当てはめに使った点で、近くの全部の光源の光を足した予想が、ライトマップの明るさに合うように、
    /// 光源ごとの倍率を掛け算で少しずつ直す（強める方向には直さない）。
    /// shortcut: ほかの光源から点までの遮りは見ない。壁を挟んだ光源を取り違えるようなら、遮りの視線を足す
    /// </summary>
    private static void Balance(List<Result> lamps)
    {
        int n = lamps.Count;
        float Predict(int k, Vector3 p, Vector3 normal) => Lit(lamps[k], p, normal);
        // 点ごと: 自分の光と、近くのほかの光源の光（倍率 1 のとき）
        var own = new float[n][];
        var others = new List<(int k, float v)>[n][];
        Parallel.For(0, n, j =>
        {
            var samples = lamps[j].Samples;
            own[j] = new float[samples.Count];
            others[j] = new List<(int, float)>[samples.Count];
            var near = Enumerable.Range(0, n).Where(k => k != j && (lamps[k].Pos - lamps[j].Pos).sqrMagnitude < 4f * FarReach * FarReach).ToArray();
            for (int i = 0; i < samples.Count; i++)
            {
                own[j][i] = Predict(j, samples[i].P, samples[i].N);
                others[j][i] = new List<(int, float)>();
                foreach (int k in near)
                {
                    float v = Predict(k, samples[i].P, samples[i].N);
                    if (v > 1e-4f) others[j][i].Add((k, v));
                }
            }
        });
        var gain = Enumerable.Repeat(1f, n).ToArray();
        for (int round = 0; round < 12; round++)
        {
            var next = new float[n];
            for (int j = 0; j < n; j++)
            {
                float want = 0f, got = 0f;
                for (int i = 0; i < own[j].Length; i++)
                {
                    float total = gain[j] * own[j][i];
                    foreach (var (k, v) in others[j][i]) total += gain[k] * v;
                    want += Mathf.Max(lamps[j].Samples[i].E, 0f) * own[j][i];
                    got += total * own[j][i];
                }
                next[j] = got > 1e-9f ? Mathf.Min(gain[j] * want / got, 1f) : gain[j];
            }
            gain = next;
        }
        for (int j = 0; j < n; j++)
        {
            var l = lamps[j];
            l.Intensity *= gain[j];
            l.Intensity.a = 1f;
            lamps[j] = l;
        }
    }

    /// <summary>光源 l が、場所 p・向き normal の面に届ける明るさ（ライトマップと同じ尺度、遮りは見ない）。</summary>
    private static float Lit(in Result l, Vector3 p, Vector3 normal) => l.Intensity.grayscale * Reaches(l, p, normal);

    /// <summary>強さ 1 の光源 l が、場所 p・向き normal の面に届ける割合（cos / 距離² に、スポットの縁や面光源の向きを掛けた値）。</summary>
    private static float Reaches(in Result l, Vector3 p, Vector3 normal)
    {
        var to = l.Pos - p;
        float d2 = Mathf.Max(to.sqrMagnitude, 0.01f), d = Mathf.Sqrt(d2);
        float cos = Mathf.Max(Vector3.Dot(normal, to) / d, 0f);
        if (cos <= 0f || d > 1.5f * FarReach) return 0f;
        // 面光源: 向いている側だけ。近くでは、面の広さで頭打ち（円盤で近づけた式）
        if (l.Area > 0f) return cos * Mathf.Max(-Vector3.Dot(l.Axis, to) / d, 0f) / (d2 + l.Area / Mathf.PI);
        float shape = l.Outer > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(l.Outer, l.Inner, Vector3.Angle(l.Axis, -to))) : 1f;
        return cos / d2 * shape;
    }

    /// <summary>木の葉の素材と Renderer ごとの、地の色に掛ける倍率（MaterialLibrary.SetShade）。</summary>
    public List<(Material material, int renderer, float shade)> Shades { get; private set; } = new();

    /// <summary>
    /// 木 1 本（葉の素材と Renderer）ごとに、ライトマップの明るさと、空・光源が届ける予想の比を出す。
    /// 予想は、ゲームが描く側の面（素材の _Cull）に届く光。葉どうしの遮りは見ない。
    /// </summary>
    private static List<(Material, int, float)> Shade(List<Surface> surfaces, Bvh bvh, Far far, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Result> lights)
    {
        var result = new List<(Material, int, float)>();
        foreach (var g in surfaces.Where(x => x.Leaf).GroupBy(x => (x.Material, x.RendererId)))
        {
            var tris = new List<(float baked, float predicted, float area)>();
            Parallel.ForEach(g, x =>
            {
                if (!maps.TryGetValue(x.Lightmap, out var map) || map.color == null) return;
                var stack = new int[128];
                var own = new List<(float, float, float)>();
                for (int i = 0; i + 2 < x.Idx.Length; i += 3)
                {
                    Vector3 p0 = x.Pos[x.Idx[i]], p1 = x.Pos[x.Idx[i + 1]], p2 = x.Pos[x.Idx[i + 2]];
                    var cross = Vector3.Cross(p1 - p0, p2 - p0);
                    float tri = cross.magnitude * 0.5f;
                    if (tri < 0.005f) continue;
                    Vector3 n = cross.normalized, at = (p0 + p1 + p2) / 3f;
                    float light = 0f;
                    var sides = x.Cull == 2 ? new[] { n } : x.Cull == 1 ? new[] { -n } : new[] { n, -n };
                    foreach (var side in sides)
                    {
                        light += far.At(at, side, stack).grayscale / sides.Length;
                        foreach (var l in lights)
                        {
                            float v = Lit(l, at, side);
                            if (v > 0.003f && !bvh.Blocked(at + side * 0.03f, l.Pos, stack)) light += v / sides.Length;
                        }
                    }
                    own.Add((Sample(map, (x.Uv[x.Idx[i]] + x.Uv[x.Idx[i + 1]] + x.Uv[x.Idx[i + 2]]) / 3f, n).grayscale, light, tri));
                }
                lock (tris) tris.AddRange(own);
            });
            float area = tris.Sum(t => t.area);
            if (area < 1f) continue;
            // いちばん明るい葉どうし（面積で上から 1 割の所）を比べる。平均どうしだと、下から照らされた木（Stage01 の広場）で、照らされた葉まで
            // 照らされていない葉の暗さに合わせて暗くなった。暗い木は、明るい葉でも 0.1 ほどなので、これでも暗くなる
            float Top(Func<(float baked, float predicted, float area), float> value)
            {
                float acc = 0f;
                foreach (var t in tris.OrderByDescending(value)) { acc += t.area; if (acc >= area * 0.1f) return value(t); }
                return 0f;
            }
            float predicted = Top(t => t.predicted);
            if (predicted <= 0f) continue;
            // 暗くする側だけ（明るくする側も許すと、予想に照り返しが入っていない分、一律に 1.2〜1.6 倍になる）
            float shade = Mathf.Clamp(Top(t => t.baked) / predicted, 0.05f, 1f);
            if (shade < 0.95f) result.Add((g.Key.Material, g.Key.RendererId, shade));
        }
        return result;
    }

    /// <summary>補助光の候補の内訳（ログ用）。</summary>
    public static string LastFill = "";

    /// <summary>開発用（mod.fills = 0）: 補助光を置かない。</summary>
    public static bool Fills = true;

    private const float FillSpacing = 6f, FillLift = 3f, FillReach = 10f, MinFill = 0.3f;

    /// <summary>
    /// 残りのうち、補助光で埋める割合（mod.fillshare）。残りには照り返しの分も入っていて、Remix は照り返しを自分で足すので、全部を埋めると明るすぎる
    /// （Stage09 の 12 視点の中央値が、1 のとき +0.3 段）。
    /// </summary>
    public static float FillShare = 0.7f;

    /// <summary>
    /// 補助光: 空・平行光源・照明・場面にもとからある光を全部引いても、ライトマップに残る明るさを、何も無い所に置く点光源で埋める。
    /// 焼いたあとで光源を場面から消してあるステージ（Stage05・09）は、器具の無い光（広場を照らす大きな面光源など）がライトマップにしか残っていない
    /// （Stage09 の広場の地面: ライトマップ (0.82, 1.64, 1.47)、空で説明できる分 (0.18, 0.55, 1.52)）。
    /// 置き場所は、ライトプローブ（作り手が人の通る所に並べてある）を FillSpacing の升目でまとめ、その FillLift 上。そこから視線を飛ばし、当たった点の残りを
    /// 「強さ × cos / 距離²」に当てはめる（切片なし: 残りは全部この光で説明する）。強さの上限（Cap）と取り合い（Balance）は照明と同じ。
    /// </summary>
    private static List<Result> Fill(Vector3[] probes, List<Surface> surfaces, Bvh bvh, Far far, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Result> lamps, List<Result> known)
    {
        var cells = new Dictionary<(int, int, int), (Vector3 sum, float n)>();
        void Add(Vector3 p, float weight)
        {
            var key = (Mathf.FloorToInt(p.x / FillSpacing), Mathf.FloorToInt(p.y / 4f), Mathf.FloorToInt(p.z / FillSpacing));
            cells[key] = cells.TryGetValue(key, out var c) ? (c.sum + p * weight, c.n + weight) : (p * weight, weight);
        }
        // 持ち上げる高さ: ライトプローブ（人の高さにある）からは FillLift、床からは 1.6 m（部屋の壁より上へ出ないように）
        float lift = FillLift;
        if (probes.Length >= 50) foreach (var p in probes) Add(p, 1f);
        else
        {
            lift = 1.6f;
            // ライトプローブがほとんど無い場面（衣装選択の部屋: 20 個で、平均の場所は部屋の外だった）は、ライトマップのある床の上に置く
            foreach (var s in surfaces)
            {
                if (s.Cutout) continue;
                for (int i = 0; i + 2 < s.Idx.Length; i += 3)
                {
                    Vector3 a = s.Pos[s.Idx[i]], b = s.Pos[s.Idx[i + 1]], c = s.Pos[s.Idx[i + 2]];
                    var cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;
                    if (area > 0.02f && cross.y > 0.7f * cross.magnitude) Add((a + b + c) / 3f, area);
                }
            }
        }
        var others = lamps.Concat(known).ToList();
        var fills = new List<Result>();
        int fewHits = 0, weak = 0;
        string weakExample = "";
        Parallel.ForEach(cells, cell =>
        {
            var stack = new int[128];
            var pos = cell.Value.sum / cell.Value.n;
            if (!bvh.Blocked(pos, pos + Vector3.up * lift, stack)) pos += Vector3.up * lift;
            // 周りの空間の真ん中へ、横に寄せる（4 m までの当たりの重心へ、2 回）。家具や壁のすぐそばに置くと、すぐ前の面を白く飛ばさない強さ = 部屋には弱すぎる光に
            // なった（衣装選択の部屋: 候補 1 つが「弱い」で消えて、真っ暗だった）
            for (int step = 0; step < 2; step++)
            {
                var shift = Vector3.zero;
                foreach (var d in Sphere)
                    shift += d * (bvh.Closest(pos, d, 0.02f, 4f, stack, out float t, out _, out _, out _, solidOnly: true) ? t : 4f);
                // 横だけ寄せる: 天井の無い部屋（衣装選択）では、上へ寄せると部屋の外へ出た
                shift.y = 0f;
                pos += shift / Sphere.Length;
            }
            var hits = Cast(pos, Sphere, bvh, maps, FillReach, far);
            if (hits.Count < 12) { System.Threading.Interlocked.Increment(ref fewHits); return; }
            var xs = new float[hits.Count];
            Color num = default;
            float den = 0f;
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                var from = h.P + h.N * 0.03f;
                foreach (var l in others)
                {
                    float share = Reaches(l, h.P, h.N);
                    if (share * l.Intensity.grayscale < 0.005f || bvh.Blocked(from, l.Pos, stack)) continue;
                    h.L -= l.Intensity * share;
                }
                h.L = Max0(h.L) * FillShare;
                hits[i] = h;
                xs[i] = Mathf.Max(-Vector3.Dot(h.N, h.D), 0f) / Mathf.Max(h.T * h.T, 0.01f);
                num += h.L * xs[i];
                den += xs[i] * xs[i];
            }
            if (den < 1e-9f) return;
            var intensity = Cap(new Color(num.r / den, num.g / den, num.b / den, 1f), xs, hits);
            if (intensity.grayscale < MinFill)
            {
                if (System.Threading.Interlocked.Increment(ref weak) == 1)
                    weakExample = $"（例: 場所 {pos}、当たり {hits.Count}、上限の前 {num.grayscale / den:0.###}、上限のあと {intensity.grayscale:0.###}、残りの平均 {hits.Average(h => h.L.grayscale):0.###}、近い当たり {hits.Min(h => h.T):0.##} m）";
                return;
            }
            var fill = new Result
            {
                Name = "補助光", Id = unchecked((cell.Key.Item1 * 73856093) ^ (cell.Key.Item2 * 19349663) ^ (cell.Key.Item3 * 83492791) ^ 0x5F111), Pos = pos, Radius = 0.3f,
                Intensity = intensity, Samples = hits.Select(h => (h.P, h.N, h.L.grayscale)).ToList(),
            };
            lock (fills) fills.Add(fill);
        });
        int fitted = fills.Count;
        Balance(fills);
        fills.RemoveAll(f => f.Intensity.grayscale < MinFill);
        LastFill = $"ライトプローブ {probes.Length} 個、置き場所の候補 {cells.Count}、当たりが少ない {fewHits}、弱い {weak}{weakExample}、取り合いで消えた {fitted - fills.Count}";
        return fills;
    }

    /// <summary>向きの近い面ごとの組（周りの明るさは面の向きで大きく違う: 上向きの地面は空の光が多く、壁は少ない）。</summary>
    private static List<List<int>> Groups(List<Hit> hits)
    {
        var groups = new List<(Vector3 n, List<int> members)>();
        for (int i = 0; i < hits.Count; i++)
        {
            var g = groups.FirstOrDefault(x => Vector3.Dot(x.n, hits[i].N) > 0.9f);
            if (g.members == null) groups.Add((hits[i].N, new List<int> { i })); else g.members.Add(i);
        }
        return groups.Select(g => g.members).ToList();
    }

    /// <summary>
    /// 値 = B + I x（x = cos / 距離²、光源は light）を、色ごとに最小二乗で。B は面の向きの組ごと、I は全部の組で 1 つ。
    /// axis があれば、半角 outer 度のスポット（芯は 0.6 倍）として、縁の減り方も x に掛ける。当てはめられなければ false。
    /// </summary>
    private static bool Pooled(List<Hit> hits, List<List<int>> groups, Vector3 light, Vector3? axis, float outer, out Color intensity, out float error,
        List<(Vector3, Vector3, float)> samples)
    {
        intensity = default;
        error = 0f;
        var xs = new float[hits.Count];
        var use = new bool[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            var to = light - hits[i].P;
            float d2 = Mathf.Max(to.sqrMagnitude, 0.01f);
            xs[i] = Mathf.Max(Vector3.Dot(hits[i].N, to) / Mathf.Sqrt(d2), 0f) / d2;
            if (axis is { } a) xs[i] *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, outer * 0.6f, Vector3.Angle(a, -to)));
            use[i] = true;
        }
        Color cov = default;
        float vx = 0f;
        var fitted = new List<(List<int> members, float mx, Color mean)>();
        foreach (var all in groups)
        {
            var members = all.Where(i => use[i]).ToList();
            if (members.Count < 4) continue;
            float mx = members.Average(i => xs[i]);
            var mean = new Color(members.Average(i => hits[i].L.r), members.Average(i => hits[i].L.g), members.Average(i => hits[i].L.b));
            foreach (int i in members)
            {
                cov += (hits[i].L - mean) * (xs[i] - mx);
                vx += (xs[i] - mx) * (xs[i] - mx);
            }
            fitted.Add((members, mx, mean));
        }
        if (vx < 1e-6f) return false;
        intensity = Cap(new Color(Mathf.Max(cov.r / vx, 0f), Mathf.Max(cov.g / vx, 0f), Mathf.Max(cov.b / vx, 0f), 1f), xs, hits);
        foreach (var (members, mx, mean) in fitted)
            foreach (int i in members)
            {
                float miss = hits[i].L.grayscale - (mean + intensity * (xs[i] - mx)).grayscale;
                error += miss * miss;
                samples.Add((hits[i].P, hits[i].N, hits[i].L.grayscale - (mean - intensity * mx).grayscale));
            }
        return true;
    }

    /// <summary>
    /// 強さの上限: 光源が点に届ける分（強さ × x）は、その点のライトマップの値（ほかの光も入った合計）を超えない。
    /// 傾きだけで決めると、当たりの少ない照明（高い街灯: 届く範囲に地面がほとんど無い）や木のスポットが、切片を負にして何倍も強くなった
    /// （Stage02 の街灯 23、足元がピンクに飛んだ）。点ごとの「値 / x」を、届く光の多い点から数えて 2 割の所で切る（焼いたときの小さな影に引っぱられないように）。
    /// </summary>
    private static Color Cap(Color intensity, float[] xs, List<Hit> hits)
    {
        var order = Enumerable.Range(0, hits.Count).Where(i => xs[i] > 1e-4f).ToArray();
        float total = order.Sum(i => xs[i]);
        if (total <= 0f) return intensity;
        for (int c = 0; c < 3; c++)
        {
            float acc = 0f;
            foreach (int i in order.OrderBy(i => hits[i].L[c] / xs[i]))
            {
                acc += xs[i];
                if (acc < total * 0.2f) continue;
                intensity[c] = Mathf.Min(intensity[c], hits[i].L[c] / xs[i]);
                break;
            }
        }
        return intensity;
    }

    /// <summary>
    /// 看板 1 枚ごとの明るさ: 絵の面のライトマップの値の、面積で重みを付けた中央の値と、置いた光源がその面に届ける明るさ（lit）。
    /// 平均だと、面の端の強く光った所に引っぱられて、見えている所の 2.7 倍になった（広場の大きな看板: 平均 1.47、ゲームの絵から逆算すると 0.55）
    /// </summary>
    private static List<(Material, int, Color, float)> BoardLight(List<(Material material, int renderer, int lightmap, List<(Vector3 p, Vector2 uv, Vector3 n, float area)> texels)> boards,
        Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, List<Result> lamps)
    {
        var result = new List<(Material, int, Color, float)>();
        foreach (var (material, renderer, lightmap, texels) in boards)
        {
            if (!maps.TryGetValue(lightmap, out var map) || map.color == null) continue;
            var values = texels.Select(t => (c: Sample(map, t.uv, t.n), t.area)).OrderBy(v => v.c.grayscale).ToList();
            float area = texels.Sum(t => t.area), acc = 0f, lit = 0f;
            var median = values[values.Count - 1].c;
            foreach (var (c, a) in values)
            {
                acc += a;
                if (acc >= area * 0.5f) { median = c; break; }
            }
            foreach (var (p, _, n, a) in texels)
                foreach (var l in lamps) lit += Lit(l, p, n) * a;
            result.Add((material, renderer, median, lit / area));
        }
        return result;
    }

    private static Vector3[] Fibonacci(int n)
    {
        var r = new Vector3[n];
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - (i + 0.5f) / n * 2f, rad = Mathf.Sqrt(1f - y * y), a = golden * i;
            r[i] = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
        }
        return r;
    }

    /// <summary>axis の周り、半角 half 度の円錐の中の向き n 個（一様）。</summary>
    private static Vector3[] Cone(Vector3 axis, float half, int n)
    {
        var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        var v = Vector3.Cross(axis, u);
        var r = new Vector3[n];
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f)), cosHalf = Mathf.Cos(half * Mathf.Deg2Rad);
        for (int i = 0; i < n; i++)
        {
            float z = 1f - (i + 0.5f) / n * (1f - cosHalf), rad = Mathf.Sqrt(1f - z * z), a = golden * i;
            r[i] = axis * z + u * (Mathf.Cos(a) * rad) + v * (Mathf.Sin(a) * rad);
        }
        return r;
    }

    /// <summary>origin から dirs へ視線を飛ばし、いちばん近い当たり（0.1 m から reach まで）のライトマップの値を読む。</summary>
    private static List<Hit> Cast(Vector3 o, Vector3[] dirs, Bvh bvh, Dictionary<int, (int w, int h, byte[] color, byte[] dir)> maps, float reach = Reach, Far far = null)
    {
        var hits = new List<Hit>();
        var stack = new int[128];
        foreach (var d in dirs)
        {
            if (!bvh.Closest(o, d, 1e-4f, reach, stack, out float best, out float bu, out float bv, out var hit) || best < 0.1f) continue;
            var front = Vector3.Cross(hit.E1, hit.E2).normalized;
            bool back = Vector3.Dot(front, d) > 0f;
            var normal = back ? -front : front; // 照明の側を向く面
            // 木の葉の板を裏から見たとき: ライトマップは表（空の側）の明るさなので、表の向きで読んで、表に届く空の光を引く
            // （裏の向きで引いていた頃は、表の空の光がまるごと残り、木の根元に空の色の偽のスポットが置かれた）
            var lit = back && hit.S.Cutout ? front : normal;
            if (-Vector3.Dot(normal, d) < 0.1f || !maps.TryGetValue(hit.S.Lightmap, out var map) || map.color == null) continue;
            var s = hit.S;
            var uv = s.Uv[s.Idx[hit.I]] * (1f - bu - bv) + s.Uv[s.Idx[hit.I + 1]] * bu + s.Uv[s.Idx[hit.I + 2]] * bv;
            var p = o + d * best;
            var light = Sample(map, uv, lit);
            // 空と平行光源で説明できる分は引いておく（残りが、照明と照り返し）
            if (far != null) light = Max0(light - far.At(p, lit, stack));
            hits.Add(new Hit { P = p, N = normal, D = d, T = best, L = light, Renderer = s.RendererId });
        }
        return hits;
    }

    private static Color Max0(Color c) => new(Mathf.Max(c.r, 0f), Mathf.Max(c.g, 0f), Mathf.Max(c.b, 0f), 1f);

    /// <summary>
    /// 遠くの光（空と、場面の焼き込みの平行光源）が、面の 1 点に届ける分。遮りを視線で見る。
    /// 照明の当てはめでは、ライトマップの値からこれを引いた残りを使う: 引かないと、空の光のむら（建物の影の縁）を照明の光と取り違えて、
    /// 遠くまで見る照明（高い街灯）が何十倍にも強くなった。Remix に渡す環境光（SceneGeometry.EnqueueAmbient）と同じ分け方。
    /// </summary>
    private sealed class Far
    {
        private readonly Bvh _bvh;
        private readonly List<(Vector3 forward, Color radiance)> _suns;
        private readonly Color _e, _c;
        private readonly Vector3[] _cone;
        private readonly float _coneUp;

        public Far(Bvh bvh, List<(Vector3 forward, Color radiance)> suns, (Color up, Color side, Color down) ambient)
        {
            _bvh = bvh;
            _suns = suns;
            float alpha;
            (_e, _c, _, alpha) = SceneGeometry.SolveAmbient(ambient.up, ambient.side, ambient.down);
            _cone = Cone(Vector3.up, alpha * Mathf.Rad2Deg, 8);
            _coneUp = _cone.Sum(d => d.y);
        }

        public Color At(Vector3 p, Vector3 n, int[] stack)
        {
            var from = p + n * 0.03f;
            float sky = 0f;
            foreach (var d in _cone)
            {
                float cos = Vector3.Dot(n, d);
                if (cos > 0f && !_bvh.Closest(from, d, 0.02f, 80f, stack, out _, out _, out _, out _, solidOnly: true)) sky += cos;
            }
            var light = _e + _c * (sky / _coneUp);
            foreach (var (forward, radiance) in _suns)
            {
                float cos = -Vector3.Dot(n, forward);
                if (cos > 0f && !_bvh.Closest(from, -forward, 0.02f, 150f, stack, out _, out _, out _, out _, solidOnly: true)) light += radiance * cos;
            }
            return light;
        }
    }

    /// <summary>ライトマップの値（地の色に掛ける光）。向きのライトマップがあれば、URP の DecodeDirectionalLightmap と同じに面の向きで戻す。</summary>
    private static Color Sample((int w, int h, byte[] color, byte[] dir) map, Vector2 uv, Vector3 normal)
    {
        float x = Mathf.Clamp(uv.x * map.w - 0.5f, 0f, map.w - 1.001f), y = Mathf.Clamp(uv.y * map.h - 0.5f, 0f, map.h - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        // 半精度の RGBA（1 画素 8 バイト）
        Color Texel(int xi, int yi)
        {
            int i = (yi * map.w + xi) * 8;
            return new Color(HalfToFloat(BitConverter.ToUInt16(map.color, i)), HalfToFloat(BitConverter.ToUInt16(map.color, i + 2)), HalfToFloat(BitConverter.ToUInt16(map.color, i + 4)));
        }
        var c = Color.Lerp(Color.Lerp(Texel(x0, y0), Texel(x0 + 1, y0), fx), Color.Lerp(Texel(x0, y0 + 1), Texel(x0 + 1, y0 + 1), fx), fy);
        if (map.dir == null) return c;
        int di = (Mathf.RoundToInt(y) * map.w + Mathf.RoundToInt(x)) * 4;
        var dir = new Vector3(map.dir[di] / 255f - 0.5f, map.dir[di + 1] / 255f - 0.5f, map.dir[di + 2] / 255f - 0.5f);
        float w = Mathf.Max(map.dir[di + 3] / 255f, 1e-4f);
        return c * ((Vector3.Dot(normal, dir) + 0.5f) / w);
    }

    private static float SqrDistance(Vector3 min, Vector3 max, Vector3 p)
    {
        var q = Vector3.Max(min, Vector3.Min(max, p));
        return (q - p).sqrMagnitude;
    }

    private static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
        float v = exp == 0 ? mant / 1024f * (1f / 16384f) : exp == 31 ? float.PositiveInfinity : (1f + mant / 1024f) * Mathf.Pow(2f, exp - 15);
        return sign != 0 ? -v : v;
    }
}
