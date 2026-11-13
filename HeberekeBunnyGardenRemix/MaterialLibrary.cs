using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeberekeBunnyGardenRemix.Remix;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// Unity の素材を Remix の素材の説明（<see cref="MaterialDesc"/>）にする。メインスレッドで動き、仕事は <see cref="SceneGeometry.Jobs"/> に積む。
/// Remix がそのまま読めない絵は、ここで並べ替えた絵を作って渡す。
/// ・ノーマルマップ: Unity の DXT5nm は X がアルファにある（UnpackNormal の x *= w）。Remix の NORMAL_TANGENT_XY は R と G を読むので、R=X、G=Y にする
/// ・なめらかさ: URP は _MetallicGlossMap のアルファ（× _Smoothness）。Remix は SMOOTHNESS_IN_RED なので R に置く
/// 並べ替えは GPU で縮めて（Graphics.Blit）読み戻し、CPU で並べ替えてから作り直す。出来上がったら、同じ hash の素材を送り直す（Remix はその場で更新する）。
/// </summary>
internal sealed class MaterialLibrary
{
    internal sealed class MaterialJob : SceneGeometry.Job
    {
        public ulong Hash;
        public MaterialParams Params;
        public SceneGeometry.TextureDesc Albedo, Normal, Roughness, Metallic, Emissive;
        public float BaseRoughness; // 下限を掛ける前の粗さ（テクスチャがあるときは下限そのもの）
        public bool NoFloor;        // 粗さの下限を掛けない（ガラス: 素材のなめらかさのまま、周りを映す）
        public List<MaterialJob> Cells; // この素材から作った升目の素材（GetCell）。この素材を送り直すとき、絵と色を写して一緒に送り直す
        public bool Wet;            // 濡れた路面（升目の素材）: 金属の反射の色を WetMetalTint にする
        public bool Reflective;     // 反射プローブの箱の中の物（ReflectiveVariant）: 金属の反射の色を明るめにする
        public float Shade = 1f;    // 地の色に掛ける倍率（線形）。木の葉など、焼き込みでは葉どうしの陰で暗い物（SetShade）

        /// <summary>描画スレッドに渡す写し（メインスレッドが並べ替えの結果を書き足していくので、渡した物は変えない）。</summary>
        public MaterialJob Snapshot() => (MaterialJob)MemberwiseClone();
    }

    /// <summary>メッシュを組むときに使う、素材の側の情報。</summary>
    internal sealed class MaterialDesc
    {
        public ulong Hash;
        public Vector2 UvScale = Vector2.one, UvOffset = Vector2.zero; // Unity の _BaseMap_ST。UV に焼き込む
        public MaterialJob Job;
        public DetailGloss Detail; // ディテールの UV で描く素材の、金属・なめらかさの絵（升目ごとの素材を作るのに使う: GetCell）。無ければ null
    }

    /// <summary>ディテールの UV で描く素材（道路）の、地の UV で貼る金属・なめらかさの絵と、升目の素材を作るのに要る値。</summary>
    internal sealed class DetailGloss
    {
        public Material Source;
        public int Variant;
        public ulong VariantId;
        public Texture2D Gloss;
        public float Smooth;
        public bool FromAlbedoAlpha;
        public Vector2 Scale, Offset; // _DetailAlbedoMap_ST（地の UV → ディテールの UV）
    }

    // 並べ替えた絵の大きさの上限。読み戻しと作り直しを CPU でするので、大きくしすぎない
    private const int MaxConvertedSize = 1024;

    private readonly SceneGeometry _scene;
    private readonly ManualLogSource _log;
    // variant: 同じ Unity の素材を、Renderer ごとに別の Remix の素材にするときの番号（看板: 置いてある場所ごとに焼き込みの明るさが違う）。ふつうは 0
    private readonly Dictionary<(Material, int variant), MaterialDesc> _byMaterial = new();
    private readonly List<Texture2D> _made = new();
    // 前の場面で作った絵。描画スレッドがまだ使っているかもしれないので、もう 1 つ先の場面で捨てる
    private readonly List<Texture2D> _retired = new();
    private int _generation, _converting;

    /// <summary>
    /// 粗さの下限（mod.roughmin）。このゲームの URP Lit はなめらかさが 1 に近く、ゲームでは暗い反射プローブしか映らないので艶消しに見えるが、
    /// パストレースでは本当に周りが映って鏡のようになる。見た目を合わせるため、下限を設ける。
    /// </summary>
    public float RoughnessFloor = DefaultRoughnessFloor;
    // 0.35 だった。かずくんの希望「テカリのある物は反射して欲しい」（2026-11-10）で下げた。粗さを絵で持つ素材には効かない（Remix は絵を使う）
    public const float DefaultRoughnessFloor = 0.1f;

    /// <summary>粗さの下限を変えたので、作った素材を全部送り直す。</summary>
    public void ResendAll()
    {
        foreach (var d in _byMaterial.Values)
        {
            ApplyFloor(d.Job);
            Send(d.Job);
        }
    }

    /// <summary>
    /// 金属の地図の金属の所の、反射の色（15 分のいくつ、ガンマ。mod.metaltint）。4 で F0 がおよそ 0.055（ガラスより少し映る、暗い金属）。
    /// マンホールの真ん中（金属）を HDR で測ると、ゲーム 0.007、1 で 0.04、6 で 0.17、15 で 1.1（真上の環境光の円錐が映る）、塗装の所は 0.18
    /// </summary>
    public int MetalTint = 4;

    /// <summary>光る面の強さの倍率（mod.emissivescale）。</summary>
    public float EmissiveScale = 1f;

    private void Send(MaterialJob job)
    {
        var snap = job.Snapshot();
        snap.Params.Emission *= EmissiveScale;
        if (job.Shade != 1f)
        {
            var shaded = Gamma(snap.Params.Albedo.linear * job.Shade);
            shaded.a = snap.Params.Albedo.a;
            snap.Params.Albedo = shaded;
        }
        if (snap.Params.MetalTint > 0) snap.Params.MetalTint = job.Wet ? WetMetalTint : job.Reflective ? ReflectiveMetalTint : MetalTint;
        _scene.Enqueue(snap);
        // 升目の素材は、絵と色がこの素材と同じ（粗さだけ別）。あとから届いた絵（ノーマルなど）や色も写す
        if (job.Cells == null) return;
        foreach (var cell in job.Cells)
        {
            cell.Albedo = job.Albedo;
            cell.Normal = job.Normal;
            cell.Emissive = job.Emissive;
            cell.Params.Albedo = job.Params.Albedo;
            cell.Params.Emission = job.Params.Emission;
            cell.Shade = job.Shade;
            Send(cell);
        }
    }

    /// <summary>
    /// 濡れた路面（升目の素材の金属の所）は、Remix では水の膜: 反射の色（15 分のいくつ。mod.wettint）は水の F0（0.02〜0.03）に近い 3（F0 0.033）、
    /// なめらかさは WetSmoothness（ほぼ鏡）。ゲームの濡れた所は金属の扱いで拡散が 0、なめらかさは 0.6〜0.72（反射はぼける）。
    /// ゲームのなめらかさのまま映すと、街灯や看板がぼけた一面のにじみになり、凹凸の暗い側が埋まってのっぺりした（6 = F0 0.13 で底が 0.145、ゲーム 0.015）。
    /// それで一度 1（F0 0.005）まで下げたが、映り込みがほぼ消えて「RTX の意味がない」（かずくん、2026-11-11）。鏡に近ければ、映るのは看板や窓の形だけで、
    /// ほかは暗い空を映して暗いまま。ザラザラ（平行光源のハイライト）はゲーム自身に描き足させる（SunHighlight）
    /// </summary>
    public int WetMetalTint = 3;
    /// <summary>濡れた所の、Remix でのなめらかさ（升目の絵を作るときに使う。次に読む場面から効く: mod.wetsmooth）。</summary>
    public static float WetSmoothness = 0.95f;

    /// <summary>
    /// ディテールの UV で描く素材（道路）の、ディテールの繰り返し 1 つ分（升目 i, j）の素材。絵と色は元の素材と同じで、粗さだけ、
    /// 金属・なめらかさの絵のその升目に当たる部分（地の UV で 1 / 繰り返し の大きさ）を切り出して持つ。
    /// ゲームの道路は、ひび割れ（ディテール、32 倍の細かさ）と、濡れた所（金属・なめらかさの絵、地の UV）を別々の UV で貼る。Remix の素材は UV が 1 つなので、
    /// 粗さを平均の一定値にしていた頃は、濡れた所（Stage01 のスタート地点: 月明かりを照り返してひび割れがきらつく）が消えていた。
    /// メッシュを升目で切り分け（SceneGeometry.SplitCells）、升目ごとにこの素材を使う。升目は素材の UV で決まるので、メッシュが違っても同じ升目は同じ素材。
    /// </summary>
    public MaterialDesc GetCell(MaterialDesc source, int i, int j)
    {
        var d = source.Detail;
        int variant = unchecked((d.Variant * 486187739 + i) * 16777619 + j * 31 + 0x5EED);
        if (_byMaterial.TryGetValue((d.Source, variant), out var known)) return known;
        int gen = _generation;
        var job = source.Job.Snapshot();
        job.Cells = null;
        job.Hash = StableId.Handle(0x41, StableId.Of(d.Source), StableId.Mix(StableId.Mix(StableId.Mix(d.VariantId, 0xCE11), unchecked((ulong)(long)i)), unchecked((ulong)(long)j)));
        // 金属は絵の R から（暗い金属の道: Describe の金属の地図と同じ）、なめらかさは G。地図があるので、粗さの定数は下限
        job.Params.Metallic = 0f;
        job.Params.MetalTint = MetalTint;
        job.Wet = true;
        // 升目の中の UV は 0〜1。折り返すと、粗さの絵（その升目の分だけを切り出した物）の端が反対側の端と混ざり、升目の境目が線になって見えた
        job.Params.Clamp = true;
        job.BaseRoughness = 0f;
        ApplyFloor(job);
        var desc = new MaterialDesc { Hash = job.Hash, Job = job, UvScale = source.UvScale, UvOffset = source.UvOffset };
        var region = new Rect((i - d.Offset.x) / d.Scale.x, (j - d.Offset.y) / d.Scale.y, 1f / d.Scale.x, 1f / d.Scale.y);
        float smooth = d.Smooth, wet = WetSmoothness;
        bool fromAlbedo = d.FromAlbedoAlpha;
        Convert(d.Gloss, FormattableString.Invariant($"cell:{region.x:0.#####},{region.y:0.#####},{region.width:0.#####},{region.height:0.#####}:{(fromAlbedo ? 1 : 0)}:{smooth:0.####}:{wet:0.###}"), job, desc,
            (px, k) =>
            {
                // 濡れた所（R = 金属）は、水の膜のなめらかさに寄せる
                float sm = fromAlbedo ? smooth : px[k + 3] / 255f * smooth;
                px[k + 1] = (byte)Mathf.RoundToInt(255f * Mathf.Lerp(sm, wet, px[k] / 255f));
                px[k + 2] = 0; px[k + 3] = 255;
            },
            (t, cell) => cell.Roughness = TextureOf(t, gen, RemixApi.EncodingSmoothnessInGreen), region);
        (source.Job.Cells ??= new List<MaterialJob>()).Add(job);
        _byMaterial[(d.Source, variant)] = desc;
        Send(job);
        return desc;
    }

    /// <summary>ディテール（_DETAIL_MULX2）が地の絵よりずっと細かい素材（道路のアスファルト）。Remix にはディテールの UV で渡す（Describe）。</summary>
    public static bool IsDetailFirst(Material m) =>
        m != null && m.IsKeywordEnabled("_DETAIL_MULX2") && m.HasProperty("_DetailAlbedoMap") && m.HasProperty("_DetailNormalMap")
        && (m.GetTexture("_DetailAlbedoMap") is Texture2D || m.GetTexture("_DetailNormalMap") is Texture2D) && Mathf.Abs(m.GetTextureScale("_DetailAlbedoMap").x) >= 8f;

    /// <summary>
    /// 道路の、リアルタイムの光のハイライトだけを描く素材（ゲームの素材の写し）。RemixCompositePass が、Remix の絵の上に加算で描く。
    /// ゲームの道路のザラザラ・ひび割れのきらつきは、ほぼ全部「Mixed の平行光源のハイライトが、ディテールのノーマルで細かく明暗に分かれた物」:
    /// その光を消すと道路はのっぺりした暗い面になり、ノーマルの強さを 0 にすると一面の白い照り返しになる（2026-11-11、mod.lightint / mod.matset で切り分けた）。
    /// Remix でも同じ光のハイライトは出るが、強さがゲームの 3 分の 1 ほどでぼやける（URP の式は物理の GGX の約 2 倍の山、Remix はノイズ除去がならす）うえ、
    /// 街灯の映り込みで暗い側が埋まる。Remix の光には「この光だけ映す」指定が無いので、ハイライトはゲームのシェーダーそのものに描かせる。
    /// 写しの中身: 加算（One One、深さは書かない）。金属の絵の R を 1 にして拡散を 0 にし（ライトマップも平行光源の拡散も出ない）、
    /// 地の絵は「F0 の絵」（金属の所は地の色、そうでない所は 0.04 になる値）に差し替える。なめらかさ・ノーマル・ディテール・影はゲームのまま。
    /// </summary>
    internal sealed class SunHighlight
    {
        public Material Source, Clone;
        public uint Bit;   // この素材だけで出来た Renderer に付ける印（renderingLayerMask）
        public int Pass;   // ForwardLit
        public int Pending = 2;
        public bool Ready => Pending == 0;
    }

    private readonly List<SunHighlight> _highlights = new();
    public IReadOnlyList<SunHighlight> Highlights => _highlights;
    // 印に使う桁（27〜30）。足りなくなったら、その素材のハイライトは描き足さない
    private const int HighlightFirstBit = 27, HighlightBits = 4;
    /// <summary>トゥーンの素材の、発光の強さ（地の絵に掛ける。mod.toonglow）と、地の色の倍率（線形。mod.toonalbedo）。次に読む場面から効く。</summary>
    public static float ToonGlow = 0.85f, ToonAlbedo = 0.2f;

    /// <summary>開発用（mod.sunhighlight = 0）: ハイライトの描き足しをやめる。</summary>
    public static bool SunHighlights = true;

    /// <summary>この素材のハイライトを描き足すための印。描き足さない素材は 0。初めての素材なら、写しの素材を作る。</summary>
    public uint HighlightBit(Material m)
    {
        if (!IsDetailFirst(m) || !m.IsKeywordEnabled("_METALLICSPECGLOSSMAP") || !m.HasProperty("_MetallicGlossMap") || m.GetTexture("_MetallicGlossMap") is not Texture2D gloss
            || !m.HasProperty("_BaseMap") || m.GetTexture("_BaseMap") is not Texture2D baseTex) return 0;
        foreach (var known in _highlights) if (known.Source == m) return known.Bit;
        int pass = m.FindPass("ForwardLit");
        if (pass < 0 || _highlights.Count >= HighlightBits) return 0;
        var clone = new Material(m) { name = m.name + "_RemixSunHighlight", hideFlags = HideFlags.HideAndDontSave };
        clone.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        clone.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        clone.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
        clone.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        clone.SetFloat("_ZWrite", 0f);
        // 遮蔽を 0 にして、ゲームの映り込み（反射プローブ）を描かない（URP の GlobalIllumination は映り込みと環境光に遮蔽を掛ける。平行光源のハイライトには掛けない）。
        // 映り込みは Remix が本物を映す。描いていた頃は、低い視点で濡れた所にゲームの赤っぽい反射プローブが乗り、Remix の映り込みを覆っていた（2026-11-11）
        clone.EnableKeyword("_OCCLUSIONMAP");
        clone.SetTexture("_OcclusionMap", Texture2D.blackTexture);
        clone.SetFloat("_OcclusionStrength", 1f);
        var h = new SunHighlight { Source = m, Clone = clone, Bit = 1u << (HighlightFirstBit + _highlights.Count), Pass = pass };
        _highlights.Add(h);
        _log.LogInfo($"ハイライトを描き足す素材: {m.name}（印 0x{h.Bit:X}、パス {pass}）");

        int gen = _generation;
        float f = Mathf.Min(1f, (float)MaxConvertedSize / Mathf.Max(gloss.width, gloss.height));
        int w = Mathf.Max(4, Mathf.RoundToInt(gloss.width * f)), height = Mathf.Max(4, Mathf.RoundToInt(gloss.height * f));
        var tint = m.GetColor("_BaseColor").linear;
        // 名前は別のスレッドから読めないので、先に取っておく
        string f0Name = baseTex.name + "_remix_f0", metalName = gloss.name + "_remix_allmetal";
        byte[] glossPx = null, basePx = null;
        void Both()
        {
            if (glossPx == null || basePx == null) return;
            _converting += 2;
            Background(() =>
            {
                // 地の絵は線形の値で読み戻してある（線形の絵として作るので、シェーダーにはそのまま線形の色として届く）
                // shortcut: ディテールの色（× 2）は平均 1 と見なす。金属でない所の F0 が 0.04 から外れるようなら、ディテールの平均で割る
                var f0 = new byte[basePx.Length];
                for (int i = 0; i < f0.Length; i += 4)
                {
                    float metal = glossPx[i] / 255f;
                    for (int c = 0; c < 3; c++)
                        f0[i + c] = (byte)Mathf.Clamp(Mathf.RoundToInt((metal * basePx[i + c] / 255f + (1f - metal) * 0.04f / Mathf.Max(tint[c], 0.04f)) * 255f), 0, 255);
                    f0[i + 3] = basePx[i + 3];
                    glossPx[i] = 255;
                }
                _finished.Enqueue((f0, w, height, f0Name, gen, t => { clone.SetTexture("_BaseMap", t); if (clone.HasProperty("_MainTex")) clone.SetTexture("_MainTex", t); h.Pending--; }));
                _finished.Enqueue((glossPx, w, height, metalName, gen, t => { clone.SetTexture("_MetallicGlossMap", t); h.Pending--; _log.LogInfo($"ハイライトの素材 {clone.name} の絵が出来ました（残り {h.Pending}）"); }));
            });
        }
        ReadBack(gloss, w, height, d => { glossPx = d; Both(); });
        ReadBack(baseTex, w, height, d => { basePx = d; Both(); });
        return h.Bit;
    }

    /// <summary>
    /// 別のスレッドでする仕事。例外をログに書く: Task.Run は例外を黙って捨てるので、別のスレッドから Unity の物（素材や絵の name など）を読む誤りが、
    /// 「絵がいつまでも出来ない」としてしか見えなかった（2 回やった。2026-11-11）
    /// </summary>
    private void Background(Action work) => System.Threading.Tasks.Task.Run(() =>
    {
        try { work(); }
        catch (Exception e) { _log.LogError($"別のスレッドの仕事が失敗しました: {e}"); }
    });

    /// <summary>絵を w × h に縮めて、線形の RGBA32 で読み戻す。</summary>
    private void ReadBack(Texture tex, int w, int h, Action<byte[]> got)
    {
        int gen = _generation;
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
        {
            RenderTexture.ReleaseTemporary(rt);
            if (!req.hasError && gen == _generation) got(req.GetData<byte>().ToArray());
        });
    }

    private void ApplyFloor(MaterialJob job) => job.Params.Roughness = job.NoFloor ? job.BaseRoughness : Mathf.Max(job.BaseRoughness, RoughnessFloor);

    public MaterialLibrary(SceneGeometry scene, ManualLogSource log)
    {
        _scene = scene;
        _log = log;
    }

    // ゲームの映り込みの平均の色（面の平均 × 強さ）: 既定のキューブマップ（RenderSettings.customReflectionTexture）と、反射プローブ
    private Color _cubeDefault, _cubeProbe;
    private bool _cubeReady;
    private readonly List<(MaterialJob job, MaterialDesc desc, Texture2D albedo, Texture2D mask, Color tint, int gen)> _metalPending = new();

    /// <summary>反射プローブの箱の中の金属の、反射の色（15 分のいくつ）。本物の周りも少し映す。</summary>
    public const int ReflectiveMetalTint = 8;

    /// <summary>
    /// 場面の映り込みの絵（既定のキューブマップと反射プローブ）の平均の色を読む。読めたら、待たせてあった金属の素材に発光を足す。
    /// ゲームの金属は「地の色 × 映り込みの絵（ぼかした物）」で塗られ、周りの本当の明るさには関係しない: 反射プローブの箱の中の樽は一様に明るい銀色、
    /// 箱の外のマンホールは暗い（既定のキューブマップは平均 0.03 ほど）。Remix で本物の周りを映すだけだと、夜の街を映して樽が暗く青くなった（2026-11-10）
    /// </summary>
    private void ReadReflections(int gen)
    {
        _cubeReady = false;
        _cubeDefault = _cubeProbe = default;
        _metalPending.Clear();
        var probes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None).Where(x => x.texture != null).Take(4).ToList();
        int pending = 1 + probes.Count;
        Color probeSum = default;
        void Done()
        {
            if (--pending > 0 || gen != _generation) return;
            if (probes.Count > 0) _cubeProbe = probeSum / probes.Count;
            _cubeReady = true;
            _log.LogInfo($"映り込みの絵の平均: 既定 {_cubeDefault}、反射プローブ {_cubeProbe}（{probes.Count} 個の平均 × 強さ）。金属の素材 {_metalPending.Count} 個に足します");
            foreach (var x in _metalPending) MetalGlow(x);
            _metalPending.Clear();
        }
        if (RenderSettings.customReflectionTexture is { } env)
            ReflectionAverage(env, faces => { _cubeDefault = faces.Aggregate((a, b) => a + b) / 6f * RenderSettings.reflectionIntensity; Done(); });
        else Done();
        foreach (var probe in probes)
        {
            float intensity = probe.intensity;
            ReflectionAverage(probe.texture, faces => { probeSum += faces.Aggregate((a, b) => a + b) / 6f * intensity; Done(); });
        }
    }

    /// <summary>金属の所（金属の地図の R）に、地の色 × 映り込みの平均の色の発光を付ける。金属の所がほとんど無い素材と、映り込みが暗い所（既定のキューブマップ）は付けない。</summary>
    private void MetalGlow((MaterialJob job, MaterialDesc desc, Texture2D albedo, Texture2D mask, Color tint, int gen) x)
    {
        // 0.87: URP の surfaceReduction（1 / (粗さ² + 1)）の、なめらかさ 0.4 ほどのときの値
        var glow = (x.job.Reflective ? _cubeProbe : _cubeDefault) * 0.87f;
        float max = glow.maxColorComponent;
        if (max < 0.03f || x.gen != _generation) return;
        void Use(Texture2D t)
        {
            if (t == null) return; // 金属の所がほとんど無かった
            x.job.Emissive = TextureOf(t, x.gen, 0);
            x.job.Params.Emission = new Color(max, max, max, 1f);
            Send(x.job);
        }
        var tint = x.tint;
        var key = (unchecked(x.albedo.GetInstanceID() * 397 ^ x.mask.GetInstanceID()),
            FormattableString.Invariant($"metalglow:{tint.r:0.###},{tint.g:0.###},{tint.b:0.###}:{glow.r:0.###},{glow.g:0.###},{glow.b:0.###}"));
        if (_converted.TryGetValue(key, out var entry))
        {
            if (entry.Waiting.Count == 0) Use(entry.Tex); else entry.Waiting.Add(Use);
            return;
        }
        _converted[key] = entry = new Converted();
        entry.Waiting.Add(Use);
        float f = Mathf.Min(1f, (float)MaxConvertedSize / Mathf.Max(x.mask.width, x.mask.height));
        int w = Mathf.Max(4, Mathf.RoundToInt(x.mask.width * f)), h = Mathf.Max(4, Mathf.RoundToInt(x.mask.height * f));
        string name = x.albedo.name + "_" + x.mask.name + "_" + key.Item2;
        int gen = x.gen;
        byte[] maskPx = null, albedoPx = null;
        void Finish(Texture2D t)
        {
            entry.Tex = t;
            foreach (var use in entry.Waiting) use(t);
            entry.Waiting.Clear();
        }
        void Read(Texture2D tex, Action<byte[]> got)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                if (req.hasError || gen != _generation) return;
                got(req.GetData<byte>().ToArray());
                if (maskPx == null || albedoPx == null) return;
                _converting++;
                Background(() =>
                {
                    int metal = 0;
                    var px = new byte[w * h * 4];
                    for (int i = 0; i < px.Length; i += 4)
                    {
                        float m = maskPx[i] / 255f;
                        if (m > 0.5f) metal++;
                        for (int c = 0; c < 3; c++)
                            px[i + c] = (byte)Mathf.Clamp(Mathf.RoundToInt(Srgb(albedoPx[i + c] / 255f * tint[c] * m * glow[c] / max) * 255f), 0, 255);
                        px[i + 3] = 255;
                    }
                    // 金属の所が 0.5% も無ければ、絵を作らない（px を null で渡す）
                    _finished.Enqueue((metal < w * h / 200 ? null : px, w, h, name, gen, Finish));
                });
            });
        }
        Read(x.mask, d => maskPx = d);
        Read(x.albedo, d => albedoPx = d);
    }

    /// <summary>場面が変わった。作った絵を捨てる（Remix の側の物は ResetJob で消える）。</summary>
    public void Reset(int gen)
    {
        _generation = gen;
        _presetShade = new();
        _presetGlow = new();
        StableId.Clear();
        _converted.Clear();
        _converting = 0;
        ReadReflections(gen);
        _byMaterial.Clear();
        foreach (var h in _highlights) Object.Destroy(h.Clone);
        _highlights.Clear();
        foreach (var t in _retired) if (t != null) Object.Destroy(t);
        _retired.Clear();
        _retired.AddRange(_made);
        _made.Clear();
    }

    /// <summary>
    /// 反射プローブ（ReflectionProbe）の箱に入っている物の素材の variant。ゲームの金属は、反射プローブのある所では焼いた周りの景色を明るく映し
    /// （ビールの樽: 明るい銀色）、無い所では暗い既定のキューブマップしか映さない（Stage01 のマンホール: 黒っぽい）。
    /// Remix では、どちらも暗い金属（MetalTint）にして、映り込みの絵の平均の色を発光で足す（MetalGlow）。箱の中の物は、反射の色を明るめにする（ReflectiveMetalTint）。
    /// 箱の中の物を「地の色で本物の周りを映す金属」にしていた頃は、夜の街を映して樽が暗く青かった。
    /// </summary>
    public const int ReflectiveVariant = 1;

    /// <summary>この場面の素材の hash の数と、全部の排他的論理和（起動のたびに同じになっているかを確かめる）。</summary>
    public string HashCheck
    {
        get
        {
            var all = new HashSet<ulong>(_byMaterial.Values.Select(d => d.Hash));
            return $"{all.Count} 種 {all.Aggregate(0UL, (a, b) => a ^ b):X16}";
        }
    }

    /// <summary>素材の説明。初めての素材なら、Remix に作らせる仕事を積む（メッシュの仕事より先に積まれる）。</summary>
    public MaterialDesc Get(Material m, int variant = 0, ulong variantId = 0)
    {
        if (m != null && _byMaterial.TryGetValue((m, variant), out var known)) return known;
        MaterialDesc desc;
        // variantId: Renderer ごとの variant のときの、起動のたびに同じ値（StableId.Of(Renderer)）。0 なら variant の番号そのまま
        using (Timing.Measure("素材の説明")) desc = Describe(m, variant, variantId != 0 ? variantId : (ulong)variant);
        desc.Job.BaseRoughness = desc.Job.Params.Roughness;
        ApplyFloor(desc.Job);
        if (_presetShade.TryGetValue(desc.Hash, out float shade)) desc.Job.Shade = shade;
        if (m != null && _presetGlow.TryGetValue(desc.Hash, out float glow)) Glow(m, desc, glow);
        if (m != null) _byMaterial[(m, variant)] = desc;
        Send(desc.Job);
        return desc;
    }

    private MaterialDesc Describe(Material m, int variant, ulong variantId)
    {
        int gen = _generation;
        var job = new MaterialJob { Hash = StableId.Handle(0x41, StableId.Of(m), variantId) };
        var desc = new MaterialDesc { Hash = job.Hash, Job = job };
        ref var p = ref job.Params;
        p.Albedo = Color.gray;
        p.Roughness = 0.7f;
        p.AlphaCutoff = -1f;
        p.BlendType = -1;
        p.Opacity = 1f;
        if (m == null) return desc;
        // 不透明なガラス（ビルの窓・店のガラス戸の M_glass_untransparent など）: ゲームでは反射プローブを映している。Remix では本物の周りを映す
        job.NoFloor = m.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;

        string shader = m.shader != null ? m.shader.name : "";
        // _MainTexture はこのゲームのシェーダーグラフ（SimpleHDR・GenericEffect）
        var albedoName = FirstTexture(m, "_BaseMap", "_MainTex", "_BaseColorMap", "_MainTexture");
        if (albedoName != null)
        {
            job.Albedo = TextureOf(m.GetTexture(albedoName), gen, 0);
            desc.UvScale = m.GetTextureScale(albedoName);
            desc.UvOffset = m.GetTextureOffset(albedoName);
        }
        p.Albedo = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
        p.Opacity = p.Albedo.a;

        // ガラス: Remix の透過の素材（反射と屈折のある薄い板）。透ける色は、地の色をアルファの分だけ混ぜた色
        if (IsGlass(m))
        {
            var tint = p.Albedo.linear;
            p.Translucent = true;
            p.Transmittance = Color.Lerp(Color.white, new Color(tint.r, tint.g, tint.b), p.Opacity);
            return desc;
        }

        // ディテール（_DETAIL_MULX2）が地の絵よりずっと細かい素材（道路のアスファルト: 地の絵は 0.06 倍に引き伸ばしてほぼ一様、ディテールはその 32 倍細かい）。
        // URP のディテールの UV は「地の絵の UV（_BaseMap_ST を掛けたあと）× _DetailAlbedoMap_ST」（LitInput.hlsl: InitializeStandardLitSurfaceData に渡る uv は
        // TRANSFORM_TEX 済み）。メッシュの UV から見た繰り返しは 0.06 × 32 = 1.92 倍。32 倍そのままで貼っていた頃は、細かすぎてノイズに潰れ、ひび割れの凹凸が出なかった
        // ゲームのザラザラは、ディテールのノーマル（_DetailNormalMapScale）と紙の質感（_DetailAlbedoMap × 2）で出ている。Remix の素材は UV が 1 つなので、
        // UV をディテールの繰り返しにして、地の絵と金属・なめらかさの絵は平均の値で掛ける（地の絵の大きな模様と、水たまりのつやの模様は無くなる）。
        // ディテールを使わずに地の UV のままだと、ノーマルの強さ 0.05 の地のノーマルしか無く、道路がツルツルに見えた（2026-11-10）
        var detailAlbedo = m.HasProperty("_DetailAlbedoMap") ? m.GetTexture("_DetailAlbedoMap") as Texture2D : null;
        var detailNormal = m.HasProperty("_DetailNormalMap") ? m.GetTexture("_DetailNormalMap") as Texture2D : null;
        bool detailFirst = albedoName != null && IsDetailFirst(m);
        if (detailFirst)
        {
            var baseTex = m.GetTexture(albedoName);
            var detailScale = m.GetTextureScale("_DetailAlbedoMap");
            desc.UvOffset = Vector2.Scale(desc.UvOffset, detailScale) + m.GetTextureOffset("_DetailAlbedoMap");
            desc.UvScale = Vector2.Scale(desc.UvScale, detailScale);
            job.Albedo = detailAlbedo != null ? TextureOf(detailAlbedo, gen, 0) : default;
            var baseColor = p.Albedo.linear;
            float twice = detailAlbedo != null ? 2f : 1f;
            p.Albedo = Gamma(baseColor * (0.75f * twice));
            Average(baseTex, avg =>
            {
                job.Params.Albedo = Gamma(new Color(baseColor.r * avg.r, baseColor.g * avg.g, baseColor.b * avg.b) * twice);
                Send(job);
            });
        }

        // 金属・なめらかさ（URP Lit / Simple Lit）。トゥーンは光沢を持たないので、つや消しのまま
        if (m.HasProperty("_Smoothness") && shader.StartsWith("Universal Render Pipeline/") && detailFirst)
        {
            // ディテールの UV で描くので、金属・なめらかさの絵（地の UV）は平均の値にする
            float smooth = m.GetFloat("_Smoothness");
            p.Metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            p.Roughness = 1f - smooth;
            if (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") is Texture2D gloss)
            {
                // 升目ごとの素材（GetCell）に要る値。升目に切り分けられなかったメッシュは、下の平均の値で描く
                desc.Detail = new DetailGloss
                {
                    Source = m, Variant = variant, VariantId = variantId, Gloss = gloss, Smooth = smooth,
                    FromAlbedoAlpha = m.HasProperty("_SmoothnessTextureChannel") && m.GetFloat("_SmoothnessTextureChannel") > 0.5f,
                    Scale = m.GetTextureScale("_DetailAlbedoMap"), Offset = m.GetTextureOffset("_DetailAlbedoMap"),
                };
                Average(gloss, avg =>
                {
                    job.Params.Metallic = avg.r;
                    job.BaseRoughness = 1f - smooth * avg.a;
                    ApplyFloor(job);
                    Send(job);
                });
            }
        }
        else if (m.HasProperty("_Smoothness") && shader.StartsWith("Universal Render Pipeline/"))
        {
            float smooth = m.GetFloat("_Smoothness");
            p.Metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            var gloss = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") as Texture2D : null;
            bool fromAlbedoAlpha = m.HasProperty("_SmoothnessTextureChannel") && m.GetFloat("_SmoothnessTextureChannel") > 0.5f;
            if (gloss != null)
            {
                // URP: 金属の地図があると、金属は地図の R（_Metallic は使わない）、なめらかさは地図の A × _Smoothness（または地の絵の A: そのときは _Smoothness のまま）。
                // 1 枚の絵（R = 金属、G = なめらかさ）に並べ替え、Remix に金属を R から決めさせる。金属の所の反射の色は、地の色ではなく暗い灰色（MetalTint）にする:
                // ゲームの金属は、暗い反射プローブしか映さないので黒っぽい（マンホールの錆の所）。Remix では、環境光を写した広い光源まで映して青く明るくなり
                // （ゲーム 0.006、Remix 0.19）、わざと汚してある所が消えていた。Remix の光には「拡散だけ」の指定が無いので、金属の反射を弱めて近づける
                p.Metallic = 0f;
                p.MetalTint = MetalTint;
                job.Reflective = variant == ReflectiveVariant;
                // 金属の所に、ゲームの映り込み（反射プローブか既定のキューブマップの平均 × 地の色）を発光で足す（MetalGlow）。もう光っている素材には足さない
                if (!m.IsKeywordEnabled("_EMISSION") && albedoName != null && m.GetTexture(albedoName) is Texture2D baseTex)
                {
                    var pending = (job, desc, baseTex, gloss, p.Albedo.linear, gen);
                    if (_cubeReady) MetalGlow(pending); else _metalPending.Add(pending);
                }
                Convert(gloss, FormattableString.Invariant($"gloss:{(fromAlbedoAlpha ? 1 : 0)}:{smooth:0.####}"), job, desc, (px, i) => { px[i + 1] = fromAlbedoAlpha ? (byte)(255 * smooth) : (byte)(px[i + 3] * smooth); px[i + 2] = 0; px[i + 3] = 255; },
                    (t, j) => j.Roughness = TextureOf(t, gen, RemixApi.EncodingSmoothnessInGreen));
                p.Roughness = 0f; // 地図があるときは下限
            }
            else p.Roughness = 1f - smooth;
        }

        // ノーマルマップ（ディテールの UV で描く物は、ディテールのノーマル）
        var bump = detailFirst ? detailNormal : m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") as Texture2D : null;
        if (bump != null)
        {
            string scaleName = detailFirst ? "_DetailNormalMapScale" : "_BumpScale";
            float scale = m.HasProperty(scaleName) ? m.GetFloat(scaleName) : 1f;
            Convert(bump, FormattableString.Invariant($"normal:{scale:0.####}"), job, desc, (px, i) =>
            {
                // Unity の UnpackNormal と同じ: x = r * a（DXT5nm でも BC5 でも合う）、y = g。_BumpScale は 0.5 を中心に伸ばす
                float x = px[i] / 255f * (px[i + 3] / 255f), y = px[i + 1] / 255f;
                px[i] = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + (x - 0.5f) * scale) * 255f), 0, 255);
                px[i + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + (y - 0.5f) * scale) * 255f), 0, 255);
                px[i + 2] = 0; px[i + 3] = 255;
            }, (t, j) => j.Normal = TextureOf(t, gen, RemixApi.EncodingNormalTangentXY));
        }

        // 発光（URP の _EMISSION）
        if (m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor"))
        {
            // HDR の色（_EmissionColor）は、素材に入っている値がそのままシェーダーに渡る（線形に直さない）。.linear にすると、
            // 車のヘッドライト（3.95）が 20.6 になり、ゲームの HDR の絵の 5.2 倍に光った。看板（2.0 → 4.6）は 2.3 倍（2026-11-10 に測った）
            p.Emission = m.GetColor("_EmissionColor");
            if (m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") is Texture2D em) EmissiveTexture(em, p.Emission, job, desc, gen);
        }
        // 発光（トゥーン: Universal Toon Shader の _Emissive_Color × _Emissive_Tex）。既定は黒
        else if (m.HasProperty("_Emissive_Color") && m.GetColor("_Emissive_Color") is var ec && Mathf.Max(ec.r, Mathf.Max(ec.g, ec.b)) > 0.001f)
        {
            p.Emission = ec;
            if (m.HasProperty("_Emissive_Tex") && m.GetTexture("_Emissive_Tex") is Texture2D et) EmissiveTexture(et, ec, job, desc, gen);
        }
        // トゥーン（キャラクター）: 地の絵そのものを発光にして、地の色は暗くする。
        // キャラクターは Remix では影と映り込みにだけ出る。ゲームのトゥーンは周りの明るさに関係なく平らに明るいが、地の色に本物の光を当てるだけだと、
        // 夜の街では灰色に沈み、ガラスに映った姿がゲームのキャラクターと別物になった（2026-11-11）
        else if (shader.Contains("Toon") && albedoName != null && job.Albedo.Ptr != IntPtr.Zero)
        {
            job.Emissive = job.Albedo;
            p.Emission = Color.white * ToonGlow;
            p.Albedo = Gamma(p.Albedo.linear * ToonAlbedo);
        }

        // 切り抜き。半透明（描く順番 2501 から）は Remix に渡さず、ゲーム自身が描く（IsTransparent）
        if (IsCutout(m))
            p.AlphaCutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
        // キャラクターの半透明の部品（前髪の透け・網タイツ）: 切り抜きの不透明にする。アルファで混ぜる面（BlendType 0）にしていた頃は、
        // Remix が混ぜる面を別の近似で照らし、発光にもアルファを掛けない（opaque_surface_material_blending.slangh の kAlpha）ので、
        // 映り込みの中で前髪だけ白っぽく明るく浮いた（2026-11-11）。前髪の透けの絵のアルファはほとんど 0.86〜1（ゲームもそのアルファで混ぜる）
        if (IsBlendedToon(m)) p.AlphaCutoff = 0.5f;
        return desc;
    }

    /// <summary>
    /// この素材（その Renderer の分）の地の色に、shade（線形）を掛ける。木の葉は、焼き込みではとても暗い（ライトマップの値 0.02〜0.04。下から照らされた木だけ明るい）。
    /// Remix では板の 1 枚 1 枚に空や照明が当たって何倍も明るくなるので、焼いた明るさと Remix で届く予想の比を掛けて合わせる（BakedLamps.Shades）。
    /// 素材ごとに 1 つの倍率にしていた頃は、下から照らされて明るい木（Stage01 の広場）まで暗くなった。
    /// </summary>
    public void SetShade(Material m, int variant, float shade)
    {
        if (m == null || !_byMaterial.TryGetValue((m, variant), out var desc)) return;
        desc.Job.Shade = shade;
        Send(desc.Job);
    }

    /// <summary>
    /// 木の葉の板の素材か: 切り抜きで、裏も描く（_Cull が 2 でない。このゲームの木の葉は 1 = 裏だけ描く）。
    /// Remix の素材とメッシュを木 1 本ごとに分け、木ごとに地の色の倍率を決める（SetShade）。
    /// </summary>
    public static bool IsLeaf(Material m) => IsCutout(m) && m.HasProperty("_Cull") && m.GetFloat("_Cull") != 2f;

    /// <summary>切り抜き（アルファテスト）で描く素材か。</summary>
    public static bool IsCutout(Material m) => m != null && (m.IsKeywordEnabled("_ALPHATEST_ON") || m.renderQueue >= 2450);

    /// <summary>
    /// 映り込みのキューブマップ（RenderSettings.customReflectionTexture）の、面ごとの平均の色（4 × 4 のミップを読み戻す。HDR の形式のときだけ正しい）。
    /// </summary>
    public static void ReflectionAverage(Texture env, System.Action<Color[]> done)
    {
        // 圧縮（BC6H）のキューブマップは、面をそのまま読み戻せなかった（全部 0 が返った）。面を同じ形式の 2D の絵に写し、半精度の描き先へ描いてから読む
        int size = env.width, pending = 6;
        var faces = new Color[6];
        for (int face = 0; face < 6; face++)
        {
            int f = face;
            var flat = new Texture2D(size, size, env.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None);
            Graphics.CopyTexture(env, f, 0, flat, 0, 0);
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(flat, rt);
            UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAHalf, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(flat);
                if (!req.hasError)
                {
                    var px = req.GetData<ushort>();
                    Color sum = default;
                    for (int i = 0; i + 3 < px.Length; i += 4)
                        sum += new Color(Mathf.HalfToFloat(px[i]), Mathf.HalfToFloat(px[i + 1]), Mathf.HalfToFloat(px[i + 2]));
                    faces[f] = sum / Mathf.Max(px.Length / 4, 1);
                }
                if (--pending == 0) done(faces);
            });
        }
    }

    /// <summary>環境として Remix が描く素材か（URP の Lit・Simple Lit・Unlit と Arnold のシェーダーグラフ）。それ以外（トゥーン・ほかのシェーダーグラフ）はゲーム自身が描き直す。</summary>
    public static bool IsEnvironment(Material m) =>
        m != null && m.shader != null && (m.shader.name.StartsWith("Universal Render Pipeline/") || m.shader.name.StartsWith("Shader Graphs/Arnold"));

    /// <summary>
    /// キャラクターの半透明の部品（Universal Toon Shader で、描く順番が半透明、絵を持つ物）。Remix には切り抜きの不透明として渡す（Describe）。
    /// 頬の赤み（m_face_blush）は外す: トゥーンの地の色を暗くして渡しているので、赤みが目の上の茶色い帯になった。
    /// </summary>
    public static bool IsBlendedToon(Material m) => IsTransparent(m) && m.shader != null && m.shader.name == "Toon" && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null
                                                    && m.name.IndexOf("blush", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>半透明（描く順番 2501 から、URP の RenderQueueRange.transparent）。ゲーム自身が描くので、Remix には渡さない（ガラスは除く: <see cref="IsGlass"/>）。</summary>
    public static bool IsTransparent(Material m) => m != null && m.renderQueue > 2500;

    /// <summary>
    /// Remix にガラスとして描かせる半透明（環境の素材で、名前に glass を含むか、普通の混ぜ方でなめらか（0.9 から）な物: コンビニの窓・冷蔵庫の扉など）。
    /// ゲームのガラスは暗い反射プローブしか映さないが、かずくんの希望は「ガラスはちゃんと反射して欲しい」（2026-11-10）。ゲーム側の同じ物は
    /// 見えない素材に差し替える（<see cref="SceneGeometry.HideGlass"/>）
    /// </summary>
    public static bool IsGlass(Material m) => IsTransparent(m) && IsEnvironment(m) &&
        (m.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
         (m.HasProperty("_Blend") && m.GetFloat("_Blend") == 0f && m.HasProperty("_Smoothness") && m.GetFloat("_Smoothness") >= 0.9f));

    /// <summary>
    /// 発光の絵。Remix は絵があると発光の色（emissiveColorConstant）を使わず、絵 × 強さ（色のいちばん大きい成分）にする
    /// （opaque_surface_material_interaction.slangh）。色に偏りがあれば（信号の赤 1024, 10.7, 0.2 など）、色を絵に焼き込んだ絵を作る。
    /// 焼き込まないと、赤い信号や黄色いヘッドライトが白く光った（2026-11-10）
    /// </summary>
    private void EmissiveTexture(Texture2D tex, Color emission, MaterialJob job, MaterialDesc desc, int gen)
    {
        job.Emissive = TextureOf(tex, gen, 0);
        float max = emission.maxColorComponent;
        if (max <= 0f || Mathf.Min(emission.r, Mathf.Min(emission.g, emission.b)) >= 0.99f * max) return;
        var unit = new Color(emission.r / max, emission.g / max, emission.b / max);
        // 読み戻しは線形の値。作る絵は UNORM で、Remix がガンマを戻して読むので、sRGB に直して書く
        Convert(tex, FormattableString.Invariant($"emissive:{unit.r:0.###},{unit.g:0.###},{unit.b:0.###}"), job, desc, (px, i) =>
        {
            for (int c = 0; c < 3; c++)
                px[i + c] = (byte)Mathf.Clamp(Mathf.RoundToInt(Srgb(px[i + c] / 255f * unit[c]) * 255f), 0, 255);
            px[i + 3] = 255;
        }, (t, j) => j.Emissive = TextureOf(t, gen, 0));
    }

    /// <summary>
    /// この素材（その Renderer の分）を、地の絵 × glow で光らせる（看板の焼き込みの明かりの代わり: SceneGeometry.SolveLamps）。
    /// ライトマップの色は付けない: 色を付けると、看板 1 枚ごとに色を焼き込んだ絵を作ることになる（EmissiveTexture）
    /// </summary>
    public void SetGlow(Material m, int variant, float glow)
    {
        if (m == null || !_byMaterial.TryGetValue((m, variant), out var desc)) return;
        Glow(m, desc, glow);
        Send(desc.Job);
    }

    private void Glow(Material m, MaterialDesc desc, float glow)
    {
        var job = desc.Job;
        // もう自分で光っている看板（_EMISSION）には足さない: 足すと白く飛んで字が読めなくなった（クリニックの看板）
        if (job.Params.Emission.maxColorComponent > 0f) return;
        // ゲームの色は 絵 × _BaseColor × ライトマップ。_BaseColor を掛け忘れると、暗い色を掛けてある看板（広場の大きな看板）が 3 倍ほど明るく光った。
        // _BaseColor に色が付いている看板だけ、色を焼き込んだ絵を作る（EmissiveTexture）
        var tint = job.Params.Albedo.linear;
        job.Params.Emission = new Color(tint.r * glow, tint.g * glow, tint.b * glow, 1f);
        var name = FirstTexture(m, "_BaseMap", "_MainTex", "_BaseColorMap", "_MainTexture");
        if (name != null && m.GetTexture(name) is Texture2D tex) EmissiveTexture(tex, job.Params.Emission, job, desc, _generation);
    }

    // 保存してあった結果（素材の hash → 地の色の倍率・発光の強さ）。その素材を初めて説明するときに付ける
    private Dictionary<ulong, float> _presetShade = new(), _presetGlow = new();

    /// <summary>保存してあった、木の葉の暗さと看板の発光（素材の hash で指す）。このあと説明する素材に付く。</summary>
    public void Preset(Dictionary<ulong, float> shades, Dictionary<ulong, float> glows)
    {
        _presetShade = shades;
        _presetGlow = glows;
    }

    /// <summary>その素材（variant）の Remix の hash。無ければ 0。</summary>
    public ulong HashOf(Material m, int variant) => m != null && _byMaterial.TryGetValue((m, variant), out var desc) ? desc.Hash : 0;

    private static Color Gamma(Color linear) => new Color(linear.r, linear.g, linear.b, 1f).gamma;

    /// <summary>絵の平均の色（線形）。GPU で 64×64 に写して読み戻す。場面が変わっていたら呼ばない。</summary>
    private void Average(Texture tex, Action<Color> done)
    {
        int gen = _generation;
        var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAHalf, req =>
        {
            RenderTexture.ReleaseTemporary(rt);
            if (req.hasError || gen != _generation) return;
            var d = req.GetData<ushort>();
            Color sum = default;
            for (int i = 0; i < d.Length; i += 4)
                sum += new Color(Mathf.HalfToFloat(d[i]), Mathf.HalfToFloat(d[i + 1]), Mathf.HalfToFloat(d[i + 2]), Mathf.HalfToFloat(d[i + 3]));
            done(sum / (d.Length / 4));
        });
    }

    private static string FirstTexture(Material m, params string[] names)
    {
        foreach (var n in names) if (m.HasProperty(n) && m.GetTexture(n) != null) return n;
        return null;
    }

    private sealed class Converted
    {
        public Texture2D Tex;
        public List<Action<Texture2D>> Waiting = new();
    }

    // 並べ替えた絵。元の絵と並べ替え方（kind）が同じなら、素材が違っても 1 枚を使い回す
    private readonly Dictionary<(int src, string kind), Converted> _converted = new();
    // 別のスレッドで画素を書き終えた絵（メインスレッドが Pump で Texture2D にする）
    private readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] px, int w, int h, string name, int gen, Action<Texture2D> apply)> _finished = new();

    /// <summary>
    /// 絵を GPU で縮めて読み戻し、px（RGBA32）を画素ごとに書き換えて作り直す。出来上がったら done を呼び、素材を送り直す。
    /// kind は並べ替え方の名前（値も含める）: 同じ絵・同じ kind は 1 回しか作らない。pixel は別のスレッドで呼ぶので、Unity の物に触らないこと。
    /// 前は素材ごとに毎回、メインスレッドで画素を回していて、ステージを読み込むたびに 10 秒あまり固まった（Stage09: 1030 回、11.7 秒。2026-11-11）
    /// </summary>
    private void Convert(Texture2D src, string kind, MaterialJob job, MaterialDesc desc, Action<byte[], int> pixel, Action<Texture2D, MaterialJob> done, Rect? region = null)
    {
        int gen = _generation;
        var key = (src.GetInstanceID(), kind);
        if (_converted.TryGetValue(key, out var entry))
        {
            // もう出来ている: 素材はこのあと呼ぶ側が送る。まだなら、出来たときに送り直す
            if (entry.Tex != null) done(entry.Tex, job);
            else entry.Waiting.Add(t => { done(t, job); Send(job); });
            return;
        }
        _converted[key] = entry = new Converted();
        entry.Waiting.Add(t => { done(t, job); Send(job); });
        float f = Mathf.Min(1f, (float)MaxConvertedSize / Mathf.Max(src.width, src.height));
        int w = Mathf.Max(4, Mathf.RoundToInt(src.width * f)), h = Mathf.Max(4, Mathf.RoundToInt(src.height * f));
        // region（UV の四角）があれば、そこだけを切り出す（画素の数もその割合に。8 画素から）
        if (region is { } part)
        {
            w = Mathf.Clamp(Mathf.CeilToInt(w * Mathf.Abs(part.width)), 8, w);
            h = Mathf.Clamp(Mathf.CeilToInt(h * Mathf.Abs(part.height)), 8, h);
        }
        string name = src.name + "_remix_" + kind; // 名前に並べ替え方を入れる（テクスチャの hash は名前から作る: StableId）
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        if (region is { } cut) Graphics.Blit(src, rt, cut.size, cut.position); else Graphics.Blit(src, rt);
        _converting++;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
        {
            RenderTexture.ReleaseTemporary(rt);
            if (gen != _generation) return;
            if (req.hasError) { _converting--; return; }
            var px = req.GetData<byte>().ToArray();
            Background(() =>
            {
                for (int i = 0; i < px.Length; i += 4) pixel(px, i);
                _finished.Enqueue((px, w, h, name, gen, t =>
                {
                    entry.Tex = t;
                    foreach (var use in entry.Waiting) use(t);
                    entry.Waiting.Clear();
                }));
            });
        });
    }

    /// <summary>毎フレーム（メインスレッド）。画素を書き終えた絵を Texture2D にして、待っている素材に渡す。1 フレームに budgetMs まで。</summary>
    public void Pump(double budgetMs)
    {
        if (_finished.IsEmpty) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < budgetMs && _finished.TryDequeue(out var f))
        {
            if (f.gen != _generation) continue;
            _converting--;
            if (f.px == null) { f.apply(null); continue; }
            Texture2D tex;
            using (Timing.Measure("絵の並べ替え: 絵を作る"))
            {
                tex = new Texture2D(f.w, f.h, TextureFormat.RGBA32, true, linear: true) { name = f.name };
                tex.SetPixelData(f.px, 0);
                tex.Apply(true, makeNoLongerReadable: true);
            }
            _made.Add(tex);
            // 同じ hash で送り直すと、Remix はその素材を新しい説明で作り直す（メッシュはそのまま）
            f.apply(tex);
            if (_converting == 0) _log.LogInfo($"素材の絵の並べ替えが終わりました（{_made.Count} 枚）");
        }
    }

    // 線形 → sRGB（Mathf.LinearToGammaSpace は Unity の中の関数なので、別のスレッドからは呼ばない）
    private static float Srgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;

    internal static SceneGeometry.TextureDesc TextureOf(Texture tex, int gen, uint encoding)
    {
        if (tex is not Texture2D t2d) return default;
        uint format = DxgiFormat(t2d.graphicsFormat);
        if (format == 0) return default;
        // 同じ絵でも読み方（encoding）が違えば、Remix では別の絵にする
        return new SceneGeometry.TextureDesc { Ptr = t2d.GetNativeTexturePtr(), DxgiFormat = format, Encoding = encoding, Hash = StableId.Handle(0x54, StableId.Of(t2d), encoding) };
    }

    /// <summary>Unity のテクスチャの形式を、D3D11 のビューの形式（DXGI_FORMAT）に。知らない形式は 0。</summary>
    private static uint DxgiFormat(GraphicsFormat f) => f switch
    {
        GraphicsFormat.R8G8B8A8_UNorm => 28,
        GraphicsFormat.R8G8B8A8_SRGB => 29,
        GraphicsFormat.B8G8R8A8_UNorm => 87,
        GraphicsFormat.B8G8R8A8_SRGB => 91,
        GraphicsFormat.RGBA_DXT1_UNorm => 71,
        GraphicsFormat.RGBA_DXT1_SRGB => 72,
        GraphicsFormat.RGBA_DXT3_UNorm => 74,
        GraphicsFormat.RGBA_DXT3_SRGB => 75,
        GraphicsFormat.RGBA_DXT5_UNorm => 77,
        GraphicsFormat.RGBA_DXT5_SRGB => 78,
        GraphicsFormat.R_BC4_UNorm => 80,
        GraphicsFormat.RG_BC5_UNorm => 83,
        GraphicsFormat.RGB_BC6H_UFloat => 95,
        GraphicsFormat.RGBA_BC7_UNorm => 98,
        GraphicsFormat.RGBA_BC7_SRGB => 99,
        _ => 0,
    };
}
