using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 場面の静的なメッシュ（MeshRenderer）を集めて、Remix に渡す仕事（<see cref="Jobs"/>）にする。メインスレッドで動く。
/// ゲームのメッシュはほとんど CPU から読めない（isReadable = false）ので、頂点とインデックスは GPU から読み戻す（AsyncGPUReadback）。
/// 静的バッチされた物は、まとめられたメッシュ（ワールド座標）の、その Renderer の分のサブメッシュだけを使う。
/// Remix の呼び出しは描画スレッドの仕事なので、ここでは作るものを並べるだけ。
/// </summary>
internal sealed class SceneGeometry
{
    /// <summary>描画スレッドへの仕事。Gen は積んだときの場面の世代（<see cref="Enqueue"/> が入れる）。</summary>
    internal abstract class Job
    {
        public int Gen;
    }

    /// <summary>今までに作ったものを全部消す（場面が変わったとき）。</summary>
    internal sealed class ResetJob : Job { }

    /// <summary>メッシュ 1 つと、それを置く場所の一覧。</summary>
    internal sealed class MeshJob : Job
    {
        public ulong Hash;
        public SurfaceDesc[] Surfaces;
        public Matrix4x4[] Instances;
        public int[] RendererIds; // Instances と同じ並び。動いたときに、どの実体かを指す
        public bool Overlay;      // ゲーム自身が描き直す物（Remix では影だけ）
        // 裏の面も描くか。ゲームが裏を描かない素材（_Cull 2）だけの物は false にして、Remix にも裏を描かせない:
        // ポスターや建物の壁には、同じ場所に背中合わせで置いた板があり、両面を描くと重なってちらついた（Z-fighting、2026-11-10）
        public bool DoubleSided = true;
    }

    /// <summary>開発用（mod.uvrange = メッシュの名前の一部）: 次に読む場面で、そのメッシュの UV の範囲をログに書く。</summary>
    public static string UvRangeOf;

    /// <summary>開発用（mod.cull = 0）: 裏の面を描かない指定をやめて、全部を両面で描く（次に読む場面から）。</summary>
    public static bool CullBackFaces = true;

    /// <summary>動いた・消えた・出てきた物の、新しい場所と見え方。</summary>
    internal sealed class UpdateJob : Job
    {
        public int[] RendererIds;
        public Matrix4x4[] Matrices;
        public bool[] Visible;
    }

    /// <summary>面 1 つ。素材は <see cref="MaterialLibrary"/> が別の仕事で先に作っておく。</summary>
    internal struct SurfaceDesc
    {
        public Vector3[] Positions, Normals;
        public Vector2[] Uvs;
        public int[] Indices;
        public ulong MaterialHash;
    }

    /// <summary>素材のテクスチャ。Ptr は ID3D11Texture2D*（Unity の物なので、描画スレッドで取り込むまで生きている前提）。Hash が 0 なら無し。</summary>
    internal struct TextureDesc
    {
        public IntPtr Ptr;
        public uint DxgiFormat, Encoding;
        public ulong Hash;
    }

    /// <summary>場面の光源 1 つ。</summary>
    internal sealed class LightJob : Job
    {
        public ulong Hash;
        public LightType Type;
        public Vector3 Position, Forward, Right, Up;
        public Color Radiance;
        public float Range, SpotAngle, InnerSpotAngle;
        public Vector2 AreaSize;
        public bool BakedOnly; // ゲームでは焼き込み（ライトマップ）だけに効く光
        public string Name = ""; // ゲームの光源の道筋（開発用の mod.skiplight で外すため）
        public bool Ambient;   // ゲームの環境光（RenderSettings.ambient*）を写した、とても広い平行光源
        public float AngularDiameter = 0.5f;
        public float Radius;   // 点光源の球の半径（0 なら既定の 5 cm）
        public int Id;         // ゲームの Light の ID（0 はゲームの Light ではない物）。同じ ID の仕事が来たら、前の光を消して置き換える
        public bool Off;       // その Light が消えた（Id の光を消すだけの仕事）
    }

    public readonly ConcurrentQueue<Job> Jobs = new();

    /// <summary>
    /// いちばん新しい場面の世代。描画スレッドは、これより古い世代の仕事を捨てる。
    /// 場面が変わると、古い場面のテクスチャはゲームが解放し、こちらで作った絵も捨てるので、残っていた仕事を Remix に渡すと解放済みの物を指す
    /// （2026-11-09 に Remix が中断した: 0x40000015）。
    /// </summary>
    public volatile int LatestGeneration;

    /// <summary>今の世代の印を付けて積む。</summary>
    public void Enqueue(Job job)
    {
        job.Gen = _generation;
        Jobs.Enqueue(job);
    }

    private readonly ManualLogSource _log;
    private int _generation;
    /// <summary>今の場面の世代の番号（場面が変わるたびに増える）。取っ手の hash に混ぜる。</summary>
    public int Generation => _generation;
    private int _waiting, _meshJobs, _failed, _instances;

    private sealed class Pending
    {
        public Mesh Mesh;
        public byte[][] Streams;
        public byte[] Indices;
        public int Outstanding;
        public bool Failed;
        public readonly List<Use> Uses = new();
    }

    private struct Use
    {
        public int SubStart, SubCount;
        public Material[] Materials;
        public Matrix4x4 ToWorld;
        public int RendererId;
        public bool Overlay;
        public int Lightmap;  // ライトマップの番号。-1 は読まない（焼き込みの照明の明るさを読むため。照明の近くの物だけ）
        public Vector4 LightmapST;
        public string Lamp;   // 照明の素材（BakedLamps.IsLampMaterial）を持つ Renderer の名前。無ければ null
        public bool Board;    // 看板の絵の素材を持つ: Remix の素材とメッシュを Renderer ごとに分ける（場所ごとに焼き込みの明るさが違う）
        public ulong Stable;  // その Renderer の、起動のたびに同じ値（StableId.Of）
        public bool Foliage;  // 木の葉の素材（MaterialLibrary.IsLeaf）を持つ: Remix の素材とメッシュを Renderer ごとに分ける（木ごとに焼き込みの明るさが違う）
        public bool Reflective; // 反射プローブの箱に入っている: 金属は、地の色で周りを映す素材にする（MaterialLibrary.ReflectiveVariant）
    }

    // 静的バッチされていない Renderer（動くかもしれない物）。毎フレーム、場所と見え方が変わったかを見る
    private sealed class Tracked
    {
        public MeshRenderer Renderer;
        public int Id;
        public Matrix4x4 Last;
        public bool Visible;
        public bool Batched;  // 静的バッチ: 動かない（場所は単位行列のまま）。LOD で消えるかだけ見る
        public LODGroup Lod;  // いちばん細かい段のときの LODGroup（離れると描かれなくなる）
        public float Cull;    // その高さ（画面の高さに対する割合）より小さく映ると、ゲームは描かない（最後の段の screenRelativeTransitionHeight）
    }

    private readonly List<Tracked> _tracked = new();
    private readonly HashSet<int> _knownRenderers = new();

    public readonly MaterialLibrary Materials;
    public readonly BakedLamps Lamps;

    public SceneGeometry(ManualLogSource log)
    {
        _log = log;
        Materials = new MaterialLibrary(this, log);
        Lamps = new BakedLamps(log);
    }

    /// <summary>今の場面の MeshRenderer を集め直す。前の場面の分は描画スレッドで消える。</summary>
    public void Rebuild(string scene)
    {
        ShowGlass(); // 素材を読む前に、差し替えたガラスを戻す
        int gen = ++_generation;
        LatestGeneration = gen;
        Enqueue(new ResetJob());
        Materials.Reset(gen);
        _meshHashes.Clear();
        _waiting = _meshJobs = _failed = _instances = 0;
        _tracked.Clear();
        _knownRenderers.Clear();
        _sceneName = scene;
        var saved = LampCache ? LoadLamps(scene) : null;
        Lamps.Cached = saved != null;
        Lamps.FindLamps(CoarseLods());
        var (renderers, meshes) = Collect(gen);
        int lights = CollectLights(gen);
        if (saved != null) ApplySaved(gen, saved);
        _log.LogInfo($"[{scene}] メッシュを GPU から読み戻します: Renderer {renderers} 個、メッシュ {meshes} 個。光源 {lights} 個");
    }

    // ゲームのガラスを見えない素材に差し替えた Renderer と、元の素材
    private readonly List<(Renderer renderer, Material[] original)> _glass = new();
    private readonly HashSet<int> _glassChecked = new();

    /// <summary>
    /// Remix がガラスを描くので、ゲームの同じガラス（<see cref="MaterialLibrary.IsGlass"/>）は、何も描かない素材に差し替える（LOD の粗い段の物も）。
    /// まだ見ていない Renderer だけを見る。Remix の形は差し替える前の素材で作る（Collect のあとに呼ぶ）
    /// </summary>
    public void HideGlass(Material invisible)
    {
        if (invisible == null) return;
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!_glassChecked.Add(r.GetInstanceID())) continue;
            var mats = r.sharedMaterials;
            if (!mats.Any(MaterialLibrary.IsGlass)) continue;
            _glass.Add((r, mats));
            r.sharedMaterials = mats.Select(m => MaterialLibrary.IsGlass(m) ? invisible : m).ToArray();
        }
    }

    /// <summary>差し替えたガラスを元に戻す（Remix を切ったとき・場面を作り直すとき）。</summary>
    public void ShowGlass()
    {
        foreach (var (r, original) in _glass)
            if (r != null) r.sharedMaterials = original;
        _glass.Clear();
        _glassChecked.Clear();
    }

    /// <summary>まだ知らない MeshRenderer を足す（場面に入ったあとに出てくる物）。足した Renderer の数を返す。</summary>
    public int Rescan() => Collect(_generation).renderers;

    private (int renderers, int meshes) Collect(int gen)
    {
        var byMesh = new Dictionary<Mesh, Pending>();
        var reflectionBoxes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None).Select(probe => probe.bounds).ToList();
        int renderers = 0;
        var coarse = CoarseLods();
        var lods = FinestLods();
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!_knownRenderers.Add(r.GetInstanceID())) continue;
            var mats = r.sharedMaterials;
            // トゥーンやシェーダーグラフの物（アイテムなど）は、ゲーム自身に描き直させる印を付ける（RemixCompositePass）。Remix には影のために渡す。
            // ほかは環境の印（描き直すときの深さに入れる）。粗い LOD にも付ける: 遠くでゲームが粗い段を描いているときも、深さが要る
            // shortcut: 場面の途中で出てきた物は、次の見直し（Rescan）までは深さに入らない。目立つ物が出たら、描く前に印を付ける
            bool overlay = mats.Any(m => m != null && !MaterialLibrary.IsEnvironment(m));
            r.renderingLayerMask |= overlay ? RemixCompositePass.OverlayBit : RemixCompositePass.EnvironmentBit;
            if (coarse.Contains(r)) continue;
            // 影だけを落とす代わりの形（建物の _Shadow、素材 M_ShadowModel）は渡さない。Remix は本物の形から影を出す。
            // 渡すと本物の壁と同じ所に重なり、黒い三角が出てちらついた（2026-11-10）
            if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) continue;
            // 道路: 平行光源のハイライトを、ゲーム自身に描き足させる印（MaterialLibrary.SunHighlight）。素材が 1 種類だけの Renderer に限る（素材を差し替えて描くので）
            if (mats.Length > 0 && mats.All(m => m == mats[0])) r.renderingLayerMask |= Materials.HighlightBit(mats[0]);
            // 静的バッチ: まとめられたメッシュの頂点はワールド座標で、この Renderer の面は subMeshStartIndex から素材の数だけ
            bool batched = r.isPartOfStaticBatch;
            // 静的バッチの物は動かないので、無効なら出さない（見え方を追いかけない）
            if (batched && !r.enabled) continue;
            int start = batched ? r.subMeshStartIndex : 0;
            int count = Math.Min(mats.Length, mesh.subMeshCount - start);
            if (count <= 0) continue;
            if (!byMesh.TryGetValue(mesh, out var p)) byMesh[mesh] = p = new Pending { Mesh = mesh };
            int id = r.GetInstanceID();
            // baked: ライトマップを持つ。lightmapped: 照明の当てはめに使う（照明の近くだけ。保存してある結果を使う場面では集めない）
            bool baked = r.lightmapIndex >= 0 && r.lightmapIndex < LightmapSettings.lightmaps.Length;
            bool lightmapped = baked && Lamps.Wants(r.bounds);
            p.Uses.Add(new Use { SubStart = start, SubCount = count, Materials = mats, ToWorld = batched ? Matrix4x4.identity : r.localToWorldMatrix, RendererId = id, Overlay = overlay, Stable = StableId.Of(r),
                                 Lightmap = lightmapped ? r.lightmapIndex : -1, LightmapST = r.lightmapScaleOffset,
                                 Lamp = mats.Any(m => BakedLamps.IsLampMaterial(m, r.name)) ? r.name : null,
                                 Board = baked && mats.Any(m => BakedLamps.IsBoardMaterial(m, r.name)),
                                 Foliage = baked && mats.Any(MaterialLibrary.IsLeaf),
                                 Reflective = r.reflectionProbeUsage != ReflectionProbeUsage.Off && reflectionBoxes.Any(b => b.Intersects(r.bounds)) });
            // 見え方は TrackChanges が毎フレーム合わせる（無効な Renderer も、ここで作っておけば有効になったときに出せる）
            // LOD の物は、静的バッチでも離れたら消えるので見張る
            lods.TryGetValue(r, out var lod);
            if (!batched || lod.group != null)
                _tracked.Add(new Tracked { Renderer = r, Id = id, Last = batched ? Matrix4x4.identity : r.localToWorldMatrix, Visible = true, Batched = batched, Lod = lod.group, Cull = lod.cull });
            renderers++;
        }

        foreach (var p in byMesh.Values) Request(p, gen);
        _waiting += byMesh.Count;
        return (renderers, byMesh.Count);
    }

    /// <summary>
    /// LOD の粗い段（LOD1 から）にだけある Renderer。ゲームは距離で 1 つの段だけを描くが、Remix に全部渡すと、粗い形が細かい形に重なる
    /// （看板が粗い壁に埋もれ、粗い段の低い解像度の絵が上に出た）。Remix には、いちばん細かい段だけを渡す。
    /// shortcut: 遠くでも細かい段のまま。重くなったら、カメラからの距離で段を切り替える
    /// </summary>
    /// <summary>
    /// LOD のいちばん細かい段の Renderer と、その LODGroup が描かれなくなる高さ。Remix には細かい段だけを渡すので、
    /// ゲームが遠くで消す物（最後の段より小さく映る物）は、こちらでも消す
    /// </summary>
    private static Dictionary<Renderer, (LODGroup group, float cull)> FinestLods()
    {
        var finest = new Dictionary<Renderer, (LODGroup, float)>();
        foreach (var group in Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None))
        {
            var lods = group.GetLODs();
            if (lods.Length == 0) continue;
            foreach (var r in lods[0].renderers)
                if (r != null) finest[r] = (group, lods[lods.Length - 1].screenRelativeTransitionHeight);
        }
        return finest;
    }

    /// <summary>LODGroup が、このカメラから見て描かれない大きさか（Unity の LOD の決め方: 画面の高さに対する割合 × lodBias）。</summary>
    private static bool LodCulled(LODGroup g, float cull, Camera cam)
    {
        if (cull <= 0f || !g.enabled) return false;
        var t = g.transform;
        var s = t.lossyScale;
        float size = g.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        float d = Mathf.Max(Vector3.Distance(cam.transform.position, t.TransformPoint(g.localReferencePoint)), 1e-3f);
        float height = size * 0.5f / (d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * QualitySettings.lodBias;
        return height < cull;
    }

    internal static HashSet<Renderer> CoarseLods()
    {
        var coarse = new HashSet<Renderer>();
        foreach (var group in Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None))
        {
            var lods = group.GetLODs();
            if (lods.Length < 2) continue;
            var finest = new HashSet<Renderer>(lods[0].renderers.Where(r => r != null));
            for (int i = 1; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                    if (r != null && !finest.Contains(r)) coarse.Add(r);
        }
        return coarse;
    }

    private int CollectLights(int gen)
    {
        _liveLights.Clear();
        return CollectAmbient(gen) + CollectFixtures(gen) + TrackLights(gen, log: true);
    }

    // ゲームの Light の ID → いま Remix に出している光の仕事
    private readonly Dictionary<int, LightJob> _liveLights = new();
    private int _lightVersion;

    /// <summary>
    /// 場面の Light を光源の仕事にする。場面に入ったときと、そのあと 1 秒ごとに呼ぶ。点いた・消えた・動いた・色や強さが変わった Light だけ仕事にして、
    /// 変わった数を返す。ミニゲームの照明のように、場面に入ったあとで点く Light があり、入ったときだけ拾うと、その光が無かった（2026-11-10）。
    /// 置き換えるときは hash を変える（中身が同じ光を消して作り直すと、Remix の後回しの消去で新しい方まで消える: SceneMirror.RecreateLights）
    /// </summary>
    public int TrackLights(int gen, bool log = false)
    {
        int changed = 0;
        var seen = new HashSet<int>();
        // 小さな場面（〜Env: 衣装選択の部屋など）は、焼くのに使った照明を無効にして置いてある（明かりはライトマップにだけ残る）。その場面が出ている間は、
        // 無効でも「焼き込み専用で、焼いた」照明を光源にする（衣装選択: 点光源 3・平行光源 3。使わないと Remix の部屋が真っ暗だった）。
        // ステージの場面ではしない（ミニゲーム用の無効な照明が、ステージ全体を照らしてしまう）
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool parked = active.name.EndsWith("Env");
        foreach (var l in Object.FindObjectsByType<Light>(parked ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            bool on = l.enabled && l.gameObject.activeInHierarchy;
            if (!on && !(l.gameObject.scene == active && l.bakingOutput.isBaked && l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)) continue;
            // Default の層を照らさない光（キャラクターだけを照らす光など）は、Remix が描く環境には要らない（キャラクターはゲーム自身が描く）
            if (l.intensity <= 0f || (l.cullingMask & 1) == 0) continue;
            // 範囲 1 m 以下の面光源は、焼き込みではほぼ自分の前しか照らさない（Unity の焼き込みは範囲の外を照らさない）。Remix の光には範囲が無く、
            // 遠くまで届いてしまうので作らない（自動販売機は範囲 20 m と 1 m の 2 つを重ねていて、20 m の方は残る）
            if (l.type == LightType.Rectangle && l.range <= 1f) continue;
            int id = l.GetInstanceID();
            seen.Add(id);
            var t = l.transform;
            var job = new LightJob
            {
                Id = id, Name = PathOf(t), Type = l.type,
                Position = t.position, Forward = t.forward, Right = t.right, Up = t.up,
                Radiance = l.color.linear * l.intensity,
                Range = l.range, SpotAngle = l.spotAngle, InnerSpotAngle = l.innerSpotAngle,
                AreaSize = l.areaSize,
                BakedOnly = l.bakingOutput.isBaked && l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked,
            };
            if (_liveLights.TryGetValue(id, out var old) && Same(old, job)) continue;
            job.Hash = Hash(0x4C, gen, id, ++_lightVersion);
            if (log && l.type == LightType.Directional)
                _log.LogInfo($"平行光源 {l.name}: 色 {l.color}、強さ {l.intensity}、向き {t.forward}、cullingMask 0x{l.cullingMask:X}、" +
                             $"焼き込み {l.bakingOutput.lightmapBakeType}（焼いた {l.bakingOutput.isBaked}）、影 {l.shadows}");
            _liveLights[id] = job;
            Enqueue(job);
            changed++;
        }
        foreach (var id in _liveLights.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Enqueue(new LightJob { Id = id, Off = true });
            _liveLights.Remove(id);
            changed++;
        }
        return changed;
    }

    private static bool Same(LightJob a, LightJob b) =>
        a.Type == b.Type && (a.Position - b.Position).sqrMagnitude < 1e-4f && Vector3.Dot(a.Forward, b.Forward) > 0.9999f && Vector3.Dot(a.Up, b.Up) > 0.9999f &&
        a.Radiance == b.Radiance && a.Range == b.Range && a.SpotAngle == b.SpotAngle && a.InnerSpotAngle == b.InnerSpotAngle && a.AreaSize == b.AreaSize;

    /// <summary>
    /// ゲームの環境光を、Remix の広い平行光源 4 つで近づける。場面に入ったときは、ゲームのシェーダーが使う値（RenderSettings.ambientProbe）を上・横・下の向きで測って使い、
    /// ライトマップを読み終えたら、そこから測った空の光（BakedLamps.Sky）で作り直す（SolveLamps: 焼いた値は RenderSettings の値と合わないステージがある）。
    /// Remix の平行光源は、円錐の広さによらず、正面を向いた面の照度が π × 値（distant_light.slangh: 放射輝度 / sin²(半角) を円錐で積む）。
    /// URP の環境光は拡散 = albedo × 値（π を含む）なので、正面を向いた面ではそのまま値が合う。
    /// ・全体の分 e: 上向きと下向きの、直径 179° の 2 つ（どの向きの面にも e）
    /// ・空の分 c と地面の分 g: 半角 α の円錐。横向きの面には r(α) = (α - sin2α / 2) / (π sin²α) の割合だけ届く
    /// 上 = e + c、下 = e + g、横 = e + r (c + g) を解く。このゲームの夜空は「真上だけ明るい青」なので、α は細くなる。
    /// 影が付くので、建物の中や隙間は暗くなる（ゲームのライトマップの空の遮りに近い）。
    /// </summary>
    private int CollectAmbient(int gen)
    {
        _hasAmbient = RenderSettings.ambientMode != AmbientMode.Skybox;
        if (!_hasAmbient) return 0; // 空から（Skybox）は、空のドームライトが受け持つ
        var dirs = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        var at = new Color[dirs.Length];
        RenderSettings.ambientProbe.Evaluate(dirs, at);
        Color up = Max0(at[0]), down = Max0(at[1]), side = Max0((at[2] + at[3] + at[4] + at[5]) * 0.25f);
        _ambientUp = up;
        _ambientDown = down;
        _ambientSide = side;
        EnqueueAmbient(gen, up, side, down, "ゲームの値");
        return 4;
    }

    private Color _ambientUp, _ambientDown, _ambientSide;
    private bool _hasAmbient;
    private int _ambientVersion;

    // 環境光の 4 つの光の ID（ゲームの Light の ID と重ならない値）。同じ ID の仕事で置き換える
    private const int AmbientId = int.MinValue + 16;

    /// <summary>上・横・下の向きの面が受ける値から、環境光の 4 つの光を作る（作り直すときは、前の物を置き換える）。</summary>
    private void EnqueueAmbient(int gen, Color up, Color side, Color down, string source)
    {
        var (e, c, g, alpha) = SolveAmbient(up, side, down);
        float cone = 2f * alpha * Mathf.Rad2Deg;
        int v = _ambientVersion++;
        Enqueue(new LightJob { Id = AmbientId, Hash = Hash(0x4C, gen, -1, v), Type = LightType.Directional, Ambient = true, AngularDiameter = 179f, Forward = Vector3.down, Radiance = e });
        Enqueue(new LightJob { Id = AmbientId + 1, Hash = Hash(0x4C, gen, -2, v), Type = LightType.Directional, Ambient = true, AngularDiameter = 179f, Forward = Vector3.up, Radiance = e });
        Enqueue(new LightJob { Id = AmbientId + 2, Hash = Hash(0x4C, gen, -3, v), Type = LightType.Directional, Ambient = true, AngularDiameter = cone, Forward = Vector3.down, Radiance = c });
        Enqueue(new LightJob { Id = AmbientId + 3, Hash = Hash(0x4C, gen, -4, v), Type = LightType.Directional, Ambient = true, AngularDiameter = cone, Forward = Vector3.up, Radiance = g });
        _log.LogInfo($"環境光（{source}）: 上 {up}、横 {side}、下 {down} → 全体 {e}、空 {c}・地面 {g}（直径 {cone:0}°）");
    }

    private const float FixtureGain = 2f;

    /// <summary>
    /// 天井の照明器具（素材の名前に Ceilinglight を含む物）に、下向きの面光源を置く。このゲームのコンビニの中は、焼いたあとで消した光でだけ照らされていて
    /// （場面に光源が無い）、その明るさはライトプローブにしか残っていない（店の中の物はライトマップではなくプローブで照らされる: lightmapIndex 65534）。
    /// 器具の真下の床（当たり判定で探す）の少し上で、近くの器具の光を足した照度が、そこのプローブの上向きの値 c の π 倍（URP の値は 照度 / π）になる放射輝度にする。
    /// </summary>
    private readonly List<LightJob> _fixtureLights = new();

    private int CollectFixtures(int gen)
    {
        _fixtureLights.Clear();
        if (LightmapSettings.lightProbes == null || LightmapSettings.lightProbes.count == 0) return 0;
        var fixtures = new List<(int id, Vector3 pos, Vector2 size, Vector3 floor, Color c)>();
        var up = new[] { Vector3.up };
        var v = new Color[1];
        var coarse = CoarseLods();
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || coarse.Contains(r) || !r.sharedMaterials.Any(m => m != null && m.name.IndexOf("Ceilinglight", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            var b = r.bounds;
            var pos = new Vector3(b.center.x, b.min.y - 0.01f, b.center.z);
            // 当たり判定のトリガー（音の範囲など、見えない箱）は床ではないので外す
            var floor = Physics.Raycast(pos + Vector3.down * 0.05f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point + Vector3.up * 0.3f : pos + Vector3.down * 2.5f;
            LightProbes.GetInterpolatedProbe(floor, r, out var sh);
            sh.Evaluate(up, v);
            fixtures.Add((r.GetInstanceID(), pos, new Vector2(Mathf.Max(b.size.x, 0.1f), Mathf.Max(b.size.z, 0.1f)), floor, Max0(v[0])));
        }
        foreach (var (id, pos, size, floor, c) in fixtures)
        {
            // 照度 = 放射輝度 × Σ（面の小片の面積 × cos × cos / 距離²）。細長い器具は、0.5 m ごとの小片に分けて足す
            float g = 0f;
            foreach (var f in fixtures)
            {
                int nx = Mathf.CeilToInt(f.size.x / 0.5f), nz = Mathf.CeilToInt(f.size.y / 0.5f);
                float piece = f.size.x * f.size.y / (nx * nz);
                for (int i = 0; i < nx; i++)
                for (int k = 0; k < nz; k++)
                {
                    var at = f.pos + new Vector3(((i + 0.5f) / nx - 0.5f) * f.size.x, 0f, ((k + 0.5f) / nz - 0.5f) * f.size.y);
                    var d = at - floor;
                    if (d.y <= 0f || d.sqrMagnitude > 100f) continue;
                    g += piece * d.y * d.y / (d.sqrMagnitude * d.sqrMagnitude);
                }
            }
            if (g <= 0f) continue;
            // 面光源の値は、描画スレッドで 1 / π される（Unity の強さの決まり）ので、π を掛けておく。
            // FixtureGain: 細い器具だけでは、元の焼き込みの光（天井全体）ほど壁や棚に回らない。店の中で HDR を比べて 1 段暗かった分（2026-11-10）
            var job = new LightJob
            {
                Hash = Hash(0x46, gen, id, 0), Type = LightType.Rectangle, Position = pos,
                Forward = Vector3.down, Right = Vector3.right, Up = Vector3.forward, AreaSize = size,
                Radiance = c * (Mathf.PI / g * Mathf.PI * FixtureGain),
            };
            _fixtureLights.Add(job);
            Enqueue(job);
        }
        if (fixtures.Count > 0) _log.LogInfo($"天井の照明器具 {fixtures.Count} 個に、ライトプローブの明るさに合わせた面光源を置きます");
        return fixtures.Count;
    }

    private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

    /// <summary>焼き込みの照明の強さを決め（別のスレッド）、点光源の仕事にする。</summary>
    /// <summary>別のスレッドから、メインスレッドでしてほしいこと（SceneMirror.Update が毎フレーム片づける）。</summary>
    public readonly ConcurrentQueue<Action> MainThread = new();

    private int _lampsSolved;
    private string _sceneName = "";

    /// <summary>開発用（mod.lampcache = 0）: 照明の当てはめの結果を保存・再利用しない（次に読む場面から）。</summary>
    public static bool LampCache = true;

    // 保存の形式や当てはめ方を変えたら上げる（古いファイルは使われなくなる）
    private const int LampCacheVersion = 1;

    /// <summary>照明の当てはめの結果（空の光、光源、木の葉の暗さ、看板の発光）。素材は Remix の hash（起動のたびに同じ: StableId）で指す。</summary>
    private sealed class SavedLamps
    {
        public (Color up, Color side)? Sky;
        public List<BakedLamps.Result> Lamps;
        public Dictionary<ulong, float> Shades = new(), Glows = new();
    }

    private static string LampCachePath(string scene) =>
        System.IO.Path.Combine(BepInEx.Paths.CachePath, "HeberekeBunnyGardenRemix", $"lamps_{string.Concat(scene.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.txt");

    // 同じ場面でも中身が変わったら使わないための印: 形式の版、ライトマップの枚数と大きさ、ライトプローブの数、補助光の割合
    private static string LampCacheKey()
    {
        var maps = string.Join(",", LightmapSettings.lightmaps.Select(m => m.lightmapColor != null ? $"{m.lightmapColor.name}:{m.lightmapColor.width}" : "-"));
        int probes = LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0;
        return FormattableString.Invariant($"v{LampCacheVersion} maps[{maps}] probes {probes} fill {BakedLamps.FillShare:0.###} {(BakedLamps.Fills ? 1 : 0)}");
    }

    /// <summary>
    /// 照明の当てはめの結果をファイルに書く（BepInEx\cache）。当てはめは、ライトマップの読み戻し（4096² が 2〜4 枚）と数十秒の計算で、その間は照明が無く、CPU も使い切る。
    /// 2 回目からは読むだけにして、場面に入ってすぐ照明を点ける。
    /// </summary>
    private void SaveLamps(string scene, SavedLamps s)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder();
            text.AppendLine(LampCacheKey());
            if (s.Sky is { } sky) text.AppendLine(string.Format(inv, "sky {0:R} {1:R} {2:R} {3:R} {4:R} {5:R}", sky.up.r, sky.up.g, sky.up.b, sky.side.r, sky.side.g, sky.side.b));
            foreach (var l in s.Lamps)
                text.AppendLine(string.Format(inv, "lamp {0} {1:R} {2:R} {3:R} {4:R} {5:R} {6:R} {7:R} {8:R} {9:R} {10:R} {11:R} {12:R} {13}", l.Id, l.Pos.x, l.Pos.y, l.Pos.z, l.Axis.x, l.Axis.y, l.Axis.z,
                    l.Radius, l.Outer, l.Inner, l.Intensity.r, l.Intensity.g, l.Intensity.b, l.Name.Replace('\n', ' ')));
            foreach (var kv in s.Shades) text.AppendLine(string.Format(inv, "shade {0} {1:R}", kv.Key, kv.Value));
            foreach (var kv in s.Glows) text.AppendLine(string.Format(inv, "glow {0} {1:R}", kv.Key, kv.Value));
            var path = LampCachePath(scene);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, text.ToString());
            _log.LogInfo($"照明の当てはめの結果を保存しました: {path}");
        }
        catch (Exception e) { _log.LogWarning($"照明の結果を保存できません: {e.Message}"); }
    }

    private SavedLamps LoadLamps(string scene)
    {
        try
        {
            var path = LampCachePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            var lines = System.IO.File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != LampCacheKey()) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float F(string v) => float.Parse(v, inv);
            var s = new SavedLamps { Lamps = new List<BakedLamps.Result>() };
            foreach (var line in lines.Skip(1))
            {
                var w = line.Split(new[] { ' ' }, 15);
                switch (w[0])
                {
                    case "sky": s.Sky = (new Color(F(w[1]), F(w[2]), F(w[3]), 1f), new Color(F(w[4]), F(w[5]), F(w[6]), 1f)); break;
                    case "lamp":
                        s.Lamps.Add(new BakedLamps.Result
                        {
                            Id = int.Parse(w[1], inv), Pos = new Vector3(F(w[2]), F(w[3]), F(w[4])), Axis = new Vector3(F(w[5]), F(w[6]), F(w[7])), Radius = F(w[8]), Outer = F(w[9]), Inner = F(w[10]),
                            Intensity = new Color(F(w[11]), F(w[12]), F(w[13]), 1f), Name = w.Length > 14 ? w[14] : "",
                        });
                        break;
                    case "shade": s.Shades[ulong.Parse(w[1], inv)] = F(w[2]); break;
                    case "glow": s.Glows[ulong.Parse(w[1], inv)] = F(w[2]); break;
                }
            }
            return s;
        }
        catch (Exception e)
        {
            _log.LogWarning($"保存してある照明の結果を読めません（作り直します）: {e.Message}");
            return null;
        }
    }

    private void ApplySaved(int gen, SavedLamps s)
    {
        _lampsSolved = gen;
        if (_hasAmbient && s.Sky is { } sky)
        {
            _ambientSide = sky.side;
            EnqueueAmbient(gen, sky.up, sky.side, _ambientDown, "保存してあった、ライトマップから測った値");
        }
        foreach (var l in s.Lamps) EnqueueLamp(gen, l);
        Materials.Preset(s.Shades, s.Glows);
        _log.LogInfo($"保存してあった照明の結果を使います: 光源 {s.Lamps.Count} 個（補助光 {s.Lamps.Count(l => l.Name == "補助光")}）、木の葉 {s.Shades.Count} 本、看板 {s.Glows.Count} 枚");
    }

    private void EnqueueLamp(int gen, BakedLamps.Result l) => Enqueue(new LightJob
    {
        Hash = Hash(0x4D, gen, l.Id, 0), Name = (l.Name == "補助光" ? "" : "焼き込み/") + l.Name, Type = l.Outer > 0f ? LightType.Spot : LightType.Point, Position = l.Pos, Forward = l.Axis,
        SpotAngle = l.Outer * 2f, InnerSpotAngle = l.Inner * 2f, Radius = l.Radius, Radiance = l.Intensity, Range = 10f,
    });

    private void SolveLamps(int gen)
    {
        // 場面ごとに 1 回だけ（あとから出てきた物の読み戻しでも呼ばれる。もう一度やると同じ光源を二重に作る）
        if (_lampsSolved == gen) return;
        _lampsSolved = gen;
        // 場面にある焼き込み専用の平行光源（別に Remix へ渡してある）。ライトマップから、空の光と一緒に引く
        var suns = _liveLights.Values.Where(l => l.Type == LightType.Directional && l.BakedOnly).Select(l => (l.Forward, l.Radiance)).ToList();
        // 場面にもとからある光（Remix へ渡してある物）。補助光の計算で、ライトマップから引く。強さは 1 m 先の正面の値にそろえる:
        // 面光源は 放射輝度（強さ / π）× 面積 × cos × cos / (π 距離²)
        var known = _liveLights.Values.Concat(_fixtureLights).Where(l => l.Type != LightType.Directional).Select(l => new BakedLamps.Result
        {
            Name = l.Name, Pos = l.Position, Axis = l.Forward, Intensity = l.Type == LightType.Rectangle ? l.Radiance * (l.AreaSize.x * l.AreaSize.y / (Mathf.PI * Mathf.PI)) : l.Radiance,
            Outer = l.Type == LightType.Spot ? l.SpotAngle * 0.5f : 0f, Inner = l.Type == LightType.Spot ? l.InnerSpotAngle * 0.5f : 0f,
            Area = l.Type == LightType.Rectangle ? Mathf.Max(l.AreaSize.x * l.AreaSize.y, 1e-4f) : 0f,
        }).ToList();
        var probes = LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.positions : new Vector3[0];
        var toSave = new SavedLamps();
        Lamps.Solve(suns, _hasAmbient ? (_ambientUp, _ambientSide, _ambientDown) : default, known, probes, () => gen == _generation, lamps =>
        {
            // 環境光を、ライトマップで測った空の光で作り直す（BakedLamps.Sky）。下向きは、ゲームの値のまま。
            // shortcut: 場面から消された焼き込みの平行光源（Stage05・09 の月明かり 0.25）の分は、空の光に混ざったまま（影が柔らかくなる）。向きを測って分けるなら、高い壁の方位ごとの差から
            if (_hasAmbient && Lamps.Sky is { } sky)
            {
                _ambientSide = sky.side;
                EnqueueAmbient(gen, sky.up, sky.side, _ambientDown, $"ライトマップの、空が開けた面から。{BakedLamps.LastSky}");
            }
            foreach (var l in lamps) EnqueueLamp(gen, l);
            toSave.Sky = _hasAmbient ? Lamps.Sky : null;
            toSave.Lamps = lamps;
            var shades = Lamps.Shades;
            if (shades.Count > 0)
                MainThread.Enqueue(() =>
                {
                    if (gen != _generation) return;
                    foreach (var (material, renderer, shade) in shades)
                    {
                        Materials.SetShade(material, renderer, shade);
                        if (Materials.HashOf(material, renderer) is var hash && hash != 0) toSave.Shades[hash] = shade;
                    }
                    var sorted = shades.Select(x => x.shade).OrderBy(x => x).ToList();
                    _log.LogInfo($"木の葉 {shades.Count} 本の地の色を、ライトマップの暗さに合わせました（倍率 {sorted[0]:0.##}〜{sorted[sorted.Count - 1]:0.##}、中央 {sorted[sorted.Count / 2]:0.##}）");
                });
            var fills = lamps.Where(l => l.Name == "補助光").ToList();
            if (fills.Count > 0)
                _log.LogInfo($"補助光 {fills.Count} 個（空・照明で説明できない、ライトマップの残りの明るさ）。強さの中央 {fills.Select(l => l.Intensity.grayscale).OrderBy(x => x).ElementAt(fills.Count / 2):0.##}、" +
                             $"最大 {fills.Max(l => l.Intensity.grayscale):0.##}");
            var top = lamps.Where(l => l.Name != "補助光").OrderByDescending(l => l.Intensity.maxColorComponent).Take(8).Select(l => $"{l.Name} {l.Intensity.maxColorComponent:0.##}{(l.Outer > 0f ? $"（スポット {l.Outer:0}°）" : "")}");
            _log.LogInfo($"焼き込みにだけある照明 {lamps.Count} 個を光源にしました（候補 {Lamps.Count} 個のうち、スポット {lamps.Count(l => l.Outer > 0f)}、木 {lamps.Count(l => l.Name.StartsWith("木/"))}。" +
                         $"強い物: {string.Join("、", top)}）。外れ: {BakedLamps.LastStats}。補助光: {BakedLamps.LastFill}");
            foreach (var l in lamps.Where(l => l.Name.StartsWith("木/")))
                _log.LogInfo($"  {l.Name}: 位置 {l.Pos} 向き {l.Axis} 強さ {l.Intensity}");
        }, boards => MainThread.Enqueue(() =>
        {
            // 看板は、ゲームでは焼き込みの光で明るく塗られている。置いた光源（看板を照らすスポットなど）と環境光（横向き）で足りない分を、
            // 地の絵に掛けた発光にする（器具の無い LED の看板などは、本物も自分で光る物）。同じ絵でも置いてある場所で明るさが違うので、Renderer ごとに決める
            if (gen != _generation) return;
            var side = new[] { _ambientSide };
            int count = 0;
            foreach (var (material, renderer, light, lit) in boards)
            {
                float glow = light.grayscale - lit - side[0].grayscale;
                if (glow < 0.05f) continue;
                Materials.SetGlow(material, renderer, glow);
                if (Materials.HashOf(material, renderer) is var hash && hash != 0) toSave.Glows[hash] = glow;
                count++;
            }
            // 看板が最後（木の葉の倍率は、この前にメインスレッドで入っている）。ここまで来たら結果を保存する
            if (LampCache && toSave.Lamps != null) SaveLamps(_sceneName, toSave);
            _log.LogInfo($"看板 {count} 枚（{boards.Count} 枚のうち）を、ライトマップの明るさで光らせます（環境光の横 {side[0].grayscale:0.##}）");
        }));
    }

    private static Color Max0(Color c) => new(Mathf.Max(c.r, 0f), Mathf.Max(c.g, 0f), Mathf.Max(c.b, 0f), 1f);

    /// <summary>
    /// 上・横・下の向きの面が受ける値を、全体の分 e（どの向きにも e）と、真上の円錐（半角 alpha）の分 c、真下の円錐の分 g に分ける。
    /// 上 = e + c、下 = e + g、横 = e + r(alpha) (c + g)。BakedLamps も、ライトマップから空の分を引くのに同じ分け方を使う。
    /// </summary>
    public static (Color e, Color c, Color g, float alpha) SolveAmbient(Color up, Color side, Color down)
    {
        // α: どの色でも e が負にならない細さ（横 >= r (上 + 下)）。細すぎると影が硬いので 10° まで
        float rWant = 0.5f;
        for (int ch = 0; ch < 3; ch++) if (up[ch] + down[ch] > 1e-5f) rWant = Mathf.Min(rWant, side[ch] / (up[ch] + down[ch]));
        float alpha = Mathf.PI / 2f;
        for (float lo = 10f * Mathf.Deg2Rad, hi = Mathf.PI / 2f, i = 0; i < 30; i++)
        {
            alpha = (lo + hi) * 0.5f;
            if (SideShare(alpha) > rWant) hi = alpha; else lo = alpha;
        }
        float r = SideShare(alpha);
        Color e = default, c = default, g = default;
        for (int ch = 0; ch < 3; ch++)
        {
            e[ch] = Mathf.Clamp((side[ch] - r * (up[ch] + down[ch])) / (1f - 2f * r), 0f, Mathf.Min(up[ch], down[ch]));
            c[ch] = up[ch] - e[ch];
            g[ch] = down[ch] - e[ch];
        }
        return (e, c, g, alpha);
    }

    /// <summary>半角 α の円錐（上向き）の光のうち、横向きの面に届く割合（正面の面を 1 として）。</summary>
    private static float SideShare(float alpha)
    {
        float s = Mathf.Sin(alpha);
        return (alpha - Mathf.Sin(2f * alpha) / 2f) / (Mathf.PI * s * s);
    }

    /// <summary>
    /// 毎フレーム。静的バッチされていない物のうち、動いた・消えた・出てきた物だけを Remix に渡す。
    /// 見えなくなった物は消さずに隠す（Remix の決まり: 消して作り直すと、BLAS の作り直しで重くなる）。
    /// </summary>
    /// <summary>開発用（mod.lodcull = 0）: LOD で遠くの物を消すのをやめる。</summary>
    public bool LodCulling = true;

    public void TrackChanges(Camera cam)
    {
        List<int> ids = null;
        List<Matrix4x4> matrices = null;
        List<bool> visible = null;
        foreach (var t in _tracked)
        {
            bool alive = t.Renderer != null;
            bool vis = alive && t.Renderer.enabled && t.Renderer.gameObject.activeInHierarchy && !(LodCulling && t.Lod != null && cam != null && LodCulled(t.Lod, t.Cull, cam));
            var m = alive && !t.Batched ? t.Renderer.localToWorldMatrix : t.Last;
            if (vis == t.Visible && m == t.Last) continue;
            t.Visible = vis;
            t.Last = m;
            (ids ??= new()).Add(t.Id);
            (matrices ??= new()).Add(m);
            (visible ??= new()).Add(vis);
        }
        if (ids != null) Enqueue(new UpdateJob { RendererIds = ids.ToArray(), Matrices = matrices.ToArray(), Visible = visible.ToArray() });
    }

    private void Request(Pending p, int gen)
    {
        var mesh = p.Mesh;
        int streams = mesh.vertexBufferCount;
        p.Streams = new byte[streams][];
        p.Outstanding = streams + 1;
        for (int s = 0; s < streams; s++)
        {
            int stream = s;
            var buffer = mesh.GetVertexBuffer(stream);
            AsyncGPUReadback.Request(buffer, req => { p.Streams[stream] = Take(req, p); buffer.Dispose(); Arrived(p, gen); });
        }
        var indices = mesh.GetIndexBuffer();
        AsyncGPUReadback.Request(indices, req => { p.Indices = Take(req, p); indices.Dispose(); Arrived(p, gen); });
    }

    private static byte[] Take(AsyncGPUReadbackRequest req, Pending p)
    {
        if (req.hasError) { p.Failed = true; return null; }
        using (Timing.Measure("メッシュの写し")) return req.GetData<byte>().ToArray();
    }

    private void Arrived(Pending p, int gen)
    {
        if (--p.Outstanding > 0 || gen != _generation) return;
        _arrived.Enqueue((p, gen));
    }

    // この場面で作ったメッシュの hash（起動のたびに同じになっているかを、ログの照合の値で確かめる）
    private readonly HashSet<ulong> _meshHashes = new();

    // 読み戻しが届いて、組み立てを待っているメッシュ
    private readonly Queue<(Pending p, int gen)> _arrived = new();

    /// <summary>
    /// 毎フレーム（メインスレッド）。届いたメッシュを組み立てて仕事にする。1 フレームに budgetMs まで
    /// （届いたフレームに全部組み立てていた頃は、ステージの読み込みで 1 秒あまり固まった）。
    /// </summary>
    public void Pump(double budgetMs)
    {
        if (_arrived.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_arrived.Count > 0 && clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            var (p, gen) = _arrived.Dequeue();
            if (gen == _generation) Assemble(p, gen);
        }
    }

    private void Assemble(Pending p, int gen)
    {
        try
        {
            if (p.Failed) throw new InvalidOperationException("GPU からの読み戻しに失敗");
            using (Timing.Measure("メッシュの組み立て")) Decode(p, gen);
        }
        catch (Exception e)
        {
            _failed++;
            if (_failed <= 5) _log.LogWarning($"{p.Mesh.name} を Remix に渡せません: {e.Message}");
        }
        if (--_waiting == 0)
        {
            _log.LogInfo($"メッシュの読み戻しが終わりました: Remix のメッシュ {_meshJobs} 個、置く場所 {_instances} 個（渡せなかったメッシュ {_failed} 個）。hash の照合: メッシュ {_meshHashes.Count} 種 {_meshHashes.Aggregate(0UL, (a, b) => a ^ b):X16}、素材 {Materials.HashCheck}");
            SolveLamps(gen);
        }
    }

    /// <summary>読み戻したバイト列を頂点に直し、使われ方（サブメッシュの範囲と素材）ごとにメッシュの仕事にする。</summary>
    private void Decode(Pending p, int gen)
    {
        var mesh = p.Mesh;
        int n = mesh.vertexCount;
        var positions = ReadVectors(mesh, p.Streams, VertexAttribute.Position, n) ?? throw new InvalidOperationException("位置が無い");
        var normals = ReadVectors(mesh, p.Streams, VertexAttribute.Normal, n);
        var uv3 = ReadVectors(mesh, p.Streams, VertexAttribute.TexCoord0, n);
        var uvs = uv3?.Select(v => new Vector2(v.x, v.y)).ToArray();
        // 開発用（mod.uvrange = 名前の一部）: そのメッシュの UV の範囲を書く
        if (UvRangeOf != null && uvs != null && uvs.Length > 0 && mesh.name.IndexOf(UvRangeOf, StringComparison.OrdinalIgnoreCase) >= 0)
            _log.LogInfo($"UV の範囲 {mesh.name}（頂点 {n}）: u {uvs.Min(v => v.x):0.###}〜{uvs.Max(v => v.x):0.###}、v {uvs.Min(v => v.y):0.###}〜{uvs.Max(v => v.y):0.###}、箱 {mesh.bounds.size}");
        var allIndices = ReadIndices(mesh, p.Indices);
        if (!Lamps.Cached && p.Uses.Any(u => u.Lightmap >= 0 || u.Lamp != null))
            using (Timing.Measure("ライトマップの面")) KeepLightmapSurfaces(p, mesh, positions, allIndices, n);

        var map = new int[n];
        var stamp = new int[n];
        int stampNow = 0;
        var surfaceCache = new Dictionary<int, (Vector3[] pos, Vector3[] nrm, Vector2[] uv, int[] idx)>();

        // 同じサブメッシュの範囲と素材の組なら、同じ Remix のメッシュを置き回す
        foreach (var group in p.Uses.GroupBy(u => Key(u)))
        {
            var first = group.First();
            var surfaces = new List<SurfaceDesc>();
            for (int k = 0; k < first.SubCount; k++)
            {
                int sub = first.SubStart + k;
                var desc = mesh.GetSubMesh(sub);
                if (desc.topology != MeshTopology.Triangles) continue;
                if (!surfaceCache.TryGetValue(sub, out var geo))
                {
                    geo = Compact(desc, allIndices, positions, normals, uvs, map, stamp, ++stampNow);
                    surfaceCache[sub] = geo;
                }
                if (geo.idx.Length == 0 || (MaterialLibrary.IsTransparent(first.Materials[k]) && !MaterialLibrary.IsGlass(first.Materials[k]))) continue;
                bool board = first.Board && BakedLamps.IsBoardMaterial(first.Materials[k], first.Lamp ?? "");
                bool own = board || (first.Foliage && MaterialLibrary.IsLeaf(first.Materials[k]));
                var mat = Materials.Get(first.Materials[k], own ? first.RendererId : first.Reflective ? MaterialLibrary.ReflectiveVariant : 0, own ? first.Stable : 0);
                // ディテールの UV で描く素材（道路）は、ディテールの繰り返しの升目で切り分け、升目ごとの素材（粗さがその場所の物）で描く
                if (mat.Detail != null && geo.uv != null && SplitCells(geo.pos, geo.nrm, TransformUvs(geo.uv, mat), geo.idx) is { } cells)
                {
                    foreach (var cell in cells)
                        surfaces.Add(new SurfaceDesc
                        {
                            Positions = cell.Value.pos.ToArray(), Normals = cell.Value.nrm?.ToArray(), Uvs = cell.Value.uv.ToArray(), Indices = cell.Value.idx.ToArray(),
                            MaterialHash = Materials.GetCell(mat, cell.Key.i, cell.Key.j).Hash,
                        });
                    continue;
                }
                surfaces.Add(new SurfaceDesc
                {
                    Positions = geo.pos, Normals = geo.nrm, Uvs = TransformUvs(geo.uv, mat), Indices = geo.idx, MaterialHash = mat.Hash,
                });
            }
            if (surfaces.Count == 0) continue;
            var instances = group.Select(u => u.ToWorld).ToArray();
            Enqueue(new MeshJob
            {
                Hash = StableId.Handle(0x4D, StableId.Of(mesh), StableKey(first)), Surfaces = surfaces.ToArray(), Instances = instances,
                RendererIds = group.Select(u => u.RendererId).ToArray(), Overlay = first.Overlay,
                // 片面でよいのは、全部の素材が裏を描かず、鏡写し（行列式が負: 面の表裏が入れ替わる）の置き方も無いとき
                DoubleSided = !CullBackFaces || first.Overlay || instances.Any(m => m.determinant < 0f) ||
                              first.Materials.Take(first.SubCount).Any(m => m == null || !m.HasProperty("_Cull") || m.GetFloat("_Cull") != 2f || MaterialLibrary.IsGlass(m)),
            });
            _meshJobs++;
            _meshHashes.Add(StableId.Handle(0x4D, StableId.Of(mesh), StableKey(first)));
            _instances += instances.Length;
        }
    }

    /// <summary>
    /// 焼き込みの照明を光源に直すために、照明の素材の面（1 灯ずつに分けるため）と、照明の近くのライトマップのある面（明るさを読むため。不透明なサブメッシュだけ）を
    /// <see cref="BakedLamps"/> に渡す。
    /// </summary>
    private void KeepLightmapSurfaces(Pending p, Mesh mesh, Vector3[] positions, int[] allIndices, int n)
    {
        var uv2 = ReadVectors(mesh, p.Streams, VertexAttribute.TexCoord1, n) ?? ReadVectors(mesh, p.Streams, VertexAttribute.TexCoord0, n);
        var lmUv = uv2?.Select(v => new Vector2(v.x, v.y)).ToArray();
        var map = new int[n];
        var stamp = new int[n];
        int now = 0;
        // その Renderer のサブメッシュのうち、take が選んだ素材の面の頂点と、詰め直したインデックス
        (List<int> used, List<int> idx) Gather(Use u, Func<Material, bool> take)
        {
            var idx = new List<int>();
            var used = new List<int>();
            now++;
            for (int k = 0; k < u.SubCount; k++)
            {
                if (!take(u.Materials[k])) continue;
                var desc = mesh.GetSubMesh(u.SubStart + k);
                if (desc.topology != MeshTopology.Triangles) continue;
                for (int i = 0; i < desc.indexCount; i++)
                {
                    int v = allIndices[desc.indexStart + i] + desc.baseVertex;
                    if (stamp[v] != now) { stamp[v] = now; map[v] = used.Count; used.Add(v); }
                    idx.Add(map[v]);
                }
            }
            return (used, idx);
        }
        foreach (var use in p.Uses)
        {
            var u = use;
            if (u.Lamp != null)
            {
                var (used, idx) = Gather(u, m => BakedLamps.IsLampMaterial(m, u.Lamp));
                if (idx.Count > 0) Lamps.AddEmitter(u.RendererId, u.Lamp, used.Select(v => u.ToWorld.MultiplyPoint3x4(positions[v])).ToArray(), idx.ToArray());
            }
            if (u.Lightmap >= 0 && lmUv != null)
            {
                // 切り抜きの素材（木の葉）は、素材ごとに分けて渡す: 光を遮る物としては数えず、葉の暗さを素材ごとに決める（BakedLamps.Surface.Cutout）
                var solid = Gather(u, m => !MaterialLibrary.IsTransparent(m) && !BakedLamps.IsLampMaterial(m, u.Lamp ?? "") && !MaterialLibrary.IsCutout(m));
                if (solid.idx.Count > 0)
                    Lamps.AddSurface(u.RendererId, u.Lightmap, u.LightmapST, u.ToWorld, solid.used.Select(v => positions[v]).ToArray(), solid.used.Select(v => lmUv[v]).ToArray(), solid.idx.ToArray(), null, false);
                foreach (var cut in u.Materials.Take(u.SubCount).Distinct().Where(m => MaterialLibrary.IsCutout(m) && !MaterialLibrary.IsTransparent(m) && !BakedLamps.IsLampMaterial(m, u.Lamp ?? "")))
                {
                    var (used, idx) = Gather(u, m => m == cut);
                    if (idx.Count > 0)
                        Lamps.AddSurface(u.RendererId, u.Lightmap, u.LightmapST, u.ToWorld, used.Select(v => positions[v]).ToArray(), used.Select(v => lmUv[v]).ToArray(), idx.ToArray(), cut,
                            MaterialLibrary.IsLeaf(cut));
                }
                foreach (var board in u.Materials.Take(u.SubCount).Distinct().Where(m => BakedLamps.IsBoardMaterial(m, u.Lamp ?? "")))
                {
                    var (faceUsed, faceIdx) = Gather(u, m => m == board);
                    Lamps.AddBoardFace(board, u.RendererId, u.Lightmap, u.LightmapST, faceUsed.Select(v => u.ToWorld.MultiplyPoint3x4(positions[v])).ToArray(), faceUsed.Select(v => lmUv[v]).ToArray(), faceIdx.ToArray());
                }
            }
        }
    }

    /// <summary>メッシュの hash に混ぜる、使われ方の値（Key と同じ区別を、起動のたびに同じ値で）。</summary>
    private static ulong StableKey(Use u)
    {
        ulong h = StableId.Mix(StableId.Mix(1469598103934665603UL, (ulong)u.SubStart), (ulong)u.SubCount);
        for (int k = 0; k < u.SubCount; k++) h = StableId.Mix(h, StableId.Of(u.Materials[k]));
        if (u.Board || u.Foliage) h = StableId.Mix(h, u.Stable);
        if (u.Reflective) h = StableId.Mix(h, 1);
        return h;
    }

    private static int Key(Use u)
    {
        unchecked
        {
            int h = u.SubStart * 397 ^ u.SubCount;
            for (int k = 0; k < u.SubCount; k++) h = h * 31 + (u.Materials[k] != null ? u.Materials[k].GetInstanceID() : 0);
            if (u.Board || u.Foliage) h = h * 31 + u.RendererId;
            if (u.Reflective) h = h * 31 + 1;
            return h;
        }
    }

    /// <summary>サブメッシュ 1 つ分の頂点だけを取り出し、インデックスを詰め直す。</summary>
    private static (Vector3[], Vector3[], Vector2[], int[]) Compact(SubMeshDescriptor desc, int[] all, Vector3[] pos, Vector3[] nrm, Vector2[] uv,
        int[] map, int[] stamp, int now)
    {
        var idx = new int[desc.indexCount];
        var used = new List<int>();
        for (int i = 0; i < desc.indexCount; i++)
        {
            int v = all[desc.indexStart + i] + desc.baseVertex;
            if (stamp[v] != now) { stamp[v] = now; map[v] = used.Count; used.Add(v); }
            idx[i] = map[v];
        }
        var p = new Vector3[used.Count];
        var n = nrm != null ? new Vector3[used.Count] : null;
        var t = uv != null ? new Vector2[used.Count] : null;
        for (int i = 0; i < used.Count; i++)
        {
            p[i] = pos[used[i]];
            if (n != null) n[i] = nrm[used[i]];
            if (t != null) t[i] = uv[used[i]];
        }
        return (p, n, t, idx);
    }

    internal static int[] ReadIndices(Mesh mesh, byte[] data)
    {
        if (mesh.indexFormat == IndexFormat.UInt32)
        {
            var r = new int[data.Length / 4];
            Buffer.BlockCopy(data, 0, r, 0, r.Length * 4);
            return r;
        }
        var s = new int[data.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToUInt16(data, i * 2);
        return s;
    }

    /// <summary>頂点の属性を最大 3 成分の Vector3 で読む。無ければ null。</summary>
    internal static Vector3[] ReadVectors(Mesh mesh, byte[][] streams, VertexAttribute attr, int n)
    {
        if (!mesh.HasVertexAttribute(attr)) return null;
        int stream = mesh.GetVertexAttributeStream(attr);
        var data = streams[stream];
        int stride = mesh.GetVertexBufferStride(stream);
        int offset = mesh.GetVertexAttributeOffset(attr);
        var format = mesh.GetVertexAttributeFormat(attr);
        int dims = Math.Min(3, mesh.GetVertexAttributeDimension(attr));
        int size = FormatSize(format);
        var r = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            int at = i * stride + offset;
            var v = Vector3.zero;
            for (int c = 0; c < dims; c++) v[c] = ReadComponent(data, at + c * size, format);
            r[i] = v;
        }
        return r;
    }

    private static int FormatSize(VertexAttributeFormat f) => f switch
    {
        VertexAttributeFormat.Float32 or VertexAttributeFormat.UInt32 or VertexAttributeFormat.SInt32 => 4,
        VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16
            or VertexAttributeFormat.UInt16 or VertexAttributeFormat.SInt16 => 2,
        _ => 1,
    };

    private static float ReadComponent(byte[] d, int at, VertexAttributeFormat f) => f switch
    {
        VertexAttributeFormat.Float32 => BitConverter.ToSingle(d, at),
        VertexAttributeFormat.Float16 => Mathf.HalfToFloat(BitConverter.ToUInt16(d, at)),
        VertexAttributeFormat.UNorm16 => BitConverter.ToUInt16(d, at) / 65535f,
        VertexAttributeFormat.SNorm16 => Math.Max(BitConverter.ToInt16(d, at) / 32767f, -1f),
        VertexAttributeFormat.UNorm8 => d[at] / 255f,
        VertexAttributeFormat.SNorm8 => Math.Max((sbyte)d[at] / 127f, -1f),
        _ => throw new NotSupportedException($"頂点の形式 {f}"),
    };

    /// <summary>
    /// 素材のタイリング（Unity の TRANSFORM_TEX: uv × scale + offset）を UV に焼き込む。Remix の素材には UV を動かす欄が無いため。
    /// URP Lit は _BaseMap_ST をノーマルマップなどにも使うので、1 つで足りる。
    /// </summary>
    /// <summary>
    /// 三角形を、UV の整数の升目（ディテールの繰り返し 1 つ分）で切り分ける。升目 (i, j) ごとに、頂点（UV は升目の中の 0〜1）とインデックスを返す。
    /// 升目が多すぎる（4096 個を超える）メッシュは null（切り分けない）。
    /// </summary>
    private static Dictionary<(int i, int j), (List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<int> idx)> SplitCells(Vector3[] pos, Vector3[] nrm, Vector2[] uv, int[] idx)
    {
        if (uv.Length == 0 || idx.Length == 0) return null;
        Vector2 min = uv[0], max = uv[0];
        foreach (var t in uv) { min = Vector2.Min(min, t); max = Vector2.Max(max, t); }
        if (!(max.x - min.x < 1e4f && max.y - min.y < 1e4f) || (Mathf.Ceil(max.x) - Mathf.Floor(min.x) + 1f) * (Mathf.Ceil(max.y) - Mathf.Floor(min.y) + 1f) > 4096f) return null;
        var cells = new Dictionary<(int, int), (List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<int> idx)>();
        var poly = new List<(Vector3 p, Vector3 n, Vector2 t)>(8);
        var next = new List<(Vector3 p, Vector3 n, Vector2 t)>(8);
        // 多角形を、UV の 1 本の線（axis = 0: u、1: v）のどちら側かで切る（Sutherland–Hodgman）
        void Clip(int axis, float value, bool keepGreater)
        {
            next.Clear();
            for (int k = 0; k < poly.Count; k++)
            {
                var a = poly[k];
                var b = poly[(k + 1) % poly.Count];
                float da = (axis == 0 ? a.t.x : a.t.y) - value, db = (axis == 0 ? b.t.x : b.t.y) - value;
                if (!keepGreater) { da = -da; db = -db; }
                if (da >= 0f) next.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float f = da / (da - db);
                    next.Add((Vector3.Lerp(a.p, b.p, f), Vector3.Lerp(a.n, b.n, f), Vector2.Lerp(a.t, b.t, f)));
                }
            }
            (poly, next) = (next, poly);
        }
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            Vector2 lo = Vector2.Min(uv[a], Vector2.Min(uv[b], uv[c])), hi = Vector2.Max(uv[a], Vector2.Max(uv[b], uv[c]));
            int i0 = Mathf.FloorToInt(lo.x), i1 = Mathf.Max(Mathf.CeilToInt(hi.x) - 1, i0), j0 = Mathf.FloorToInt(lo.y), j1 = Mathf.Max(Mathf.CeilToInt(hi.y) - 1, j0);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    poly.Clear();
                    poly.Add((pos[a], nrm != null ? nrm[a] : default, uv[a]));
                    poly.Add((pos[b], nrm != null ? nrm[b] : default, uv[b]));
                    poly.Add((pos[c], nrm != null ? nrm[c] : default, uv[c]));
                    if (i0 != i1 || j0 != j1)
                    {
                        Clip(0, i, true); Clip(0, i + 1, false); Clip(1, j, true); Clip(1, j + 1, false);
                        if (poly.Count < 3) continue;
                    }
                    if (!cells.TryGetValue((i, j), out var cell))
                        cells[(i, j)] = cell = (new List<Vector3>(), nrm != null ? new List<Vector3>() : null, new List<Vector2>(), new List<int>());
                    int start = cell.pos.Count;
                    var origin = new Vector2(i, j);
                    foreach (var v in poly)
                    {
                        cell.pos.Add(v.p);
                        cell.nrm?.Add(v.n.normalized);
                        cell.uv.Add(v.t - origin);
                    }
                    for (int k = 1; k + 1 < poly.Count; k++) { cell.idx.Add(start); cell.idx.Add(start + k); cell.idx.Add(start + k + 1); }
                }
        }
        return cells.Count > 0 ? cells : null;
    }

    internal static Vector2[] TransformUvs(Vector2[] uvs, MaterialLibrary.MaterialDesc mat)
    {
        if (uvs == null || (mat.UvScale == Vector2.one && mat.UvOffset == Vector2.zero)) return uvs;
        var r = new Vector2[uvs.Length];
        for (int i = 0; i < r.Length; i++) r[i] = Vector2.Scale(uvs[i], mat.UvScale) + mat.UvOffset;
        return r;
    }

    /// <summary>Remix の取っ手にする 0 以外の 64 ビット。種類・世代・Unity の ID・使われ方を混ぜる。</summary>
    internal static ulong Hash(byte kind, int gen, int id, int key)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            foreach (var x in new[] { kind, (ulong)(uint)gen, (ulong)(uint)id, (ulong)(uint)key }) { h ^= x; h *= 1099511628211UL; }
            return h == 0 ? 1 : h;
        }
    }
}
