using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// キャラクター（SkinnedMeshRenderer）を Remix に渡す。メインスレッドで動き、仕事は <see cref="SceneGeometry.Jobs"/> に積む。
/// 形が毎フレーム変わるので、スキニングのあとの頂点（SkinnedMeshRenderer.GetVertexBuffer）を毎フレーム GPU から読み戻す。
/// インデックス・UV・サブメッシュは変わらないので、場面に入ったときに元のメッシュから 1 回だけ読む。
/// Remix には、最初は新しいメッシュとして、2 回目からは同じ hash で形だけ差し替えて（MeshInfoRefreshGeometryEXT）渡す。
/// </summary>
internal sealed class SkinnedGeometry
{
    /// <summary>キャラクター 1 体分の、このフレームの形と置き場所。</summary>
    internal sealed class SkinnedJob : SceneGeometry.Job
    {
        public ulong Hash;
        public SceneGeometry.SurfaceDesc[] Surfaces;
        public Matrix4x4 ToWorld;
        public bool Refresh;
    }

    private sealed class Entry
    {
        public SkinnedMeshRenderer Renderer;
        public ulong Hash;
        public Material[] Materials;
        // 元のメッシュから読んだ、変わらない部分。サブメッシュごとに「使う元の頂点の番号」と「詰め直したインデックス」
        public int[][] Used, Indices;
        public Vector2[] Uvs;
        public bool Ready, InFlight, Created, Failed;
        public int SurfaceCount, Version;
    }

    private readonly SceneGeometry _scene;
    private readonly ManualLogSource _log;
    private readonly List<Entry> _entries = new();
    private readonly HashSet<int> _known = new();

    /// <summary>
    /// 今見えている（Renderer が有効で、GameObject が出ている）キャラクターの部品の hash。描画スレッドはこれだけを描く。
    /// 服が破れる演出は、破れる前と後の部品の出し入れなので、見えない部品を描かないことで追いかける。
    /// </summary>
    public volatile ulong[] Visible = Array.Empty<ulong>();

    private int _generation;
    private bool _loggedLayout;
    private readonly HashSet<string> _loggedShaders = new();
    private readonly List<Material> _materials = new();

    public SkinnedGeometry(SceneGeometry scene, ManualLogSource log)
    {
        _scene = scene;
        _log = log;
    }

    /// <summary>場面の SkinnedMeshRenderer を集め直す。<see cref="SceneGeometry.Rebuild"/> のすぐあとに呼ぶ（同じ世代の番号を使う）。</summary>
    public void Rebuild(int gen)
    {
        _generation = gen;
        _entries.Clear();
        _known.Clear();
        Visible = Array.Empty<ulong>();
        int added = Rescan();
        if (added > 0) _log.LogInfo($"キャラクター（SkinnedMeshRenderer）{added} 個を Remix に渡します");
        foreach (var m in _entries.SelectMany(e => e.Materials).Where(m => m != null && m.shader != null).GroupBy(m => m.shader.name).Select(g => g.First()))
        {
            if (!_loggedShaders.Add(m.shader.name)) continue;
            var textures = m.GetTexturePropertyNames().Select(n => $"{n}={(m.GetTexture(n) != null ? m.GetTexture(n).name : "-")}");
            _log.LogInfo($"シェーダー {m.shader.name}（素材 {m.name}）のテクスチャ: {string.Join(", ", textures)}");
        }
    }

    /// <summary>
    /// まだ知らない SkinnedMeshRenderer を足す（場面に入ったあとに出てくる部品・キャラクター）。足した数を返す。
    /// 無効な GameObject の物は数えない（出てきたときに、次の見直しで足す）。
    /// </summary>
    public int Rescan()
    {
        int added = 0;
        foreach (var r in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
        {
            var mesh = r.sharedMesh;
            if (mesh == null || !_known.Add(r.GetInstanceID())) continue;
            // キャラクターはゲーム自身がトゥーンで描き直す（RemixCompositePass）。Remix では影だけ
            r.renderingLayerMask |= RemixCompositePass.OverlayBit;
            // トゥーンの「影の層」（mesh_hairshadow・mesh_face_shadow: 素材 m_hair_shadow など）は、髪が顔に落とす影を描いた作り物。
            // Remix は本当の影を出すので渡さない（渡すと、おでこに明るい四角、目に暗いかげりが出た）
            if (r.sharedMaterials.Any(m => m != null && m.name.IndexOf("_shadow", StringComparison.OrdinalIgnoreCase) >= 0 && m.shader != null && m.shader.name.Contains("Toon"))) continue;
            // Remix に渡す面が 1 つも無い部品（頬の赤み mesh_face_blush_low / high・涙: 半透明で、ゲーム自身が描く）は、読み戻しもしない
            if (r.sharedMaterials.All(m => m == null || (MaterialLibrary.IsTransparent(m) && !MaterialLibrary.IsBlendedToon(m)))) continue;
            var e = new Entry { Renderer = r, Hash = SceneGeometry.Hash(0x53, _generation, r.GetInstanceID(), 0), Materials = r.sharedMaterials };
            _entries.Add(e);
            RequestStatic(e, mesh, _generation);
            added++;
        }
        return added;
    }

    /// <summary>毎フレーム。見えている部品の一覧を作り、見えているキャラクターのスキニングのあとの頂点を読み戻す（1 つにつき読み戻し中は 1 つまで）。</summary>
    public void Update()
    {
        var visible = new List<ulong>();
        foreach (var e in _entries)
        {
            if (e.Renderer == null) continue;
            // 服の素材の差し替え（透ける服・下着など、OutfitSwitcher が sharedMaterials を入れ替える）。次に組む形から新しい素材を使う。
            // shortcut: 透けていく途中の _Tweak_transparency の変化は追いかけない（素材は最初に見たときの値のまま）
            e.Renderer.GetSharedMaterials(_materials);
            if (!_materials.SequenceEqual(e.Materials)) e.Materials = _materials.ToArray();
            if (e.Created && e.Renderer.enabled && e.Renderer.gameObject.activeInHierarchy) visible.Add(e.Hash);
        }
        if (!visible.SequenceEqual(Visible)) Visible = visible.ToArray();

        foreach (var e in _entries)
        {
            if (!e.Ready || e.InFlight || e.Failed || e.Renderer == null || !e.Renderer.isVisible) continue;
            var buffer = e.Renderer.GetVertexBuffer();
            if (buffer == null) continue;
            e.InFlight = true;
            int gen = _generation;
            AsyncGPUReadback.Request(buffer, req =>
            {
                int stride = buffer.stride;
                buffer.Dispose();
                e.InFlight = false;
                if (gen != _generation || e.Renderer == null) return;
                if (req.hasError) return;
                try { Arrived(e, req.GetData<float>(), stride / 4); }
                catch (Exception ex) { e.Failed = true; _log.LogWarning($"{e.Renderer.name} を Remix に渡せません: {ex.Message}"); }
            });
        }
    }

    private void RequestStatic(Entry e, Mesh mesh, int gen)
    {
        int streams = mesh.vertexBufferCount;
        var data = new byte[streams][];
        byte[] indexData = null;
        int outstanding = streams + 1;
        bool failed = false;
        void Done()
        {
            if (--outstanding > 0 || gen != _generation) return;
            if (failed) { e.Failed = true; return; }
            try { PrepareStatic(e, mesh, data, indexData); }
            catch (Exception ex) { e.Failed = true; _log.LogWarning($"{e.Renderer.name} の元のメッシュを読めません: {ex.Message}"); }
        }
        for (int s = 0; s < streams; s++)
        {
            int stream = s;
            var buffer = mesh.GetVertexBuffer(stream);
            AsyncGPUReadback.Request(buffer, req => { if (req.hasError) failed = true; else data[stream] = req.GetData<byte>().ToArray(); buffer.Dispose(); Done(); });
        }
        var ib = mesh.GetIndexBuffer();
        AsyncGPUReadback.Request(ib, req => { if (req.hasError) failed = true; else indexData = req.GetData<byte>().ToArray(); ib.Dispose(); Done(); });
    }

    /// <summary>サブメッシュごとに、使う頂点の番号と詰め直したインデックスを作っておく（毎フレームの組み立てを軽くするため）。</summary>
    private static void PrepareStatic(Entry e, Mesh mesh, byte[][] streams, byte[] indexData)
    {
        int n = mesh.vertexCount;
        var all = SceneGeometry.ReadIndices(mesh, indexData);
        var uv3 = SceneGeometry.ReadVectors(mesh, streams, VertexAttribute.TexCoord0, n);
        e.Uvs = uv3?.Select(v => new Vector2(v.x, v.y)).ToArray();
        int subs = Math.Min(mesh.subMeshCount, e.Materials.Length);
        e.Used = new int[subs][];
        e.Indices = new int[subs][];
        var map = new int[n];
        var stamp = new int[n];
        for (int s = 0; s < subs; s++)
        {
            var desc = mesh.GetSubMesh(s);
            if (desc.topology != MeshTopology.Triangles) { e.Used[s] = Array.Empty<int>(); e.Indices[s] = Array.Empty<int>(); continue; }
            var used = new List<int>();
            var idx = new int[desc.indexCount];
            for (int i = 0; i < desc.indexCount; i++)
            {
                int v = all[desc.indexStart + i] + desc.baseVertex;
                if (stamp[v] != s + 1) { stamp[v] = s + 1; map[v] = used.Count; used.Add(v); }
                idx[i] = map[v];
            }
            e.Used[s] = used.ToArray();
            e.Indices[s] = idx;
        }
        e.Ready = true;
    }

    /// <summary>
    /// 読み戻したスキニングのあとの頂点を面に組んで、仕事にする。
    /// 頂点の並びは 位置(float3)、法線(float3)、接線(float4) の順と見て、1 頂点の float の数（strideFloats）から法線の有無を決める。
    /// </summary>
    private void Arrived(Entry e, Unity.Collections.NativeArray<float> data, int strideFloats)
    {
        int n = data.Length / strideFloats;
        bool hasNormals = strideFloats >= 6;
        if (!_loggedLayout)
        {
            _loggedLayout = true;
            _log.LogInfo($"スキニングのあとの頂点: 1 頂点 {strideFloats * 4} バイト（{e.Renderer.name}、頂点 {n}）");
        }

        var surfaces = new List<SceneGeometry.SurfaceDesc>();
        for (int s = 0; s < e.Used.Length; s++)
        {
            var used = e.Used[s];
            var material = s < e.Materials.Length ? e.Materials[s] : null;
            // 半透明の部品（頬の赤み・髪の先・まつ毛など）も渡す: キャラクターは Remix では影と映り込みにだけ出るが、渡さないと映り込みの中でその部品が欠けた。
            // 絵を持つトゥーン（UTS）の素材だけ（涙のように、ふだんは見えない部品を不透明で出さないため）
            if (used.Length == 0 || (MaterialLibrary.IsTransparent(material) && !MaterialLibrary.IsBlendedToon(material))) continue;
            var pos = new Vector3[used.Length];
            var nrm = hasNormals ? new Vector3[used.Length] : null;
            var uv = e.Uvs != null ? new Vector2[used.Length] : null;
            for (int i = 0; i < used.Length; i++)
            {
                int v = used[i];
                if (v >= n) throw new InvalidOperationException($"頂点の番号 {v} が読み戻した数 {n} を超えている");
                int at = v * strideFloats;
                pos[i] = new Vector3(data[at], data[at + 1], data[at + 2]);
                if (nrm != null) nrm[i] = new Vector3(data[at + 3], data[at + 4], data[at + 5]);
                if (uv != null) uv[i] = e.Uvs[v];
            }
            var mat = _scene.Materials.Get(material);
            surfaces.Add(new SceneGeometry.SurfaceDesc
            {
                Positions = pos, Normals = nrm, Uvs = SceneGeometry.TransformUvs(uv, mat), Indices = e.Indices[s], MaterialHash = mat.Hash,
            });
        }
        if (surfaces.Count == 0) return;
        // 素材の入れ替えで半透明の面が増減すると、形の差し替え（面の数が同じときだけ効く）が捨てられるので、新しいメッシュとして作り直す
        if (e.Created && surfaces.Count != e.SurfaceCount)
        {
            e.Hash = SceneGeometry.Hash(0x53, _generation, e.Renderer.GetInstanceID(), ++e.Version);
            e.Created = false;
        }
        e.SurfaceCount = surfaces.Count;
        _scene.Enqueue(new SkinnedJob { Hash = e.Hash, Surfaces = surfaces.ToArray(), ToWorld = ToWorld(e), Refresh = e.Created });
        e.Created = true;
    }

    /// <summary>
    /// スキニングのあとの頂点の座標。Unity は SkinnedMeshRenderer を、ルートボーン（無ければ自分）の行列で描く（localBounds もこの座標）。
    /// このゲームは Renderer.bounds を 10000 m の箱にしている（カリングよけ）ので、箱を比べて決めることはできない。
    /// </summary>
    private static Matrix4x4 ToWorld(Entry e) => (e.Renderer.rootBone != null ? e.Renderer.rootBone : e.Renderer.transform).localToWorldMatrix;

}
