using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// URP のカメラの中、不透明を描いた直後（空とデカールの前）に差し込むパス。環境は Remix、キャラクターと半透明はゲーム自身が描く。
/// 1. 霧のあるフレームでは、霧の濃さ（1 - f）を別の絵に描く（<see cref="FogMaterials"/>）
/// 2. 描画スレッドで Remix に描かせ、ゲームと同じ尺度の HDR の絵に戻して霧を混ぜる（プラグインの合図、<see cref="Remix.RemixResolve"/>）
/// 3. その HDR の絵をカメラの色に写す（Blitter はカメラの行列を変えない）
/// 4. ゲーム自身に、印を付けた不透明な物（キャラクター・アイテムなど、トゥーンやシェーダーグラフの物: <see cref="OverlayBit"/>）を、もう一度描かせる。
///    深さは、環境だけの深さ（EnvironmentBit の物を各素材の DepthOnly で描く）から始める。カメラの深さにはキャラクター自身がもう入っていて、
///    前髪より奥の目が弾かれ、前髪の透けが目の上に黒い帯になった（2026-11-10）
/// このあと、ゲーム自身の空・デカール（濡れた路面の汚れ: Screen Space の DecalProjector）・半透明（頬の赤み・パーティクル・ガラス）・
/// ポストプロセス・UI が、いつもどおりカメラの深さで上に乗る。ポストプロセスの前に写していた頃は、デカールが Remix の絵の下に消えていた（2026-11-10）
/// shortcut: RenderGraph の道だけ。URP の互換モード（RenderGraph を切った設定）なら何も写さない。そうしたゲームが出たら Execute も書く
/// </summary>
internal sealed class RemixCompositePass : ScriptableRenderPass
{
    /// <summary>ゲーム自身に描かせる物の印（Renderer.renderingLayerMask のいちばん上の桁）。Remix にも影のために渡すが、画面には映さない。</summary>
    public const uint OverlayBit = 1u << 31;
    /// <summary>
    /// Remix が環境として描く MeshRenderer の印（SceneGeometry.Collect が付ける）。ゲームに描き直させるときの深さは、この印の物だけで作る。
    /// 前は「キャラクターのいる層（GameObject.layer）を除く」で作っていたが、キャラクターの顔の部品（m_face）が Default の層にいて、
    /// Default の車や小物がまとめて深さから抜け、奥のアイテムが車を透けて見えた（Stage06 のトラック、2026-11-11）
    /// </summary>
    public const uint EnvironmentBit = 1u << 26;

    private sealed class PassData
    {
        public IntPtr Callback;
        public int Slot;
        public bool Composite, Fog;
        public RTHandle Source;
        public TextureHandle Target, Depth;
        public RenderTexture FogFactor, FogDepth, OverlayDepth, Dump;
        public RendererListHandle FogScene, EnvironmentDepth, OverlayOpaque;
        public readonly List<RendererListHandle> Highlights = new();
    }

    private static readonly int FogColorId = Shader.PropertyToID("unity_FogColor");
    private static readonly List<ShaderTagId> SceneTags = new() { new("UniversalForward"), new("UniversalForwardOnly"), new("SRPDefaultUnlit") };
    private static readonly List<ShaderTagId> DepthTags = new() { new("DepthOnly") };

    private readonly IntPtr _callback;
    private readonly FogMaterials _fog;
    private RTHandle _source;
    private RenderTexture _sourceTexture;
    private int _slot;
    private bool _composite;
    private Camera _fogCamera;
    private RenderTexture _fogFactor, _fogDepth, _overlayDepth;
    private IReadOnlyList<MaterialLibrary.SunHighlight> _highlights;

    /// <summary>開発用（mod.passtest）: 1 = パスは積むが合図を出さない、2 = パスを積まない、5 = Remix に描かせない、8 = 描かせる前の Flush をしない（どれが絵を乱すかの切り分け）。</summary>
    public static int Test;

    /// <summary>開発用（mod.overlay = 0）: キャラクターなどの描き直しをやめる（mod.showchara = 1 と合わせて、Remix が持っているキャラクターの姿を見る）。</summary>
    public static bool DrawOverlay = true;

    /// <summary>開発用（mod.hdrdump）: 次に写すフレームで、ゲーム自身の絵（不透明まで、空と半透明の前の HDR）をここに写す。1 回で空に戻る。</summary>
    public RenderTexture Dump;

    public RemixCompositePass(IntPtr renderEventCallback, FogMaterials fog)
    {
        _callback = renderEventCallback;
        _fog = fog;
        renderPassEvent = RenderPassEvent.BeforeRenderingSkybox;
    }

    /// <summary>
    /// このフレームで写す絵（Remix の絵を戻した HDR の絵）と、描画スレッドに渡すフレームの番号。カメラに積む直前に呼ぶ。
    /// composite が false のときは Remix に描かせて締めるだけで、画面には写さない（Remix に形が無い場面では、Remix は何も描かないので）。
    /// fogCamera が null でなければ、そのカメラの不透明な物で霧の濃さを fogFactor（深さは fogDepth）に描く。
    /// </summary>
    public void Setup(RenderTexture source, int slot, bool composite, Camera fogCamera, RenderTexture fogFactor, RenderTexture fogDepth,
        RenderTexture overlayDepth, IReadOnlyList<MaterialLibrary.SunHighlight> highlights)
    {
        _highlights = highlights;
        _overlayDepth = overlayDepth;
        if (source != _sourceTexture)
        {
            _source?.Release();
            _source = RTHandles.Alloc(source);
            _sourceTexture = source;
        }
        _slot = slot;
        _composite = composite;
        _fogCamera = fogCamera;
        _fogFactor = fogFactor;
        _fogDepth = fogDepth;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var camera = frameData.Get<UniversalCameraData>();
        // カメラが重なっているときは、画面に出す最後の 1 台でだけ写す（途中のカメラで写すと、次のカメラが上に描く）
        if (!camera.resolveFinalTarget) return;
        var resources = frameData.Get<UniversalResourceData>();
        var rendering = frameData.Get<UniversalRenderingData>();
        var lights = frameData.Get<UniversalLightData>();
        using var builder = renderGraph.AddUnsafePass<PassData>("RTX Remix", out var data);
        data.Callback = _callback;
        data.Slot = _slot;
        data.Composite = _composite && _source != null;
        data.Source = _source;
        data.Target = resources.activeColorTexture;
        data.Depth = resources.activeDepthTexture;
        builder.UseTexture(data.Target, AccessFlags.ReadWrite);
        builder.UseTexture(data.Depth, AccessFlags.ReadWrite);
        builder.AllowPassCulling(false);
        builder.AllowGlobalStateModification(true);
        data.Dump = Dump;
        Dump = null;
        data.OverlayDepth = _overlayDepth;
        data.Highlights.Clear();

        if (data.Composite)
        {
            // 環境（EnvironmentBit の物）の深さ（切り抜きも各素材の DepthOnly で正しく抜ける）
            var depth = RenderingUtils.CreateDrawingSettings(DepthTags, rendering, camera, lights, SortingCriteria.CommonOpaque);
            data.EnvironmentDepth = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, depth,
                new FilteringSettings(RenderQueueRange.opaque, -1, EnvironmentBit)));
            builder.UseRendererList(data.EnvironmentDepth);
            var opaque = RenderingUtils.CreateDrawingSettings(SceneTags, rendering, camera, lights, SortingCriteria.CommonOpaque);
            data.OverlayOpaque = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, opaque,
                new FilteringSettings(RenderQueueRange.opaque, -1, OverlayBit)));
            builder.UseRendererList(data.OverlayOpaque);
            // 道路の、平行光源のハイライト（MaterialLibrary.SunHighlight）: 印の付いた Renderer を、写しの素材で描く
            if (MaterialLibrary.SunHighlights && _highlights != null)
                foreach (var h in _highlights)
                {
                    if (!h.Ready) continue;
                    var settings = RenderingUtils.CreateDrawingSettings(SceneTags, rendering, camera, lights, SortingCriteria.CommonOpaque);
                    settings.overrideMaterial = h.Clone;
                    settings.overrideMaterialPassIndex = h.Pass;
                    var list = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, settings, new FilteringSettings(RenderQueueRange.opaque, -1, h.Bit)));
                    builder.UseRendererList(list);
                    data.Highlights.Add(list);
                }
        }

        data.Fog = data.Composite && _fogCamera != null && camera.camera == _fogCamera && _fog.Ready;
        if (data.Fog)
        {
            var drawing = RenderingUtils.CreateDrawingSettings(SceneTags, rendering, camera, lights, SortingCriteria.CommonOpaque);
            drawing.overrideMaterial = _fog.Scene;
            drawing.overrideMaterialPassIndex = 0;
            // 切り抜き（2450 から）は外す: 素材を差し替えると穴が無くなり、葉の四角に霧が掛かる
            data.FogScene = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, new FilteringSettings(new RenderQueueRange(0, 2449))));
            builder.UseRendererList(data.FogScene);
            data.FogFactor = _fogFactor;
            data.FogDepth = _fogDepth;
        }

        builder.SetRenderFunc((PassData d, UnsafeGraphContext context) =>
        {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            if (d.Dump != null)
            {
                // cmd.Blit はカメラの行列を変える（このあとの描き直しが崩れる）ので Blitter で写す
                cmd.SetRenderTarget(new RenderTargetIdentifier(d.Dump));
                Blitter.BlitTexture(cmd, (RTHandle)d.Target, new Vector4(1f, 1f, 0f, 0f), 0f, false);
            }
            if (d.Fog)
            {
                // 黒い素材で、霧の色を白にして描くと、出てくる色が 1 - f（Unity の霧の式そのもの）
                cmd.SetRenderTarget(new RenderTargetIdentifier(d.FogFactor), new RenderTargetIdentifier(d.FogDepth));
                cmd.ClearRenderTarget(true, true, Color.clear);
                var fogColor = RenderSettings.fogColor.linear;
                cmd.SetGlobalColor(FogColorId, Color.white);
                cmd.DrawRendererList(d.FogScene);
                cmd.SetGlobalColor(FogColorId, fogColor);
            }
            if (d.Composite)
            {
                cmd.SetRenderTarget(new RenderTargetIdentifier(d.OverlayDepth));
                cmd.ClearRenderTarget(true, false, Color.clear);
                cmd.DrawRendererList(d.EnvironmentDepth);
            }
            // 合図の中で、Mod 自身が D3D11 を直接呼んで描く（RemixResolve: 描き先・シェーダー・深さの状態を変える）。Unity が覚えている状態と
            // 実際がずれるので、状態を取り直させる（頬の赤みが消える件の原因ではなかったが、ずれたままにしない）
            if (Test != 1) cmd.IssuePluginEventAndDataWithFlags(d.Callback, d.Slot, CustomMarkerCallbackFlags.CustomMarkerCallbackForceInvalidateStateTracking, IntPtr.Zero);
            if (!d.Composite)
            {
                cmd.SetRenderTarget(d.Target, d.Depth);
                return;
            }
            // Remix は上から下へ書き、Unity は D3D11 のレンダーテクスチャを上下逆に持つので、写すときに上下を返す
            cmd.SetRenderTarget(d.Target);
            Blitter.BlitTexture(cmd, d.Source, new Vector4(1f, -1f, 0f, 1f), 0f, false);
            if (d.Highlights.Count > 0)
            {
                // カメラの深さ（キャラクターも入っている）で、同じ深さの所にだけ足す。霧の色は足さない（霧の濃さの分だけ弱まる: 色 × f + 霧の色 × (1 - f) の霧の色を黒に）
                cmd.SetRenderTarget(d.Target, d.Depth);
                cmd.SetGlobalColor(FogColorId, Color.black);
                foreach (var list in d.Highlights) cmd.DrawRendererList(list);
                cmd.SetGlobalColor(FogColorId, RenderSettings.fogColor.linear);
            }
            cmd.SetRenderTarget(d.Target, new RenderTargetIdentifier(d.OverlayDepth));
            if (DrawOverlay) cmd.DrawRendererList(d.OverlayOpaque);
        });
    }
}

/// <summary>
/// ゲームの霧の濃さを測る素材。Unity の霧は「色 × f + 霧の色 × (1 - f)」（f は Unity が奥行きから決める）。
/// Remix の API には霧を渡す口が無く、Remix の霧（体積）は光を散らす物なので、ゲームの霧の色にならない。
/// そこで、ゲームの URP のシェーダー（Unlit、無ければ Simple Lit）に黒い素材で場面を描かせ、霧の色を白にして 1 - f を出させる。
/// シェーダーは場面の素材が読み込まれてから見つかる（Shader.Find は読み込まれていない物を返さない）ので、場面に入るたびに探す。
/// </summary>
internal sealed class FogMaterials
{
    public Material Scene;
    /// <summary>何も描かない素材（混ぜ方 Zero One、深さも書かない）。Remix が描くガラスの、ゲーム側の素材と差し替える。</summary>
    public Material Invisible;
    public bool Ready => Scene != null;
    private readonly BepInEx.Logging.ManualLogSource _log;
    private bool _warned;

    public FogMaterials(BepInEx.Logging.ManualLogSource log) => _log = log;

    public void TryCreate()
    {
        if (Ready) return;
        Shader unlit = null, simple = null;
        foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
        {
            if (s.name == "Universal Render Pipeline/Unlit") unlit = s;
            else if (s.name == "Universal Render Pipeline/Simple Lit") simple = s;
        }
        var shader = unlit != null ? unlit : simple;
        if (shader == null)
        {
            if (!_warned) _log.LogInfo("霧に使う URP のシェーダー（Unlit・Simple Lit）がまだ読み込まれていません。霧は掛けません");
            _warned = true;
            return;
        }
        // 黒い不透明（Unlit は色 = _BaseColor、Simple Lit は黒の地なので光も 0、ハイライトと発光は既定で無し）。両面
        Scene = new Material(shader) { name = "RemixFogFactor", hideFlags = HideFlags.HideAndDontSave };
        Scene.SetColor("_BaseColor", Color.black);
        Scene.SetFloat("_SrcBlend", (float)BlendMode.One);
        Scene.SetFloat("_DstBlend", (float)BlendMode.Zero);
        Scene.SetFloat("_ZWrite", 1f);
        Scene.SetFloat("_Cull", 0f);
        Invisible = new Material(shader) { name = "RemixInvisible", hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
        Invisible.SetFloat("_Surface", 1f);
        Invisible.SetFloat("_SrcBlend", (float)BlendMode.Zero);
        Invisible.SetFloat("_DstBlend", (float)BlendMode.One);
        Invisible.SetFloat("_ZWrite", 0f);
        _log.LogInfo($"霧の濃さは {shader.name} で測ります"); 
    }
}
