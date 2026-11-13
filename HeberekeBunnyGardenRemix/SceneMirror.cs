using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HeberekeBunnyGardenRemix.Remix;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// Unity の場面を Remix に映す。今はカメラと、静的なメッシュ（<see cref="SceneGeometry"/>）と、仮の太陽 1 つ。
/// メインスレッドは URP がカメラを描き始めるところでカメラを写し取り、<see cref="RemixCompositePass"/> をカメラに積む。
/// Remix の呼び出しはすべて描画スレッドの <see cref="OnRenderEvent"/> で行い、パスが描かせた絵をカメラの色に写す。
/// </summary>
internal sealed class SceneMirror : MonoBehaviour
{
    private struct Frame
    {
        public CameraPose Camera;
        public IntPtr Target, Hdr; // Remix の描き先（8 ビット）と、ゲームの尺度に戻した HDR の絵
        // 霧: Unity が霧の濃さ（1 - f）を描いた絵と、混ぜる霧の色（線形、ゲームの尺度）。FogFactor が 0 なら霧なし
        public IntPtr FogFactor;
        public Color FogColor;
        public bool Shown; // Remix の絵を画面に写すフレーム（重ねカメラの場面では写さない）
    }

    // 描画スレッドは高々 1 フレーム遅れなので、4 つ回せば書きかけを読まない
    private const int FrameRing = 4;

    private delegate void RenderEvent(int eventId, IntPtr data);
    // 描画スレッドから呼ばれる関数。GC に回収されないよう static に持つ
    private static readonly RenderEvent s_onRenderEvent = OnRenderEvent;
    private static readonly IntPtr s_onRenderEventPtr = Marshal.GetFunctionPointerForDelegate(s_onRenderEvent);
    private static SceneMirror s_current;

    private RemixApi _api;
    private ManualLogSource _log;
    private RenderTexture _target;
    private readonly Frame[] _frames = new Frame[FrameRing];
    private SceneGeometry _geometry;
    private SkinnedGeometry _skinned;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string key, string value)> _configs = new();
    private volatile bool _failed;

    // ここから下は描画スレッドだけが触る
    private bool _registered;
    private readonly Dictionary<ulong, RemixMaterial> _materials = new();
    private readonly Dictionary<ulong, RemixTexture> _textures = new();
    // 場面の光源 → Remix の光源と、作ったときの倍率
    private readonly Dictionary<SceneGeometry.LightJob, (RemixLight light, float scale)> _lights = new();
    private readonly List<SceneGeometry.LightJob> _lightJobs = new();
    // 光の強さの全体の倍率（mod.lightscale で変えられる）
    private float _lightScale = 1f;
    // ゲームの環境光を写した広い平行光源の倍率（mod.ambientscale）
    private float _ambientScale = 1f;
    private volatile bool _showCharacters;
    // 平行光源（太陽・月）だけの倍率（mod.sunscale）
    private float _sunScale = SunScale;
    private readonly List<RemixMesh> _meshes = new();
    private readonly Dictionary<ulong, RemixMesh> _meshByHash = new();
    private readonly List<ulong> _instances = new();
    private readonly Dictionary<int, ulong> _instanceByRenderer = new();
    // キャラクター: hash → メッシュと今の置き場所。毎フレーム DrawInstance で描く
    private readonly Dictionary<ulong, (RemixMesh mesh, Matrix4x4 toWorld)> _skinnedMeshes = new();

    // 描画スレッドで 1 フレームに Remix へ渡す仕事の時間の上限。場面の読み込み直後にゲームが固まりすぎないように
    private const long JobBudgetMs = 6;

    // 画面の切り替えと診断のキー。修正 Mod が F4・F5・F6・F9・F12・P・T を使うので、それ以外
    private const Key ToggleKey = Key.F8, CensusKey = Key.F7;

    private bool _remixOn = true;
    private int _lastFrame = -1;
    private RemixCompositePass _pass;
    private FogMaterials _fogMaterials;
    private readonly RemixResolve _resolve = new();
    private RenderTexture _hdrTarget, _fogFactor, _fogDepth, _overlayDepth;
    // Remix の絵は露出 -2 段（rtx.conf の rtx.tonemap.exposureBias = -2）で 0〜1 に収めて出させ、描画スレッドで 2^2 倍に戻す
    private const float RemixExposureStops = 2f;
    // ゲームの霧を Remix の絵に掛ける（mod.fog）
    private bool _fogOn = true;
    private int _savedMsaa = -1;
    private string _censusScene;
    private float _censusAt = -1f;

    internal void Init(RemixApi api, ManualLogSource log)
    {
        _api = api;
        _log = log;
        _geometry = new SceneGeometry(log);
        _skinned = new SkinnedGeometry(_geometry, log);
        _fogMaterials = new FogMaterials(log);
        _pass = new RemixCompositePass(s_onRenderEventPtr, _fogMaterials);
        // ゲームの D3D11 デバイスは、Unity のテクスチャからたどる
        var probe = new RenderTexture(4, 4, 0) { name = "RemixDeviceProbe" };
        probe.Create();
        var device = D3D11Native.Device(probe.GetNativeTexturePtr());
        try { PresentHook.Install(device, BeforePresent, m => _log.LogError(m)); }
        finally { D3D11Native.ReleaseRef(device); Destroy(probe); }
        // 切り分け用の立方体（mod.cube = 1）。メッシュの配列はメインスレッドで取っておく
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
        _cubePositions = cubeMesh.vertices; _cubeNormals = cubeMesh.normals; _cubeIndices = cubeMesh.triangles;
        Destroy(cube);
        s_current = this;
        _log.LogInfo($"{ToggleKey} で Remix とゲームの絵を切り替え、{CensusKey} で場面の中身をログに書きます");
    }

    private void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    private void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCamera;

    private void Update()
    {
        var keyboard = Keyboard.current;
        // Ctrl+F8: ADV を飛ばして Stage01 に直接入る（開発用）。F8 だけなら絵の切り替え
        if (keyboard != null && keyboard.ctrlKey.isPressed && keyboard[ToggleKey].wasPressedThisFrame) DebugJump.ToStage("Stage01", _log);
        else if (keyboard != null && keyboard[ToggleKey].wasPressedThisFrame)
        {
            _remixOn = !_remixOn;
            _log.LogInfo(_remixOn ? "Remix の絵に切り替えました" : "ゲームの絵に切り替えました");
        }
        if (keyboard != null && keyboard[CensusKey].wasPressedThisFrame) SceneCensus.Log(_log, $"{CensusKey}");

        // 場面が変わったら、物が出そろうのを少し待ってから中身を書く
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _censusScene) { _censusScene = scene; _censusAt = Time.unscaledTime + 3f; }
        if (_censusAt >= 0f && Time.unscaledTime >= _censusAt)
        {
            _censusAt = -1f;
            SceneCensus.Log(_log, $"{scene} に入って 3 秒");
            if (!_failed)
            {
                using (Timing.Measure("場面を集める")) _geometry.Rebuild(scene);
                using (Timing.Measure("キャラクターを集める")) _skinned.Rebuild(_geometry.Generation);
                _fogMaterials.TryCreate();
                if (_remixOn) _geometry.HideGlass(_fogMaterials.Invisible);
                int gen = _geometry.Generation;
                SkyCapture.Capture(scene, _log, path =>
                {
                    if (path != null && gen == _geometry.Generation) _geometry.Enqueue(new DomeJob { Hash = SceneGeometry.Hash(0x44, gen, 0, 0), Path = path });
                });
            }
        }

        OverrideGraphics();
        while (_geometry.MainThread.TryDequeue(out var work)) work();
        // 読み込みの仕事は、1 フレームに少しずつ（固まらないように）
        _geometry.Pump(5);
        _geometry.Materials.Pump(4);
        if (!_remixOn || _failed) _geometry.ShowGlass();
        if (_remixOn && !_failed)
        {
            // 1 秒ごとに、あとから出てきた物を足す
            // 場面が変わって作り直し待ちの間は見直さない（新しい場面の物を古い世代で拾ってしまう）
            if (_geometry.Generation > 0 && _censusAt < 0f && Time.unscaledTime >= _nextRescan)
            {
                _nextRescan = Time.unscaledTime + 1f;
                Plugin.KeepFourBones();
                if (Timing.Report(100) is { } slow) _log.LogInfo(slow);
                int statics = _geometry.Rescan(), skinned = _skinned.Rescan(), lights = _geometry.TrackLights(_geometry.Generation);
                if (statics + skinned + lights > 0) _log.LogInfo($"あとから出てきた物を足します: MeshRenderer {statics} 個、SkinnedMeshRenderer {skinned} 個、変わった光源 {lights} 個");
                _geometry.HideGlass(_fogMaterials.Invisible);
            }
            _skinned.Update();
            _geometry.TrackChanges(SceneCamera());
        }
        if (Time.unscaledTime >= _nextStatus)
        {
            _nextStatus = Time.unscaledTime + 10f;
            _log.LogInfo($"[状態] 描画スレッドのフレーム {_renderFrames}、残りの仕事 {_geometry.Jobs.Count}、メッシュ {_meshes.Count}、実体 {_instances.Count}、" +
                         $"テクスチャ {_textures.Count}、光源 {_lights.Count}、最後の仕事 {_lastJobMs} ms、" +
                         $"カメラ 位置 {_lastPose.Position} 前 {_lastPose.Forward} fov {_lastPose.FovY} aspect {_lastPose.Aspect} near {_lastPose.Near} far {_lastPose.Far}");
        }
        if (Time.frameCount % 15 == 0) { CaptureIfRequested(); ConfigIfRequested(); if (_advAuto) DebugJump.KeepAdvAuto(); }
    }

    /// <summary>
    /// 開発用: プラグインのフォルダに capture.req が置かれたら、ゲームの最後の絵（UI 込み）を shots\ に保存する。
    /// 外から画面を撮ると、モニターや全画面の具合で黒くなることがあるため（2026-11-09 のキャラ選択で実際に起きた）。
    /// </summary>
    /// <summary>
    /// 開発用: プラグインのフォルダに config.req（1 行に「rtx.なにか = 値」）が置かれたら、描画スレッドで Remix に設定する。
    /// 再起動せずに Remix の設定（デバッグ表示、明るさなど）を試すため。
    /// </summary>
    private void ConfigIfRequested()
    {
        // 要求は 1 つずつ別のファイル（config*.req）。続けて送っても前のものを上書きしないように、名前の順に全部読む
        var dir = Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix");
        if (!Directory.Exists(dir)) return;
        foreach (var request in Directory.GetFiles(dir, "config*.req").OrderBy(f => f, StringComparer.Ordinal))
            ApplyConfigFile(request);
    }

    private void ApplyConfigFile(string request)
    {
        string[] lines;
        try { lines = File.ReadAllLines(request); File.Delete(request); }
        catch (IOException) { return; } // 書いている途中。次の見回りで読む
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.TrimStart().StartsWith("#")) continue;
            var (key, value) = (line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            // mod.remix はメインスレッドの切り替え（F8 と同じ）。キーボードが使えないときの比べ用
            if (key == "mod.remix") { _remixOn = value != "0"; _log.LogInfo(_remixOn ? "Remix の絵に切り替えました" : "ゲームの絵に切り替えました"); continue; }
            if (key == "mod.stage") { DebugJump.ToStage(value, _log); continue; }
            if (key == "mod.goto") { DebugJump.Goto(value, _log); continue; }
            if (key == "mod.advauto") { _advAuto = value != "0"; continue; }
            if (key == "mod.press") { DebugJump.Press(value); continue; }
            if (key == "mod.dump") { SceneDump.Log(_log); continue; }
            if (key == "mod.cam")
            {
                // 「x, y, z, 左右の角度, 上下の角度」で固定、「off」で戻す、「log」で今の場所を書く
                var cam = SceneCamera();
                if (value == "off") _camPose = null;
                else if (value == "log" && cam != null)
                    _log.LogInfo($"カメラ: {cam.transform.position.x:0.##}, {cam.transform.position.y:0.##}, {cam.transform.position.z:0.##}, {cam.transform.eulerAngles.y:0.#}, {cam.transform.eulerAngles.x:0.#}");
                else
                {
                    var n = value.Split(',').Select(x => float.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    _camPose = (new Vector3(n[0], n[1], n[2]), Quaternion.Euler(n.Length > 4 ? n[4] : 0f, n.Length > 3 ? n[3] : 0f, 0f));
                }
                continue;
            }
            if (key == "mod.fog") { _fogOn = value != "0"; continue; }
            if (key == "mod.pick") { Picker.Log(SceneCamera(), value, _log); continue; }
            if (key == "mod.lightsnear") { Picker.LogLights(value, _log); continue; }
            if (key == "mod.hdrdump") { DumpGameHdr(value); continue; }
            if (key == "mod.waypoints") { DebugJump.LogWaypoints(_log); continue; }
            if (key == "mod.damage") { DebugJump.Damage(value, _log); continue; }
            if (key == "mod.scene") { DebugJump.ToScene(value, _log); continue; }
            if (key == "mod.fills") { BakedLamps.Fills = value != "0"; continue; }
            if (key == "mod.lampcache") { SceneGeometry.LampCache = value != "0"; continue; }
            if (key == "mod.fillshare" && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var share)) { BakedLamps.FillShare = share; continue; }
            if (key == "mod.farfield") { BakedLamps.LogFarField = value != "0"; continue; }
            if (key == "mod.lmmap")
            {
                // 値は、地図に入れる面の高さの上限（m）
                float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float maxY);
                var path = Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix", "shots", $"lmmap_{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}.bin");
                System.Threading.Tasks.Task.Run(() => { try { _log.LogInfo(_geometry.Lamps.DumpGroundMap(path, maxY)); } catch (Exception e) { _log.LogWarning($"地図を書けません: {e}"); } });
                continue;
            }
            if (key == "mod.lmpick" && SceneCamera() is { } lmCam)
            {
                // 「x, y」（画面の割合、y は上から）
                var n = value.Split(',').Select(x => float.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var ray = lmCam.ViewportPointToRay(new Vector3(n[0], 1f - n[1], 0f));
                _log.LogInfo($"画面の ({n[0]}, {n[1]}): {_geometry.Lamps.Probe(ray.origin, ray.direction)}");
                continue;
            }
            if (key == "mod.emissive") { Picker.LogEmissive(_log); continue; }
            if (key == "mod.find") { Picker.LogFind(value, _log); _log.LogInfo($"カメラの cullingMask 0x{SceneCamera()?.cullingMask:X}"); continue; }
            if (key == "mod.matinfo") { Picker.LogMaterials(value, _log); continue; }
            if (key == "mod.matset") { Picker.SetMaterial(value, _log); continue; }
            if (key == "mod.sunhighlight") { MaterialLibrary.SunHighlights = value != "0"; continue; }
            // 開発用（mod.lightint = 名前, 強さ）: その名前の有効な Light の強さを変える（ゲーム自身の絵で、その光の分を切り分けるため。場面を読み直すと戻る）
            if (key == "mod.lightint")
            {
                var lp = value.Split(',');
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.name == lp[0].Trim()) l.intensity = float.Parse(lp[1], System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            if (key == "mod.shaders") { Picker.LogShaders(_log); continue; }
            if (key == "mod.near") { Picker.LogNear(value, _log); continue; }
            if (key == "mod.overlaps") { Picker.LogOverlaps(_log); continue; }
            // 開発用: Remix が持っているキャラクターの形を、Remix の絵にそのまま出す（ふだんは影と映り込みだけ）。映り込みに出る部品を確かめるため
            if ((key == "mod.toonglow" || key == "mod.toonalbedo") && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var toon))
            {
                if (key == "mod.toonglow") MaterialLibrary.ToonGlow = toon; else MaterialLibrary.ToonAlbedo = toon;
                continue;
            }
            if (key == "mod.watch") { var wp = value.Split(','); _watchName = wp[0].Trim(); _watchFrames = wp.Length > 1 ? int.Parse(wp[1]) : 30; continue; }
            // 開発用（mod.timescale = 0）: ゲームの時間を止める（動きに遅れる物かどうかの切り分け）
            if (key == "mod.timescale") { Time.timeScale = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (key == "mod.screen")
            {
                // 「1080p / 720p / full」: ゲーム内の設定と同じ切り替え（SystemData.Settings.SetDisplaySize）
                if (value == "1080p") GB.GBSystem.SystemSetting.SetDisplaySize(GB.Game.SystemData.Settings.DisplaySizes.Display1080p);
                else if (value == "720p") GB.GBSystem.SystemSetting.SetDisplaySize(GB.Game.SystemData.Settings.DisplaySizes.Display720p);
                else if (value == "full") GB.GBSystem.SystemSetting.SetDisplaySize(GB.Game.SystemData.Settings.DisplaySizes.FullScreen);
                _log.LogInfo($"Screen: {Screen.width}x{Screen.height} {Screen.fullScreenMode} fullScreen {Screen.fullScreen}");
                continue;
            }
            if (key == "mod.passtest") { RemixCompositePass.Test = int.Parse(value); continue; }
            if (key == "mod.overlay") { RemixCompositePass.DrawOverlay = value != "0"; continue; }
            if (key == "mod.showchara") { _showCharacters = value != "0"; continue; }
            if (key == "mod.cull") { SceneGeometry.CullBackFaces = value != "0"; continue; }
            if (key == "mod.uvrange") { SceneGeometry.UvRangeOf = value == "0" ? null : value; continue; }
            if (key == "mod.lodcull") { _geometry.LodCulling = value != "0"; continue; }
            if (key == "mod.texprobe") { Picker.LogTextureProbe(value, _log); continue; }
            if (key == "mod.texdump") { Picker.DumpTexture(value, Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix", "shots"), _log); continue; }
            if (key == "mod.torn")
            {
                // 開発用: 体力が半分になったときの、服が破れる切り替え（ゲームの Chara.ChangeTorn）を呼ぶ。Remix で服が入れ替わるかを確かめるため
                foreach (var chara in FindObjectsByType<GB.Character.Chara>(FindObjectsSortMode.None)) chara.ChangeTorn(value != "0");
                continue;
            }
            if (key == "mod.emissivescale" && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var em))
            {
                _geometry.Materials.EmissiveScale = em;
                _geometry.Materials.ResendAll();
                continue;
            }
            if (key == "mod.roughmin" && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rmin))
            {
                _geometry.Materials.RoughnessFloor = rmin;
                _geometry.Materials.ResendAll();
                _log.LogInfo($"粗さの下限: {rmin}");
                continue;
            }
            if (key == "mod.wetsmooth") { MaterialLibrary.WetSmoothness = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (key == "mod.wettint" && int.TryParse(value, out var wet))
            {
                _geometry.Materials.WetMetalTint = Mathf.Clamp(wet, 1, 15);
                _geometry.Materials.ResendAll();
                continue;
            }
            if (key == "mod.metaltint" && int.TryParse(value, out var tint))
            {
                _geometry.Materials.MetalTint = Mathf.Clamp(tint, 1, 15);
                _geometry.Materials.ResendAll();
                continue;
            }
            _configs.Enqueue((key, value));
        }
    }

    /// <summary>
    /// 開発用（mod.hdrdump = 1 か名前）: ゲーム自身の絵を、ポストプロセスの前の HDR（線形）のまま shots\ に書く（float16 の RGBA、先頭に幅と高さ）。名前を付けるとファイル名に入る。
    /// Remix の光の強さをゲームに合わせるとき、トーンマップと色調補正を挟まずに比べるため。
    /// </summary>
    private void DumpGameHdr(string tag)
    {
        var rt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "RemixHdrDump" };
        rt.Create();
        _pass.Dump = rt;
        var stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}{(tag == "1" ? "" : "_" + tag)}";
        StartCoroutine(ReadBack());
        System.Collections.IEnumerator ReadBack()
        {
            yield return null;
            yield return null;
            // ゲームの絵は Unity の向き（下の行が先）、Remix の絵（HDR に戻した物）は上の行が先
            Write(rt, "hdr_game", destroy: true);
            if (_hdrTarget != null) Write(_hdrTarget, "hdr_remix", destroy: false);
        }
        void Write(RenderTexture tex, string name, bool destroy)
        {
            var path = Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix", "shots", $"{stamp}_{name}.bin");
            AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBAHalf, req =>
            {
                if (!req.hasError)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using var w = new BinaryWriter(File.Create(path));
                    w.Write(tex.width);
                    w.Write(tex.height);
                    w.Write(req.GetData<byte>().ToArray());
                    _log.LogInfo($"HDR の絵を書きました: {path}");
                }
                if (destroy) Destroy(tex);
            });
        }
    }

    private void CaptureIfRequested()
    {
        var request = Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix", "capture.req");
        if (!File.Exists(request)) return;
        // 中身が「枚数, 間隔（秒）」なら続けて撮る（パンツの取得の演出など、すぐ消える物を見るため）
        string[] spec;
        try { spec = File.ReadAllText(request).Split(','); File.Delete(request); }
        catch (IOException) { return; }
        int count = int.TryParse(spec[0].Trim(), out var n) ? Mathf.Clamp(n, 1, 20) : 1;
        float interval = spec.Length > 1 && float.TryParse(spec[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var iv) ? iv : 0.25f;
        StartCoroutine(Capture());
        System.Collections.IEnumerator Capture()
        {
            var dir = Path.Combine(Paths.PluginPath, "HeberekeBunnyGardenRemix", "shots");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) yield return new WaitForSeconds(interval);
                var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SceneManager.GetActiveScene().name}_{(_remixOn ? "remix" : "game")}.png");
                ScreenCapture.CaptureScreenshot(path);
                _log.LogInfo($"画面を保存します: {path}");
            }
        }
    }

    /// <summary>
    /// 修正 Mod などが変えたグラフィックの設定のうち、Remix と相容れないものをこちらで上書きする。
    /// MSAA: Remix の絵で塗りつぶすので、ゲームの MSAA は GPU を使うだけ。Remix を切ったら元に戻す。
    /// 毎フレーム見るのは、修正 Mod が後から設定し直しても勝つため。
    /// </summary>
    private void OverrideGraphics()
    {
        if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp) return;
        if (_remixOn && urp.msaaSampleCount != 1)
        {
            _savedMsaa = urp.msaaSampleCount;
            urp.msaaSampleCount = 1;
            _log.LogInfo($"Remix で描く間は MSAA を切ります（元は {_savedMsaa}x）");
        }
        else if (!_remixOn && _savedMsaa > 1)
        {
            urp.msaaSampleCount = _savedMsaa;
            _savedMsaa = -1;
        }
    }

    /// <summary>
    /// URP が画面向けのカメラを描き始めるところ。そのフレームの最初の 1 台でカメラを写し取り、
    /// 画面向けのカメラ全部に Remix の絵を写すパスを積む。パスは、カメラの重なりの最後（画面に出す 1 台）でだけ動く。
    /// </summary>
    // 開発用（mod.watch = 名前の一部, フレーム数）: 名前にそれを含む Renderer が、カメラが描き始める瞬間に有効かどうかを、フレームごとに書く
    private string _watchName;
    private int _watchFrames;

    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (_watchFrames > 0 && _watchName != null)
        {
            _watchFrames--;
            var states = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(r => r.name.Contains(_watchName))
                .Select(r => $"{r.name}:{(r.enabled ? "有効" : "無効")}{(r.gameObject.activeInHierarchy ? "" : "(GO無効)")}{(r.isVisible ? "" : "(見えていない)")}");
            _log.LogInfo($"[watch] フレーム {Time.frameCount} カメラ {camera.name}（{camera.cameraType}、的 {(camera.targetTexture != null ? camera.targetTexture.name : "画面")}）: {string.Join(" ", states)}");
        }
        // 開発用: 決めた場所と向きからの眺めにする（mod.cam）。URP はこのあとでカメラの行列を読むので、ゲームの絵にも Remix の絵にも効く
        if (_camPose is { } pose && camera.targetTexture == null && !SkyCapture.Capturing && camera == SceneCamera())
            camera.transform.SetPositionAndRotation(pose.pos, pose.rot);
        if (_failed || !_remixOn || _pass == null || camera.targetTexture != null || SkyCapture.Capturing) return;
        var urp = camera.GetUniversalAdditionalCameraData();
        if (urp == null || urp.scriptableRenderer == null) return;

        if (Time.frameCount != _lastFrame)
        {
            var scene3D = SceneCamera();
            if (scene3D == null) return;
            _lastFrame = Time.frameCount;

            EnsureTarget();

            int slot = Time.frameCount % FrameRing;
            // 霧は、3D のカメラが重ね無しで画面に出す（霧の濃さを測るカメラと、写すカメラが同じ）ときだけ
            var extra = scene3D.GetUniversalAdditionalCameraData();
            bool fog = _fogOn && _remixHasScene && RenderSettings.fog && _fogMaterials.Ready && extra != null &&
                       extra.renderType == CameraRenderType.Base && (extra.cameraStack == null || extra.cameraStack.Count == 0);
            // 3D のカメラがほかのカメラに重ねて描く物（キャラ選択: ぼかした背景の絵を描く BgCamera の上に、キャラクターの GameCamera が重なる）のときは写さない。
            // 写すと、下のカメラが描いた背景を Remix の空の色で塗りつぶした
            bool stacked = extra != null && extra.renderType == CameraRenderType.Overlay;
            _frames[slot] = new Frame
            {
                Camera = CameraPose.From(scene3D), Target = _target.GetNativeTexturePtr(), Hdr = _hdrTarget.GetNativeTexturePtr(),
                FogFactor = fog ? _fogFactor.GetNativeTexturePtr() : IntPtr.Zero,
                FogColor = RenderSettings.fogColor.linear,
                Shown = !stacked,
            };
            _pass.Setup(_hdrTarget, slot, composite: _remixHasScene && !stacked, fog ? scene3D : null, _fogFactor, _fogDepth, _overlayDepth, _geometry.Materials.Highlights);
        }
        if (RemixCompositePass.Test != 2) urp.scriptableRenderer.EnqueuePass(_pass);
    }

    /// <summary>
    /// 3D の場面を映しているカメラ。キャラ選択では背景の絵を出す平行投影のカメラ（BgCamera）の上に、
    /// 3D のカメラ（GameCamera）が重なっているので、Camera.main ではなく「画面向けの透視投影で、いちばん手前のもの」を選ぶ。
    /// </summary>
    private static Camera SceneCamera()
    {
        var main = Camera.main;
        if (main != null && !main.orthographic && main.targetTexture == null) return main;
        return Camera.allCameras
            .Where(c => !c.orthographic && c.targetTexture == null)
            .OrderByDescending(c => c.depth)
            .FirstOrDefault();
    }

    /// <summary>Remix に描かせる先。画面と同じ大きさで MSAA 無し（RenderD3D11 の決まり）。</summary>
    private void EnsureTarget()
    {
        if (_target != null && _target.width == Screen.width && _target.height == Screen.height) return;
        // 古いものの破棄は描画スレッドのコマンドの並びに入るので、先に出した合図が使い終えてから消える
        if (_target != null) { Destroy(_target); Destroy(_hdrTarget); Destroy(_fogFactor); Destroy(_fogDepth); Destroy(_overlayDepth); }
        _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            antiAliasing = 1,
            name = "RemixOutput",
        };
        _target.Create();
        // Remix の絵をゲームの尺度（露出を戻した線形）に戻す先。URP のパスがここからカメラの色に写す
        _hdrTarget = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "RemixHdr" };
        _hdrTarget.Create();
        _fogFactor = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "RemixFogFactor" };
        _fogFactor.Create();
        _fogDepth = new RenderTexture(Screen.width, Screen.height, 32, RenderTextureFormat.Depth) { name = "RemixFogDepth" };
        _fogDepth.Create();
        // ゲーム自身に描き直させるときの深さとステンシル。URP のカメラと同じ 32 ビット浮動小数の深さ + 8 ビットのステンシル。
        // 24 ビットだと、顔のすぐ上に重なる頬の赤み（mesh_face_blush）が顔と深さを取り合い、ひどくちらついた（2026-11-10）
        _overlayDepth = new RenderTexture(new RenderTextureDescriptor(Screen.width, Screen.height, GraphicsFormat.None, GraphicsFormat.D32_SFloat_S8_UInt)) { name = "RemixOverlayDepth" };
        _overlayDepth.Create();
        _log.LogInfo($"Remix の描き先を作りました: {Screen.width}x{Screen.height}");
    }

    private static void OnRenderEvent(int slot, IntPtr data)
    {
        var self = s_current;
        if (self == null || self._failed) return;
        try
        {
            self.RenderFrame(self._frames[slot]);
        }
        catch (Exception e)
        {
            // 毎フレーム同じ例外でログが埋まらないよう、最初の 1 回で止める
            self._failed = true;
            self._log.LogError($"Remix への受け渡しを止めました: {e}");
        }
    }

    /// <summary>
    /// 描画スレッド（ゲームの Present の直前）。スワップチェーンのバッファを、Remix にフレームの締め先として渡す。
    /// これが無いと Remix のフレームが進まず、2 フレーム目から描かれない。
    /// </summary>
    private void BeforePresent(IntPtr swapChainBuffer)
    {
        if (_registered && !_failed) _api.EndFrameOn(swapChainBuffer);
    }

    // 描画スレッドだけが触る
    private IntPtr _lastTarget;
    private bool _lastRender;
    private volatile bool _remixHasScene;

    /// <summary>描画スレッド。1 フレーム分を Remix に渡して描かせる。</summary>
    private void RenderFrame(in Frame frame)
    {
        if (!_registered)
        {
            _api.RegisterD3D11Device(frame.Target);
            _registered = true;
            _log.LogInfo("RTX Remix にゲームの D3D11 デバイスを渡しました");
        }

        while (_configs.TryDequeue(out var c))
        {
            // 「mod.」で始まるものは、この Mod の切り分け用のスイッチ
            if (c.key == "mod.lights") _drawLights = c.value != "0";
            else if (c.key == "mod.retained") _drawRetained = c.value != "0";
            else if (c.key == "mod.cube") _drawCube = c.value != "0";
            else if (c.key == "mod.skyscale" && float.TryParse(c.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sky))
            {
                _skyScale = sky;
                if (_dome is { } d && _domePath != null)
                {
                    // 中身が変わるので新しい取っ手で作り直す（同じ中身の作り直しは消える。RecreateLights の説明）
                    _api.DestroyLight(d);
                    _dome = _api.CreateDomeLight(SceneGeometry.Hash(0x44, (int)_lightVersion++, 1, 0), Color.white * _skyScale, SkyCapture.DomeToWorld, _domePath);
                }
            }
            else if (c.key == "mod.areascale" && float.TryParse(c.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var area))
            {
                _areaScale = area;
                foreach (var lj in _lightJobs)
                    if (lj.Type == LightType.Rectangle && _lights.TryGetValue(lj, out var cur)) { _api.DestroyLight(cur.light); _lights.Remove(lj); SyncLight(lj); }
            }
            else if (c.key == "mod.rectflip")
            {
                _rectFlip = c.value != "0";
                // 向きが変わるので、面光源だけ作り直す（中身が変わるので消えない）
                foreach (var lj in _lightJobs)
                    if (lj.Type == LightType.Rectangle && _lights.TryGetValue(lj, out var cur)) { _api.DestroyLight(cur.light); _lights.Remove(lj); SyncLight(lj); }
            }
            else if (c.key == "mod.skiplight")
            {
                _skipLights = c.value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var lj in _lightJobs) SyncLight(lj);
            }
            else if (c.key == "mod.bakedlights")
            {
                _bakedLights = c.value != "0";
                RecreateLights();
            }
            else if (c.key == "mod.ambientscale" && float.TryParse(c.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amb))
            {
                _ambientScale = amb;
                RecreateLights();
            }
            else if ((c.key == "mod.lightscale" || c.key == "mod.sunscale") &&
                     float.TryParse(c.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale))
            {
                if (c.key == "mod.sunscale") _sunScale = scale; else _lightScale = scale;
                RecreateLights();
            }
            else _api.SetConfig(c.key, c.value);
            _log.LogInfo($"Remix の設定: {c.key} = {c.value}");
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool worked = false;
        while (clock.ElapsedMilliseconds < JobBudgetMs && _geometry.Jobs.TryDequeue(out var job))
        {
            // 古い場面の仕事は捨てる（解放済みのテクスチャなどを指しているかもしれない）
            if (job.Gen < _geometry.LatestGeneration) continue;
            Apply(job);
            worked = true;
        }
        if (worked) _lastJobMs = (int)clock.ElapsedMilliseconds;

        _api.SetupCamera(frame.Camera);
        if (_drawLights) foreach (var l in _lights.Values) _api.DrawLight(l.light);
        if (_drawRetained) _api.DrawRetainedInstances();
        // 見えている部品だけ描く（服が破れる演出で、部品が出し入れされる）
        var visibleSkinned = _skinned.Visible;
        foreach (var h in visibleSkinned)
            if (_skinnedMeshes.TryGetValue(h, out var sk)) _api.DrawInstance(sk.mesh, sk.toWorld, doubleSided: true, _showCharacters ? 0u : RemixApi.CategoryShadowOnly);
        if (_drawCube)
        {
            if (!_hasCube)
            {
                var white = _api.CreateMaterial(0x48424752_43554245UL, Color.white, roughness: 0.5f, metallic: 0f);
                _cube = _api.CreateMesh(0x48424752_43554246UL, _cubePositions, _cubeNormals, null, _cubeIndices, white);
                _hasCube = true;
            }
            var p = frame.Camera;
            _api.DrawInstance(_cube, Matrix4x4.TRS(p.Position + p.Forward * 3f, Quaternion.Euler(30f, 45f, 0f), Vector3.one), doubleSided: true);
        }
        _lastPose = frame.Camera;
        // Remix の絵を画面に写さないフレーム（渡した形が無い場面、重ねカメラの場面: キャラ選択）では、描かせない（描いても使わない）
        bool hasScene = (_drawRetained && _instances.Count > 0) || _skinnedMeshes.Count > 0 || _drawCube;
        bool render = hasScene && frame.Shown && RemixCompositePass.Test != 5;
        if (render != _lastRender)
        {
            _lastRender = render;
            _log.LogInfo(render ? "Remix に描かせます" : $"Remix には描かせません（形 {(hasScene ? "あり" : "無し")}、画面に写す {frame.Shown}）");
        }
        // 描かせる前に D3D11 の Flush（理由は RemixResolve.Flush: 頬の赤みがフレームによって消える件）
        if (render && RemixCompositePass.Test != 8) _resolve.Flush(frame.Target);
        _api.RenderD3D11(frame.Target, render);
        if (render) _resolve.Apply(frame.Target, frame.Hdr, frame.FogFactor, frame.FogColor, Mathf.Pow(2f, RemixExposureStops), m => { _failed = true; _log.LogError(m); });
        _lastTarget = frame.Target;
        _renderFrames++;
        // Remix は形（面）が 1 つも無いフレームでは描かない。そのときはゲームの絵のままにする（次のフレームから効く）
        _remixHasScene = hasScene;
    }

    /// <summary>描画スレッド。面の説明を Remix の面にする。素材は MaterialJob で先に作ってある（無ければ灰色で作る）。</summary>
    private MeshSurface[] ToSurfaces(SceneGeometry.SurfaceDesc[] descs)
    {
        var surfaces = new MeshSurface[descs.Length];
        for (int i = 0; i < surfaces.Length; i++)
        {
            ref readonly var d = ref descs[i];
            if (!_materials.TryGetValue(d.MaterialHash, out var mat))
                _materials[d.MaterialHash] = mat = _api.CreateMaterial(d.MaterialHash, Color.gray, roughness: 0.7f, metallic: 0f);
            surfaces[i] = new MeshSurface { Positions = d.Positions, Normals = d.Normals, Uvs = d.Uvs, Indices = d.Indices, Material = mat };
        }
        return surfaces;
    }

    /// <summary>描画スレッド。素材を作る（同じ hash なら作り直す）。テクスチャは初めて出てきたときに取り込む。</summary>
    private void ApplyMaterial(MaterialLibrary.MaterialJob job)
    {
        // Remix Plus 2.0.0 は、同じ hash の 2 回目の CreateMaterial を捨てる（ログの「Ignoring repeated material registration」）。
        // 先に消してから作る。消すと、その素材を使う実体は作り直される。後から届く並べ替えた絵（ノーマル・粗さ）が、これまで全部捨てられていた（2026-11-10）
        if (_materials.TryGetValue(job.Hash, out var old)) _api.DestroyMaterial(old);
        _materials[job.Hash] = _api.CreateMaterial(job.Hash, job.Params, ImportTexture(job.Albedo), ImportTexture(job.Normal), ImportTexture(job.Roughness),
            ImportTexture(job.Metallic), ImportTexture(job.Emissive));
    }

    /// <summary>描画スレッド。テクスチャを取り込み、素材から指す hash を返す（取り込めなければ 0 でテクスチャ無し）。</summary>
    private ulong ImportTexture(in SceneGeometry.TextureDesc t)
    {
        if (t.Hash == 0) return 0;
        if (_textures.ContainsKey(t.Hash)) return t.Hash;
        try
        {
            _textures[t.Hash] = _api.ImportD3D11Texture(t.Ptr, t.DxgiFormat, t.Hash, MaxTextureSize, t.Encoding);
            return t.Hash;
        }
        catch (RemixException e)
        {
            if (++_textureFailures <= 5) _log.LogWarning($"テクスチャを Remix に取り込めません（形式 {t.DxgiFormat}）: {e.Message}");
            return 0;
        }
    }

    private int _textureFailures;
    private bool _advAuto;
    private float _nextStatus, _nextRescan;
    private (Vector3 pos, Quaternion rot)? _camPose;
    private bool _drawLights = true, _drawRetained = true, _drawCube;
    private RemixMesh _cube;
    private bool _hasCube;
    private Vector3[] _cubePositions, _cubeNormals;
    private int[] _cubeIndices;
    private CameraPose _lastPose;
    private volatile int _renderFrames;
    private volatile int _lastJobMs;
    // 取り込むテクスチャの大きさの上限。VRAM を食いすぎないように（Remix Plus の mip の上限）
    private const uint MaxTextureSize = 2048;

    // Unity（URP）の光の強さを Remix の値にする。URP は π を光の強さに含めている（拡散 = albedo × 強さ × N·L）。
    // 平行光源: Remix は 放射輝度 / sin²(半角) で使うので、面の照度は π × 値。URP の強さ I なら値は I のまま（倍率 1）。
    // 点・スポット: 物理の強さは π I。半径 r の球の光源の強さは L π r² なので、L = I / r²。
    // 面光源（URP では焼き込み専用）は、面の放射輝度 = 強さ / π（_areaScale）
    private const float SunScale = 1f, SphereRadius = 0.05f;

    /// <summary>
    /// 描画スレッド。強さや選び方を変えたので、光源を合わせ直す。強さは作るときに決まるので、変わった物だけ消して作り直す。
    /// 中身が同じ光源を消して作り直すと、Remix は同じ物と見なすらしく、後回しの消去で新しい方まで消える
    /// （2026-11-09 に、太陽だけ強さを変えたら点光源と面光源が 0 個になった）。
    /// </summary>
    private void RecreateLights()
    {
        foreach (var lj in _lightJobs) SyncLight(lj);
    }

    private void SyncLight(SceneGeometry.LightJob job)
    {
        bool want = (_bakedLights || !job.BakedOnly) && !_skipLights.Any(n => job.Name.Contains(n));
        float scale = job.Ambient ? _ambientScale : _lightScale * (job.Type == LightType.Directional ? _sunScale : 1f);
        if (_lights.TryGetValue(job, out var current))
        {
            if (want && current.scale == scale) return;
            _api.DestroyLight(current.light);
            _lights.Remove(job);
        }
        if (want) _lights[job] = (CreateLight(job, scale), scale);
    }

    // ゲームでは焼き込みだけに効く光も Remix に出すか（mod.bakedlights）。Remix はライトマップを使わないので、出さないとその光は無くなる
    private bool _bakedLights = true;
    // 開発用（mod.skiplight = 名前の一部|名前の一部）: 道筋にその名前を含む光源を出さない。どの光が明るすぎるかを切り分けるため
    private string[] _skipLights = Array.Empty<string>();
    // 面光源の光る向きを逆にするか（mod.rectflip）。Unity の面光源は forward の側に光る: 逆にすると、通りの看板（SkinNature）を照らす光が看板に当たり、
    // ゲームでは暗い看板が明るくなった（2026-11-10 に並べて確かめた。11-09 に逆と見たのは、空と環境光が強すぎた頃の見立て）
    private bool _rectFlip;
    // 面光源の強さの倍率（mod.areascale）。URP は π を強さに含めるので、面の放射輝度は 強さ / π
    private float _areaScale = 1f / Mathf.PI;

    /// <summary>空（ドームライト）を作る仕事。</summary>
    private sealed class DomeJob : SceneGeometry.Job
    {
        public ulong Hash;
        public string Path;
    }

    // 描画スレッドだけが触る。空と、その明るさの倍率（mod.skyscale）
    private RemixLight? _dome;
    private string _domePath;
    // 0.5: Remix Plus はドームライトがあると、外れた視線に空を 2 回足す（geometry_resolver.slangh の外れの処理と、composite.comp.slang の
    // applySkyContribution）。半分にすると、見える空がゲームの空と同じ明るさになる（2026-11-10 に測った）。照らす光も半分になる
    private float _skyScale = 0.5f;
    private ulong _lightVersion;

    /// <summary>描画スレッド。Unity の光源を Remix の光源にする。作り直すたびに版の番号を混ぜて、前の取っ手と別の hash にする。</summary>
    private RemixLight CreateLight(SceneGeometry.LightJob job, float scale)
    {
        ulong version = _lightVersion++;
        var l = new SceneGeometry.LightJob
        {
            Hash = version == 0 ? job.Hash : (job.Hash ^ (version * 0x9E3779B97F4A7C15UL)) | 1, Type = job.Type, Position = job.Position, Forward = job.Forward, Right = job.Right, Up = job.Up,
            Radiance = job.Radiance * scale, Range = job.Range, SpotAngle = job.SpotAngle, InnerSpotAngle = job.InnerSpotAngle, AreaSize = job.AreaSize,
        };
        switch (l.Type)
        {
            case LightType.Directional:
                return _api.CreateDistantLight(l.Hash, l.Forward, l.Radiance, angularDiameterDegrees: job.AngularDiameter);
            case LightType.Spot:
            case LightType.Point:
                // 点光源の強さ I（URP、1 m 先の明るさ）を、半径 r の球の面の放射輝度 L = I / r² にする（上の説明）
                float radius = job.Radius > 0f ? job.Radius : SphereRadius;
                var radiance = l.Radiance / (radius * radius);
                LightShaping? shaping = l.Type == LightType.Spot
                    ? new LightShaping
                    {
                        Direction = l.Forward,
                        ConeAngleDegrees = l.SpotAngle * 0.5f,
                        // Remix のぼかしは cos の幅（smoothstep(cos 外, cos 外 + ぼかし, cos θ)、light_shaping.slangh）。内側の角度で明るさがいっぱいになるよう、
                        // cos 内 − cos 外 にする。角度の割合（1 − 内 / 外）を渡していた頃は、真ん中でも届かず、スポットが半分以下の明るさだった（2026-11-10）
                        ConeSoftness = Mathf.Max(Mathf.Cos(l.InnerSpotAngle * 0.5f * Mathf.Deg2Rad) - Mathf.Cos(l.SpotAngle * 0.5f * Mathf.Deg2Rad), 1e-3f),
                    }
                    : null;
                return _api.CreateSphereLight(l.Hash, l.Position, radius, radiance, shaping);
            default:
                // Rectangle（エリアライト）。Unity のエリアライトは光る向きが forward
                return _api.CreateRectLight(l.Hash, l.Position, _rectFlip ? -l.Right : l.Right, Mathf.Max(l.AreaSize.x, 0.01f), l.Up, Mathf.Max(l.AreaSize.y, 0.01f),
                    _rectFlip ? -l.Forward : l.Forward, l.Radiance * _areaScale);
        }
    }

    /// <summary>描画スレッド。<see cref="SceneGeometry"/> が並べた仕事を Remix に渡す。</summary>
    private void Apply(SceneGeometry.Job job)
    {
        switch (job)
        {
            case SceneGeometry.ResetJob:
                // 実体はメッシュより先に消す（Remix の決まり）
                _api.DestroyRetainedInstances(_instances.ToArray());
                _instances.Clear();
                _instanceByRenderer.Clear();
                foreach (var m in _meshes) _api.DestroyMesh(m);
                _meshes.Clear();
                _meshByHash.Clear();
                foreach (var sk in _skinnedMeshes.Values) _api.DestroyMesh(sk.mesh);
                _skinnedMeshes.Clear();
                foreach (var m in _materials.Values) _api.DestroyMaterial(m);
                _materials.Clear();
                foreach (var t in _textures.Values) _api.DestroyTexture(t);
                _textures.Clear();
                foreach (var l in _lights.Values) _api.DestroyLight(l.light);
                _lights.Clear();
                _lightJobs.Clear();
                if (_dome is { } dome) _api.DestroyLight(dome);
                _dome = null;
                break;
            case SceneGeometry.LightJob lj:
                // 同じ Light の前の光は消す（点き直し・動いた・変わった、または消えた）
                if (lj.Id != 0 && _lightJobs.FindIndex(j => j.Id == lj.Id) is var at and >= 0)
                {
                    if (_lights.TryGetValue(_lightJobs[at], out var cur)) { _api.DestroyLight(cur.light); _lights.Remove(_lightJobs[at]); }
                    _lightJobs.RemoveAt(at);
                }
                if (lj.Off) break;
                _lightJobs.Add(lj);
                SyncLight(lj);
                break;
            case DomeJob dj:
                if (_dome is { } oldDome) _api.DestroyLight(oldDome);
                _dome = _api.CreateDomeLight(dj.Hash, Color.white * _skyScale, SkyCapture.DomeToWorld, dj.Path);
                _domePath = dj.Path;
                break;
            case MaterialLibrary.MaterialJob matJob:
                ApplyMaterial(matJob);
                break;
            case SceneGeometry.UpdateJob uj:
            {
                // まだ Remix に渡していない物（読み戻し中）の分は飛ばす。渡すときに、その時点の場所で作られる
                var ids = new List<ulong>(); var ms = new List<Matrix4x4>(); var vs = new List<bool>();
                for (int i = 0; i < uj.RendererIds.Length; i++)
                {
                    if (!_instanceByRenderer.TryGetValue(uj.RendererIds[i], out var h)) continue;
                    ids.Add(h); ms.Add(uj.Matrices[i]); vs.Add(uj.Visible[i]);
                }
                _api.UpdateRetainedInstances(ids.ToArray(), ms.ToArray(), vs.ToArray());
                break;
            }
            case SkinnedGeometry.SkinnedJob sj:
            {
                var parts = ToSurfaces(sj.Surfaces);
                var skinnedMesh = sj.Refresh && _skinnedMeshes.ContainsKey(sj.Hash) ? _api.RefreshMesh(sj.Hash, parts) : _api.CreateMesh(sj.Hash, parts);
                _skinnedMeshes[sj.Hash] = (skinnedMesh, sj.ToWorld);
                break;
            }
            case SceneGeometry.MeshJob mj:
                if (!_meshByHash.TryGetValue(mj.Hash, out var mesh))
                {
                    mesh = _api.CreateMesh(mj.Hash, ToSurfaces(mj.Surfaces));
                    _meshByHash[mj.Hash] = mesh;
                    _meshes.Add(mesh);
                }
                // shortcut: 両面で描く。片面にするのは、手の向き（左右の鏡写し）を確かめてから
                var handles = _api.CreateRetainedInstances(mesh, mj.Instances, mj.DoubleSided, mj.Overlay ? RemixApi.CategoryShadowOnly : 0u);
                _instances.AddRange(handles);
                for (int i = 0; i < handles.Length; i++) _instanceByRenderer[mj.RendererIds[i]] = handles[i];
                break;
        }
    }

    private void OnDestroy()
    {
        if (s_current == this) s_current = null;
        if (_target != null) Destroy(_target);
    }
}
