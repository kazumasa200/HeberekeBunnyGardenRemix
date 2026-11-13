using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// ゲームの空（RenderSettings.skybox）を、Remix のドームライトの絵（緯度経度の DDS）にする。メインスレッドで動く。
/// 空の素材の設定（明るさ・回転・色味）ごと Unity に描かせたいので、空だけを映すカメラで立方体の絵に描き（RenderToCubemap）、
/// 6 面を読み戻して、Remix の並び（light_helper.slangh の cartesianDirectionToLatLongSphere）に並べ直す。
/// ドームの空間は z が上: ドームの (x, y, z) をワールドの (x, z, y) に置く行列（<see cref="DomeToWorld"/>）と一緒に使う。
/// </summary>
internal static class SkyCapture
{
    /// <summary>描いている間は true。Remix の絵を写すパスを、このカメラに積まないため。</summary>
    public static bool Capturing { get; private set; }

    private const int FaceSize = 512, Width = 2048, Height = 1024;

    /// <summary>remixapi_Transform（3x4、行優先）: ドームの x → ワールド x、y → ワールド z、z → ワールド y。</summary>
    public static readonly float[] DomeToWorld = { 1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0 };

    /// <summary>空を描いて DDS に書き、そのフルパスを done に渡す（空が無い・描けないときは null）。</summary>
    public static void Capture(string scene, ManualLogSource log, Action<string> done)
    {
        var sky = RenderSettings.skybox;
        if (sky == null) { done(null); return; }

        var cube = new RenderTexture(FaceSize, FaceSize, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { dimension = TextureDimension.Cube, name = "RemixSkyCapture" };
        cube.Create();
        var go = new GameObject("RemixSkyCapture");
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.cullingMask = 0;
        cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        bool ok;
        Capturing = true;
        try { ok = cam.RenderToCubemap(cube, 63); }
        finally { Capturing = false; Object.Destroy(go); }
        if (!ok)
        {
            log.LogWarning("空を立方体の絵に描けませんでした（RenderToCubemap）");
            cube.Release();
            done(null);
            return;
        }

        var faces = new ushort[6][];
        int left = 6;
        for (int f = 0; f < 6; f++)
        {
            int face = f;
            AsyncGPUReadback.Request(cube, 0, 0, FaceSize, 0, FaceSize, face, 1, TextureFormat.RGBAHalf, req =>
            {
                if (!req.hasError) faces[face] = req.GetData<ushort>().ToArray();
                if (--left > 0) return;
                cube.Release();
                if (Array.Exists(faces, x => x == null)) { log.LogWarning("空の絵を読み戻せませんでした"); done(null); return; }
                try
                {
                    var path = Path.Combine(Paths.CachePath, "HeberekeBunnyGardenRemix", $"sky_{Sanitize(scene)}.dds");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteDds(path, ToLatLong(faces));
                    log.LogInfo($"空（{sky.name}）をドームライトの絵にしました: {path}");
                    done(path);
                }
                catch (Exception e) { log.LogWarning($"空の絵を書けませんでした: {e.Message}"); done(null); }
            });
        }
    }

    /// <summary>
    /// 緯度経度の絵にする。画素 (u, v) は Remix では θ = v·π、φ = (u - 0.5)·2π、ドームの向き (sinθ sinφ, sinθ cosφ, cosθ)。
    /// それを <see cref="DomeToWorld"/> でワールドの向きにして、立方体の絵を引く。
    /// </summary>
    private static ushort[] ToLatLong(ushort[][] faces)
    {
        var o = new ushort[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            float theta = (y + 0.5f) / Height * Mathf.PI;
            for (int x = 0; x < Width; x++)
            {
                float phi = ((x + 0.5f) / Width - 0.5f) * 2f * Mathf.PI;
                var dome = new Vector3(Mathf.Sin(theta) * Mathf.Sin(phi), Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta));
                var world = new Vector3(dome.x, dome.z, dome.y);
                Sample(faces, world, o, (y * Width + x) * 4);
            }
        }
        return o;
    }

    /// <summary>
    /// 立方体の絵を向きで引く（最近傍）。面の並びと面の中の向きは D3D の決まり（+X、-X、+Y、-Y、+Z、-Z）。
    /// 読み戻した面の行は D3D の並び（上の行が先）のまま。上下を返すと、横の 4 面の雲が地平線の下に出た（2026-11-10 に確かめた）。
    /// </summary>
    private static void Sample(ushort[][] faces, Vector3 d, ushort[] o, int at)
    {
        float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
        int face; float sc, tc, ma;
        if (ax >= ay && ax >= az) { ma = ax; face = d.x > 0 ? 0 : 1; sc = d.x > 0 ? -d.z : d.z; tc = -d.y; }
        else if (ay >= az) { ma = ay; face = d.y > 0 ? 2 : 3; sc = d.x; tc = d.y > 0 ? d.z : -d.z; }
        else { ma = az; face = d.z > 0 ? 4 : 5; sc = d.z > 0 ? d.x : -d.x; tc = -d.y; }
        float u = (sc / ma + 1f) * 0.5f, v = (tc / ma + 1f) * 0.5f;
        int px = Mathf.Clamp((int)(u * FaceSize), 0, FaceSize - 1);
        int py = Mathf.Clamp((int)(v * FaceSize), 0, FaceSize - 1);
        var src = faces[face];
        int i = (py * FaceSize + px) * 4;
        o[at] = src[i]; o[at + 1] = src[i + 1]; o[at + 2] = src[i + 2]; o[at + 3] = 0x3C00; // アルファは 1
    }

    /// <summary>R16G16B16A16_FLOAT の 2D の DDS（DX10 の拡張ヘッダー付き）を書く。</summary>
    private static void WriteDds(string path, ushort[] pixels)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x20534444u);            // "DDS "
        w.Write(124u);                   // dwSize
        w.Write(0x1u | 0x2u | 0x4u | 0x8u | 0x1000u); // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT
        w.Write((uint)Height);
        w.Write((uint)Width);
        w.Write((uint)(Width * 8));     // pitch
        w.Write(0u);                     // depth
        w.Write(1u);                     // mip の数
        for (int i = 0; i < 11; i++) w.Write(0u);
        w.Write(32u);                    // ddspf.dwSize
        w.Write(0x4u);                   // DDPF_FOURCC
        w.Write(0x30315844u);            // "DX10"
        for (int i = 0; i < 5; i++) w.Write(0u);
        w.Write(0x1000u);                // DDSCAPS_TEXTURE
        for (int i = 0; i < 4; i++) w.Write(0u);
        w.Write(10u);                    // DXGI_FORMAT_R16G16B16A16_FLOAT
        w.Write(3u);                     // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0u);                     // miscFlag
        w.Write(1u);                     // arraySize
        w.Write(0u);                     // miscFlags2
        var bytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        w.Write(bytes);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
