using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HeberekeBunnyGardenRemix.Remix;

/// <summary>Remix の資源の取っ手。中身の IntPtr を外に出さないための型。</summary>
internal readonly struct RemixMesh { internal readonly IntPtr Handle; internal RemixMesh(IntPtr h) => Handle = h; }
internal readonly struct RemixMaterial { internal readonly IntPtr Handle; internal RemixMaterial(IntPtr h) => Handle = h; }
/// <summary>素材の値。BlendType は混ぜ方（-1 で不透明、Remix の BlendType の番号）、AlphaCutoff は切り抜きの基準（-1 で無し）。</summary>
internal struct MaterialParams
{
    public Color Albedo;          // ガンマの値。テクスチャがあるときはテクスチャに掛ける色
    public float Roughness, Metallic, Opacity, AlphaCutoff;
    public int BlendType;
    public Color Emission;        // リニア、HDR
    public bool Translucent;      // ガラス（Remix の透過の素材）
    public bool Clamp;            // 絵を折り返さない（UV が 0〜1 に収まる升目の素材）
    public Color Transmittance;
    // 0 より大きければ、粗さの絵（R = 金属、G = なめらかさ）の R で金属を決め、金属の所の反射の色（F0）を R × この値（ガンマ、15 分のいくつ）にする
    public int MetalTint;
}

/// <summary>メッシュの 1 面。素材 1 つ分の三角形。Normals・Uvs は null でもよい。</summary>
internal struct MeshSurface
{
    public Vector3[] Positions, Normals;
    public Vector2[] Uvs;
    public int[] Indices;
    public RemixMaterial Material;
}

internal readonly struct RemixTexture { internal readonly IntPtr Handle; internal RemixTexture(IntPtr h) => Handle = h; }

/// <summary>スポットライトの円錐。</summary>
internal struct LightShaping
{
    public Vector3 Direction;
    public float ConeAngleDegrees, ConeSoftness, FocusExponent;
}

internal readonly struct RemixLight { internal readonly IntPtr Handle; internal RemixLight(IntPtr h) => Handle = h; }

internal sealed class RemixException : Exception
{
    public readonly int Code;
    public RemixException(string what, int code) : base($"{what} が失敗しました（Remix のエラーコード {code}）") => Code = code;
}

/// <summary>カメラの位置と向き。メインスレッドで Camera から取り、描画スレッドで Remix に渡す（描画スレッドでは Unity の物に触れない）。</summary>
internal struct CameraPose
{
    public Vector3 Position, Forward, Up, Right;
    public float FovY, Aspect, Near, Far;
    // Remix に渡す行列（列ベクトルの約束、Unity の Matrix4x4 と同じ並び）。左手系のビュー（+z が前）と、D3D の投影（z は 0〜1）
    public Matrix4x4 View, Projection;

    /// <summary>
    /// カメラの行列をそのまま写す。このゲームの追いかけるカメラは投影をずらしている（キャラクターを画面の下寄りに置く）ので、
    /// 視野角と縦横比だけ（CameraInfoParameterizedEXT）では、Remix の絵がゲームの絵からずれた（2026-11-10、スタート地点で縦に 4 分の 1 ほど）。
    /// </summary>
    public static CameraPose From(Camera camera)
    {
        var t = camera.transform;
        var flipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
        // Unity のビューは右手系（-z が前）。Remix は左手系（+z が前）なので z を返す。投影は、その左手系のビューから OpenGL のクリップへ写し、
        // z を -1〜1 から 0〜1 に直す（z_d3d = (z_gl + w) / 2）
        var proj = camera.projectionMatrix * flipZ;
        for (int c = 0; c < 4; c++) proj[2, c] = 0.5f * (proj[2, c] + proj[3, c]);
        return new CameraPose
        {
            Position = t.position, Forward = t.forward, Up = t.up, Right = t.right,
            FovY = camera.fieldOfView, Aspect = camera.aspect, Near = camera.nearClipPlane, Far = camera.farClipPlane,
            View = flipZ * camera.worldToCameraMatrix, Projection = proj,
        };
    }
}

/// <summary>
/// Remix Plus のランタイム（ゲームのフォルダの d3d9.dll）との境界。読み込み・関数表・構造体の写し・座標系の設定を全部ここに閉じ込める。
/// D3D11 のホストの道を使う: ゲームの D3D11 は同梱の d3d11.dll / dxgi.dll 経由で Remix の DXVK の上で動いており、
/// そのデバイスを <see cref="RegisterD3D11Device"/> で渡し、毎フレーム <see cref="RenderD3D11"/> で描かせる。Startup と Present は使わない。
/// <see cref="Load"/> 以外は、すべて Unity の描画スレッドから呼ぶこと。
/// 構造体は remix_c.h（Remix Plus API 1.0.0、MIT）を写したもの。写し間違いは <see cref="Load"/> の自己チェックで止める。
/// </summary>
internal sealed unsafe class RemixApi
{
    // REMIXAPI_VERSION_MAKE(1, 0, 0)
    private const ulong ApiVersion = 1UL << 48;

    // remixapi_Interface の並び（tools/remix-layout のプローブで確かめた番号）
    private const int SlotCreateMaterial = 1, SlotDestroyMaterial = 2, SlotCreateMesh = 3, SlotDestroyMesh = 5,
        SlotSetupCamera = 6, SlotDrawInstance = 7, SlotCreateLight = 8, SlotDestroyLight = 10, SlotDrawLightInstance = 11,
        SlotSetConfigVariable = 12, SlotDestroyTexture = 16;
    private const int InterfaceSize = 328;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Fn1(IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Fn2(IntPtr a, IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnRender(IntPtr destination, uint render);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnPtrCount(IntPtr items, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnPrepareTexture(IntPtr texture2D, uint dxgiFormat, uint maxDimension, out IntPtr token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnImportTexture(IntPtr token, ulong hash, uint encoding, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnPtrCountPtr(IntPtr items, uint count, IntPtr outItems);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDeviceFn(IntPtr self, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

    private readonly Fn1 _setupCamera, _drawInstance, _destroyMesh, _destroyMaterial, _destroyLight, _drawLight, _registerD3D11;
    private readonly Fn2 _createMesh, _createMaterial, _createLight, _setConfig;
    private readonly FnRender _renderD3D11;
    private readonly Fn1 _drawRetained, _destroyTexture, _releaseD3D11Texture;
    private readonly FnPrepareTexture _prepareD3D11Texture;
    private readonly FnImportTexture _importD3D11Texture;
    private readonly FnPtrCountPtr _createRetained;
    private readonly FnPtrCount _destroyRetained, _updateRetained;

    private RemixApi(IntPtr table, IntPtr module)
    {
        T Slot<T>(int i) where T : Delegate
        {
            var p = Marshal.ReadIntPtr(table, i * IntPtr.Size);
            if (p == IntPtr.Zero) throw new RemixException($"関数表の {i} 番が空（ランタイムの版が違う）", -1);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        T Export<T>(string name) where T : Delegate
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new RemixException($"{name} が無い（Remix Plus 2.0 以降のランタイムが要る）", -1);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        _setupCamera = Slot<Fn1>(SlotSetupCamera);
        _drawInstance = Slot<Fn1>(SlotDrawInstance);
        _destroyMesh = Slot<Fn1>(SlotDestroyMesh);
        _destroyMaterial = Slot<Fn1>(SlotDestroyMaterial);
        _destroyLight = Slot<Fn1>(SlotDestroyLight);
        _drawLight = Slot<Fn1>(SlotDrawLightInstance);
        _createMesh = Slot<Fn2>(SlotCreateMesh);
        _createMaterial = Slot<Fn2>(SlotCreateMaterial);
        _createLight = Slot<Fn2>(SlotCreateLight);
        _setConfig = Slot<Fn2>(SlotSetConfigVariable);
        _registerD3D11 = Export<Fn1>("remixapi_RegisterD3D11Device");
        _renderD3D11 = Export<FnRender>("remixapi_RenderD3D11");
        _createRetained = Export<FnPtrCountPtr>("remixapi_CreateRetainedInstances");
        _destroyRetained = Export<FnPtrCount>("remixapi_DestroyRetainedInstances");
        _updateRetained = Export<FnPtrCount>("remixapi_UpdateRetainedInstances");
        _drawRetained = Export<Fn1>("remixapi_DrawRetainedInstances");
        _destroyTexture = Slot<Fn1>(SlotDestroyTexture);
        _prepareD3D11Texture = Export<FnPrepareTexture>("remixapi_PrepareD3D11TextureEx");
        _importD3D11Texture = Export<FnImportTexture>("remixapi_ImportD3D11TextureEx");
        _releaseD3D11Texture = Export<Fn1>("remixapi_ReleaseD3D11Texture");
    }

    /// <summary>ゲームのフォルダの Remix Plus（d3d9.dll）から関数表を受け取る。メインスレッドから呼んでよい。</summary>
    public static RemixApi Load(string gameDir)
    {
        Layout.Check();

        var dll = Path.Combine(gameDir, "d3d9.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Remix Plus のランタイムがゲームのフォルダにありません", dll);
        // 転送用の d3d11.dll がすでに読み込んでいるはずなので、同じモジュールが返る。フルパスなのはシステムの d3d9.dll と取り違えないため
        var module = LoadLibraryW(dll);
        if (module == IntPtr.Zero) throw new RemixException($"{dll} の読み込み（Win32 エラー {Marshal.GetLastWin32Error()}）", -1);
        var init = GetProcAddress(module, "remixapi_InitializeLibrary");
        if (init == IntPtr.Zero) throw new RemixException("remixapi_InitializeLibrary が見つからない", -1);

        var info = new InitializeLibraryInfo { sType = StructType.InitializeLibraryInfo, version = ApiVersion };
        // 関数表は使い続けるので、プロセスが終わるまで解放しない
        var table = Marshal.AllocHGlobal(InterfaceSize);
        new Span<byte>((void*)table, InterfaceSize).Clear();
        Check(Marshal.GetDelegateForFunctionPointer<Fn2>(init)((IntPtr)(&info), table), "remixapi_InitializeLibrary");
        return new RemixApi(table, module);
    }

    /// <summary>
    /// ゲームの D3D11 デバイスを Remix に渡す。texture はそのデバイスのテクスチャ（Texture.GetNativeTexturePtr）で、そこからデバイスをたどる。
    /// 1 回だけ呼ぶ。座標系もここで Unity に合わせる。
    /// </summary>
    public void RegisterD3D11Device(IntPtr texture)
    {
        // ID3D11DeviceChild::GetDevice は vtable の 3 番。返るデバイスは参照が 1 つ増えているので、渡したあと手放す
        var vtbl = *(IntPtr**)texture;
        IntPtr device;
        Marshal.GetDelegateForFunctionPointer<GetDeviceFn>(vtbl[3])(texture, out device);
        try
        {
            var code = _registerD3D11(device);
            // INVALID_ARGUMENTS(2) は DXVK のデバイスではないとき。転送用の d3d11.dll / dxgi.dll が読まれていない
            if (code == 2) throw new RemixException("RegisterD3D11Device（ゲームが Remix の D3D11 で動いていない。d3d11.dll と dxgi.dll がゲームのフォルダにあるか確かめてください）", code);
            Check(code, "RegisterD3D11Device");
        }
        finally
        {
            var dv = *(IntPtr**)device;
            Marshal.GetDelegateForFunctionPointer<ReleaseFn>(dv[2])(device); // IUnknown::Release
        }
        // Unity は左手系で Y が上。Remix の既定は右手系で Y が上なので、手の向きだけ変える
        SetConfig("rtx.leftHandedCoordinateSystem", "True");
        SetConfig("rtx.zUp", "False");
    }

    public void SetConfig(string key, string value)
    {
        fixed (byte* k = Ascii(key)) fixed (byte* v = Ascii(value))
            Check(_setConfig((IntPtr)k, (IntPtr)v), $"SetConfigVariable({key})");
    }

    // Remix Plus では hash がそのまま取っ手になるので、0 は使えない
    private static void RequireHash(ulong hash, string what)
    {
        if (hash == 0) throw new ArgumentException($"{what} の hash に 0 は使えません", nameof(hash));
    }

    /// <summary>
    /// ゲームの D3D11 テクスチャ（Texture.GetNativeTexturePtr）を、GPU のまま Remix に取り込む。hash がそのテクスチャの識別子になり、
    /// 素材からは <see cref="CreateMaterial"/> の albedoTexture に同じ hash を渡して指す。
    /// dxgiFormat はビューの形式（sRGB かどうかもここで決まる）。maxDimension は取り込む大きさの上限（0 で元のまま）。
    /// </summary>
    /// encoding は絵の読み方（remix_c.h の REMIXAPI_TEXTURE_ENCODING_*）。その絵を使う素材の欄にだけ効く。
    public RemixTexture ImportD3D11Texture(IntPtr texture2D, uint dxgiFormat, ulong hash, uint maxDimension, uint encoding = EncodingNone)
    {
        RequireHash(hash, "ImportD3D11Texture");
        Check(_prepareD3D11Texture(texture2D, dxgiFormat, maxDimension, out var token), "PrepareD3D11TextureEx");
        try
        {
            Check(_importD3D11Texture(token, hash, encoding, out var handle), "ImportD3D11TextureEx");
            return new RemixTexture(handle);
        }
        finally
        {
            _releaseD3D11Texture(token);
        }
    }

    // REMIXAPI_TEXTURE_ENCODING_*
    public const uint EncodingNone = 0, EncodingNormalTangentXY = 1, EncodingSmoothnessInRed = 2, EncodingSmoothnessInGreen = 3;

    public void DestroyTexture(RemixTexture texture) => Check(_destroyTexture(texture.Handle), "DestroyTexture");

    /// <summary>
    /// 素材を作る。Remix Plus 2.0.0 は同じ hash の 2 回目を捨てるので、作り直すときは先に <see cref="DestroyMaterial"/> する（メッシュはそのまま新しい素材で描かれる）。テクスチャは <see cref="ImportD3D11Texture"/> の hash で指し、0 なら無し。
    /// </summary>
    public RemixMaterial CreateMaterial(ulong hash, in MaterialParams p, ulong albedoTexture = 0, ulong normalTexture = 0, ulong roughnessTexture = 0,
        ulong metallicTexture = 0, ulong emissiveTexture = 0)
    {
        RequireHash(hash, "CreateMaterial");
        // 取り込んだテクスチャは「0x<hash>」という名前で指す（Remix Plus の textureHashPathLookup）
        static string PathOf(ulong h) => h != 0 ? $"0x{h:x16}" : null;
        fixed (char* albedo = PathOf(albedoTexture))
        fixed (char* normal = PathOf(normalTexture))
        fixed (char* roughness = PathOf(roughnessTexture))
        fixed (char* metallic = PathOf(metallicTexture))
        fixed (char* emissive = PathOf(emissiveTexture))
            return CreateMaterial(hash, p, (IntPtr)albedo, (IntPtr)normal, (IntPtr)roughness, (IntPtr)metallic, (IntPtr)emissive);
    }

    /// <summary>テクスチャ無しの単色の素材（切り分け用の立方体など）。</summary>
    public RemixMaterial CreateMaterial(ulong hash, Color albedo, float roughness, float metallic)
        => CreateMaterial(hash, new MaterialParams { Albedo = albedo, Roughness = roughness, Metallic = metallic, AlphaCutoff = -1f, BlendType = -1, Opacity = 1f });

    private RemixMaterial CreateMaterial(ulong hash, in MaterialParams p, IntPtr albedoPath, IntPtr normalPath, IntPtr roughnessPath, IntPtr metallicPath, IntPtr emissivePath)
    {
        // Remix の発光は「強さ × 色（またはテクスチャ）」。強さが 0 より大きいときだけ光る。
        // 色の定数は sRGB として読まれる（opaque_surface_material_interaction.slangh の gammaToLinear）ので、線形の色を戻して渡す
        var emission = p.Emission;
        float emissiveIntensity = Mathf.Max(emission.r, Mathf.Max(emission.g, emission.b));
        var unit = emissiveIntensity > 0f ? new Color(emission.r / emissiveIntensity, emission.g / emissiveIntensity, emission.b / emissiveIntensity).gamma : default;
        var emissiveColor = new Float3D(unit.r, unit.g, unit.b);
        var albedo = p.Albedo;
        var info = new MaterialInfo
        {
            sType = StructType.MaterialInfo,
            hash = hash,
            albedoTexture = albedoPath,
            normalTexture = normalPath,
            emissiveTexture = emissivePath,
            emissiveIntensity = emissiveIntensity,
            emissiveColorConstant = emissiveColor,
            spriteSheetRow = 1,
            spriteSheetCol = 1,
            filterMode = 1,
            // lss::Mdl::WrapMode: 0 = Clamp、1 = Repeat
            wrapModeU = (byte)(p.Clamp ? 0 : 1),
            wrapModeV = (byte)(p.Clamp ? 0 : 1),
        };
        if (p.Translucent)
        {
            // ガラス: 屈折する薄い板（thinWall）。透過の色は 1 m あたり
            var translucent = new MaterialInfoTranslucentEXT
            {
                sType = StructType.MaterialInfoTranslucentExt,
                refractiveIndex = 1.5f,
                transmittanceColor = new Float3D(p.Transmittance.r, p.Transmittance.g, p.Transmittance.b),
                transmittanceMeasurementDistance = 1f,
                thinWallThickness_hasvalue = 1,
                thinWallThickness_value = 0.005f,
            };
            info.pNext = (IntPtr)(&translucent);
            IntPtr th;
            Check(_createMaterial((IntPtr)(&info), (IntPtr)(&th)), "CreateMaterial(透過)");
            return new RemixMaterial(th);
        }
        // テクスチャに掛ける色（MaterialInfoOpaqueAlbedoTintEXT）。テクスチャが無いときは Remix が使わない
        var tint = new MaterialInfoOpaqueAlbedoTintEXT { sType = StructType.MaterialInfoOpaqueAlbedoTintExt, albedoTint = new Float3D(albedo.r, albedo.g, albedo.b) };
        bool blend = p.BlendType >= 0;
        // 金属の地図から金属を決める（MaterialInfoOpaqueSpecGlossEXT）: 金属 = R × tint を 0〜tint の坂に通した値（= R）、金属の所の F0 = R × tint
        float metalTint = p.MetalTint / 15f;
        var specGloss = new MaterialInfoOpaqueSpecGlossEXT
        {
            sType = StructType.MaterialInfoOpaqueSpecGlossExt,
            pNext = albedoPath != IntPtr.Zero ? (IntPtr)(&tint) : IntPtr.Zero,
            roughnessEncoding = EncodingSmoothnessInGreen,
            metalStrength = 1f, specularMetalLow = 0f, specularMetalHigh = metalTint, metalAlbedoLuminanceMax = 1f,
            specularTint = new Float3D(metalTint, metalTint, metalTint),
        };
        bool useSpecGloss = p.MetalTint > 0 && roughnessPath != IntPtr.Zero;
        var opaque = new MaterialInfoOpaqueEXT
        {
            sType = StructType.MaterialInfoOpaqueExt,
            pNext = useSpecGloss ? (IntPtr)(&specGloss) : albedoPath != IntPtr.Zero ? (IntPtr)(&tint) : IntPtr.Zero,
            roughnessTexture = roughnessPath,
            metallicTexture = metallicPath,
            albedoConstant = new Float3D(albedo.r, albedo.g, albedo.b),
            opacityConstant = blend ? p.Opacity : 1f,
            roughnessConstant = p.Roughness,
            metallicConstant = p.Metallic,
            thinFilmThickness_value = 200f,
            // 切り抜き: アルファ >= 基準で残す（AlphaTestType::kGreaterOrEqual = 6）。混ぜる物と不透明は kAlways = 7。
            // 描画の状態（D3D9 の道の物）は使わない: この道には描画の呼び出しが無い
            useDrawCallAlphaState = blend || p.AlphaCutoff >= 0f ? 0u : 1u,
            alphaTestType = p.AlphaCutoff >= 0f ? 6 : 7,
            alphaReferenceValue = p.AlphaCutoff >= 0f ? (byte)Mathf.Clamp(Mathf.RoundToInt(p.AlphaCutoff * 255f), 1, 255) : (byte)0,
            blendType_hasvalue = blend ? 1u : 0u,
            blendType_value = blend ? p.BlendType : 0,
        };
        info.pNext = (IntPtr)(&opaque);
        IntPtr handle;
        Check(_createMaterial((IntPtr)(&info), (IntPtr)(&handle)), "CreateMaterial");
        return new RemixMaterial(handle);
    }

    /// <summary>三角形のメッシュを 1 面で作る。hash はこのメッシュの識別子（0 以外）。normals・uvs は null でもよい。座標はメッシュのローカル空間のまま。</summary>
    public RemixMesh CreateMesh(ulong hash, Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices, RemixMaterial material)
        => CreateMesh(hash, new[] { new MeshSurface { Positions = positions, Normals = normals, Uvs = uvs, Indices = indices, Material = material } });

    /// <summary>面（素材ごとの三角形の組）を並べてメッシュを作る。hash はこのメッシュの識別子（0 以外）。</summary>
    public RemixMesh CreateMesh(ulong hash, MeshSurface[] surfaces) => CreateMesh(hash, surfaces, refresh: false);

    /// <summary>
    /// 同じ hash ですでに作ったメッシュの形だけを差し替える（つながりと面の数は同じであること）。キャラクターのように毎フレーム形が変わる物に使う。
    /// 新しい hash で作り直すと、Remix には別の物に見えて、動きのベクトルとデノイザーの履歴が切れる（remix_c.h の MeshInfoRefreshGeometryEXT）。
    /// </summary>
    public RemixMesh RefreshMesh(ulong hash, MeshSurface[] surfaces) => CreateMesh(hash, surfaces, refresh: true);

    private RemixMesh CreateMesh(ulong hash, MeshSurface[] surfaces, bool refresh)
    {
        RequireHash(hash, "CreateMesh");
        var pins = new GCHandle[surfaces.Length * 2];
        try
        {
            var infos = new MeshInfoSurfaceTriangles[surfaces.Length];
            for (int s = 0; s < surfaces.Length; s++)
            {
                var vertices = ToVertices(surfaces[s]);
                pins[s * 2] = GCHandle.Alloc(vertices, GCHandleType.Pinned);
                pins[s * 2 + 1] = GCHandle.Alloc(surfaces[s].Indices, GCHandleType.Pinned);
                infos[s] = new MeshInfoSurfaceTriangles
                {
                    vertices_values = pins[s * 2].AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = pins[s * 2 + 1].AddrOfPinnedObject(), // int と uint32 はビットが同じ
                    indices_count = (ulong)surfaces[s].Indices.Length,
                    material = surfaces[s].Material.Handle,
                };
            }
            fixed (MeshInfoSurfaceTriangles* ps = infos)
            {
                var refreshExt = new MeshInfoRefreshGeometryEXT { sType = StructType.MeshInfoRefreshGeometryExt };
                var info = new MeshInfo
                {
                    sType = StructType.MeshInfo, pNext = refresh ? (IntPtr)(&refreshExt) : IntPtr.Zero,
                    hash = hash, surfaces_values = (IntPtr)ps, surfaces_count = (uint)infos.Length,
                };
                IntPtr handle;
                Check(_createMesh((IntPtr)(&info), (IntPtr)(&handle)), "CreateMesh");
                return new RemixMesh(handle);
            }
        }
        finally
        {
            foreach (var pin in pins) if (pin.IsAllocated) pin.Free();
        }
    }

    private static HardcodedVertex[] ToVertices(in MeshSurface surface)
    {
        var positions = surface.Positions;
        var normals = surface.Normals != null && surface.Normals.Length == positions.Length ? surface.Normals : null;
        var uvs = surface.Uvs != null && surface.Uvs.Length == positions.Length ? surface.Uvs : null;
        var vertices = new HardcodedVertex[positions.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            ref var v = ref vertices[i];
            var p = positions[i];
            v.px = p.x; v.py = p.y; v.pz = p.z;
            if (normals != null) { var n = normals[i]; v.nx = n.x; v.ny = n.y; v.nz = n.z; }
            // UV はそのまま。Unity は D3D11 のテクスチャを、同じ UV で同じ画素を引くように上下を入れて置いているので、
            // ゲームのテクスチャを GPU のまま Remix に渡すなら反転しない
            if (uvs != null) { v.u = uvs[i].x; v.v = uvs[i].y; }
            v.color = 0xFFFFFFFF;
        }
        return vertices;
    }

    /// <summary>
    /// 置きっぱなしの実体を、同じメッシュで transforms の数だけ作る。毎フレーム <see cref="DrawRetainedInstances"/> 1 回で全部描かれる。
    /// 返す取っ手は、消すときに <see cref="DestroyRetainedInstances"/> へ渡す。
    /// </summary>
    /// <summary>
    /// 画面には映さず、影（と一部の映り込み）にだけ出す実体の印（REMIXAPI_INSTANCE_CATEGORY_BIT_THIRD_PERSON_PLAYER_MODEL）。
    /// Remix は rtx.playerModel.enableInPrimarySpace が false（既定）のとき、この印の物を最初の視線から外す。ゲーム自身が描き直す物に付ける。
    /// </summary>
    public const uint CategoryShadowOnly = 1u << 18;

    public ulong[] CreateRetainedInstances(RemixMesh mesh, Matrix4x4[] localToWorld, bool doubleSided, uint category = 0)
    {
        var infos = new InstanceInfo[localToWorld.Length];
        for (int k = 0; k < infos.Length; k++)
        {
            infos[k] = new InstanceInfo { sType = StructType.InstanceInfo, categoryFlags = category, mesh = mesh.Handle, doubleSided = doubleSided ? 1u : 0u };
            SetTransform(ref infos[k], localToWorld[k]);
        }
        var handles = new ulong[infos.Length];
        fixed (InstanceInfo* pi = infos) fixed (ulong* ph = handles)
            Check(_createRetained((IntPtr)pi, (uint)infos.Length, (IntPtr)ph), "CreateRetainedInstances");
        return handles;
    }

    public void DestroyRetainedInstances(ulong[] handles)
    {
        if (handles.Length == 0) return;
        fixed (ulong* ph = handles) Check(_destroyRetained((IntPtr)ph, (uint)handles.Length), "DestroyRetainedInstances");
    }

    /// <summary>置きっぱなしの実体の場所と見え方を、まとめて変える（動いた物だけ渡す）。</summary>
    public void UpdateRetainedInstances(ulong[] handles, Matrix4x4[] localToWorld, bool[] visible)
    {
        if (handles.Length == 0) return;
        var updates = new RetainedInstanceUpdate[handles.Length];
        for (int k = 0; k < updates.Length; k++)
        {
            updates[k] = new RetainedInstanceUpdate { handle = handles[k], flags = 0x1 | 0x2 /* TRANSFORM | VISIBILITY */, visible = visible[k] ? 1u : 0u };
            var m = localToWorld[k];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                    updates[k].transform[r * 4 + c] = m[r, c];
        }
        fixed (RetainedInstanceUpdate* pu = updates) Check(_updateRetained((IntPtr)pu, (uint)updates.Length), "UpdateRetainedInstances");
    }

    /// <summary>置きっぱなしの実体を全部、このフレームに描く。SetupCamera のあと、RenderD3D11 の前に 1 回。</summary>
    public void DrawRetainedInstances() => Check(_drawRetained(IntPtr.Zero), "DrawRetainedInstances");

    private static void SetTransform(ref InstanceInfo info, Matrix4x4 m)
    {
        // remixapi_Transform は 3x4 の行優先。Unity の Matrix4x4 の上 3 行をそのまま並べる
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                info.transform[r * 4 + c] = m[r, c];
    }

    /// <summary>太陽のような平行光源。hash はこの光源の識別子（0 以外）。direction は光の進む向き（ワールド座標）。</summary>
    public RemixLight CreateDistantLight(ulong hash, Vector3 direction, Color radiance, float angularDiameterDegrees)
    {
        RequireHash(hash, "CreateLight");
        var distant = new LightInfoDistantEXT
        {
            sType = StructType.LightInfoDistantExt,
            direction = direction.normalized,
            angularDiameterDegrees = angularDiameterDegrees,
            volumetricRadianceScale = 1f,
        };
        var info = new LightInfo
        {
            sType = StructType.LightInfo,
            pNext = (IntPtr)(&distant),
            hash = hash,
            radiance = new Float3D(radiance.r, radiance.g, radiance.b),
        };
        IntPtr handle;
        Check(_createLight((IntPtr)(&info), (IntPtr)(&handle)), "CreateLight");
        return new RemixLight(handle);
    }

    /// <summary>点光源。shaping があればスポットライト（direction に向けて coneAngleDegrees の円錐）。radiance は面の放射輝度。</summary>
    public RemixLight CreateSphereLight(ulong hash, Vector3 position, float radius, Color radiance, LightShaping? shaping)
    {
        RequireHash(hash, "CreateLight");
        var sphere = new LightInfoSphereEXT
        {
            sType = StructType.LightInfoSphereExt,
            position = position,
            radius = radius,
            volumetricRadianceScale = 1f,
        };
        if (shaping is { } sh) { sphere.shaping_hasvalue = 1; sphere.shaping_value = ToNative(sh); }
        return CreateLight(hash, radiance, &sphere);
    }

    /// <summary>四角い面光源。x・y は面の向きの軸、direction は光の出る向き。</summary>
    public RemixLight CreateRectLight(ulong hash, Vector3 position, Vector3 xAxis, float xSize, Vector3 yAxis, float ySize, Vector3 direction, Color radiance)
    {
        RequireHash(hash, "CreateLight");
        var rect = new LightInfoRectEXT
        {
            sType = StructType.LightInfoRectExt,
            position = position, xAxis = xAxis.normalized, xSize = xSize, yAxis = yAxis.normalized, ySize = ySize,
            direction = direction.normalized, volumetricRadianceScale = 1f,
        };
        return CreateLight(hash, radiance, &rect);
    }

    /// <summary>
    /// ドームライト（空）。texturePath は緯度経度の DDS のフルパス（Remix は DDS の絵だけ読む）。domeToWorld は 3x4 の行優先で、ドームの空間（z が上）からワールドへ。
    /// 光の当たらない向き（ミス）の空の色と、空からの光の両方になる。
    /// </summary>
    public RemixLight CreateDomeLight(ulong hash, Color radiance, float[] domeToWorld, string texturePath)
    {
        RequireHash(hash, "CreateLight");
        var dome = new LightInfoDomeEXT { sType = StructType.LightInfoDomeExt };
        for (int k = 0; k < 12; k++) dome.transform[k] = domeToWorld[k];
        fixed (char* path = texturePath)
        {
            dome.colorTexture = (IntPtr)path;
            return CreateLight(hash, radiance, &dome);
        }
    }

    private RemixLight CreateLight(ulong hash, Color radiance, void* ext)
    {
        var info = new LightInfo { sType = StructType.LightInfo, pNext = (IntPtr)ext, hash = hash, radiance = new Float3D(radiance.r, radiance.g, radiance.b) };
        IntPtr handle;
        Check(_createLight((IntPtr)(&info), (IntPtr)(&handle)), "CreateLight");
        return new RemixLight(handle);
    }

    public void DestroyMesh(RemixMesh mesh) => Check(_destroyMesh(mesh.Handle), "DestroyMesh");
    public void DestroyMaterial(RemixMaterial material) => Check(_destroyMaterial(material.Handle), "DestroyMaterial");
    public void DestroyLight(RemixLight light) => Check(_destroyLight(light.Handle), "DestroyLight");

    /// <summary>このフレームのカメラ。Unity の Transform の向きをそのまま渡す（行列の取り違えを避ける）。</summary>
    public void SetupCamera(in CameraPose pose)
    {
        // remixapi_CameraInfo の view / projection は、列ベクトルの行列の列が順に並ぶ（rtx_remix_api.cpp の Matrix4 { info.view }）。Unity の Matrix4x4[i] と同じ並び
        var info = new CameraInfo { sType = StructType.CameraInfo, type = 0 /* WORLD */ };
        for (int i = 0; i < 16; i++)
        {
            info.view[i] = pose.View[i];
            info.projection[i] = pose.Projection[i];
        }
        Check(_setupCamera((IntPtr)(&info)), "SetupCamera");
    }

    /// <summary>このフレームだけ描く実体。</summary>
    public void DrawInstance(RemixMesh mesh, Matrix4x4 localToWorld, bool doubleSided, uint category = 0)
    {
        var info = new InstanceInfo { sType = StructType.InstanceInfo, categoryFlags = category, mesh = mesh.Handle, doubleSided = doubleSided ? 1u : 0u };
        SetTransform(ref info, localToWorld);
        Check(_drawInstance((IntPtr)(&info)), "DrawInstance");
    }

    public void DrawLight(RemixLight light) => Check(_drawLight(light.Handle), "DrawLightInstance");

    /// <summary>
    /// このフレームの絵を destination（登録したデバイスの、MSAA 無しのレンダーテクスチャ）に描かせる。
    /// D3D11 のコマンドの並びに積まれるので、このあとのコピーは描き終えた絵を読む。フレームはゲームの Present で締まる。
    /// </summary>
    /// <summary>render が false なら、描かずにフレームの締め先を知らせるだけ。</summary>
    public void RenderD3D11(IntPtr destination, bool render = true) => Check(_renderD3D11(destination, render ? 1u : 0u), "RenderD3D11");

    /// <summary>
    /// このフレームを締める先として、スワップチェーンのバックバッファを Remix に教える（描かない）。
    /// Remix は Present のとき「最後に RenderD3D11 で渡された先」がバックバッファのときだけフレームを締め、フレームの番号を進める。
    /// 締めないと次のフレームからは描かれない（2026-11-09 に、最初の 1 枚のまま止まっていた）。
    /// </summary>
    public void EndFrameOn(IntPtr backbuffer) => Check(_renderD3D11(backbuffer, 0), "RenderD3D11(締め)");

    private static void Check(int code, string what)
    {
        if (code != 0) throw new RemixException(what, code);
    }

    private static byte[] Ascii(string s)
    {
        var bytes = new byte[s.Length + 1];
        for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
        return bytes;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // ───────────── remix_c.h の写し（使う分だけ）。大きさと位置は Layout.Check で本物と照らし合わせる ─────────────

    private enum StructType : uint
    {
        InitializeLibraryInfo = 1, MaterialInfo = 2, MaterialInfoOpaqueExt = 5, LightInfo = 6, LightInfoDistantExt = 7,
        MaterialInfoTranslucentExt = 4, LightInfoRectExt = 10, LightInfoSphereExt = 11, LightInfoDomeExt = 20, MeshInfoRefreshGeometryExt = 29, MaterialInfoOpaqueSpecGlossExt = 30, MaterialInfoOpaqueAlbedoTintExt = 31,
        MeshInfo = 12, InstanceInfo = 13, CameraInfo = 16, CameraInfoParameterizedExt = 17,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float3D
    {
        public float x, y, z;
        public Float3D(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static implicit operator Float3D(Vector3 v) => new(v.x, v.y, v.z);
    }

    [StructLayout(LayoutKind.Sequential)] private struct InitializeLibraryInfo { public StructType sType; public IntPtr pNext; public ulong version; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardcodedVertex
    {
        public float px, py, pz, nx, ny, nz, u, v;
        public uint color;
        public uint pad0, pad1, pad2, pad3, pad4, pad5, pad6;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshInfoSkinning
    {
        public uint bonesPerVertex; public IntPtr blendWeights_values; public uint blendWeights_count;
        public IntPtr blendIndices_values; public uint blendIndices_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshInfoSurfaceTriangles
    {
        public IntPtr vertices_values; public ulong vertices_count; public IntPtr indices_values; public ulong indices_count;
        public uint skinning_hasvalue; public MeshInfoSkinning skinning_value;
        public IntPtr material;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshInfo { public StructType sType; public IntPtr pNext; public ulong hash; public IntPtr surfaces_values; public uint surfaces_count; }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceInfo
    {
        public StructType sType; public IntPtr pNext; public uint categoryFlags; public IntPtr mesh;
        public fixed float transform[12]; public uint doubleSided;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialInfo
    {
        public StructType sType; public IntPtr pNext; public ulong hash;
        public IntPtr albedoTexture, normalTexture, tangentTexture, emissiveTexture;
        public float emissiveIntensity; public Float3D emissiveColorConstant;
        public byte spriteSheetRow, spriteSheetCol, spriteSheetFps, filterMode, wrapModeU, wrapModeV;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialInfoOpaqueEXT
    {
        public StructType sType; public IntPtr pNext; public IntPtr roughnessTexture, metallicTexture;
        public float anisotropy; public Float3D albedoConstant; public float opacityConstant, roughnessConstant, metallicConstant;
        public uint thinFilmThickness_hasvalue; public float thinFilmThickness_value; public uint alphaIsThinFilmThickness;
        public IntPtr heightTexture; public float displaceIn; public uint useDrawCallAlphaState, blendType_hasvalue;
        public int blendType_value; public uint invertedBlend; public int alphaTestType; public byte alphaReferenceValue;
        public float displaceOut; public uint enableDlssControlMask;
        public float dlssControlMaskIntensity, dlssControlMaskToneStrength, dlssControlMaskStructuralStrength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraInfo { public StructType sType; public IntPtr pNext; public uint type; public fixed float view[16]; public fixed float projection[16]; }


    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfo
    {
        public StructType sType; public IntPtr pNext; public ulong hash; public Float3D radiance;
        public uint isDynamic, ignoreViewModel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfoDistantEXT
    {
        public StructType sType; public IntPtr pNext; public Float3D direction;
        public float angularDiameterDegrees, volumetricRadianceScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfoDomeEXT { public StructType sType; public IntPtr pNext; public fixed float transform[12]; public IntPtr colorTexture; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialInfoTranslucentEXT
    {
        public StructType sType; public IntPtr pNext; public IntPtr transmittanceTexture; public float refractiveIndex;
        public Float3D transmittanceColor; public float transmittanceMeasurementDistance;
        public uint thinWallThickness_hasvalue; public float thinWallThickness_value; public uint useDiffuseLayer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetainedInstanceUpdate { public ulong handle; public fixed float transform[12]; public uint flags, visible; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshInfoRefreshGeometryEXT { public StructType sType; public IntPtr pNext; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialInfoOpaqueAlbedoTintEXT { public StructType sType; public IntPtr pNext; public Float3D albedoTint; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialInfoOpaqueSpecGlossEXT
    {
        public StructType sType; public IntPtr pNext; public uint roughnessEncoding;
        public float metalStrength, specularMetalLow, specularMetalHigh, metalAlbedoLuminanceMax;
        public Float3D specularTint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfoLightShaping { public Float3D direction; public float coneAngleDegrees, coneSoftness, focusExponent; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfoSphereEXT
    {
        public StructType sType; public IntPtr pNext; public Float3D position; public float radius;
        public uint shaping_hasvalue; public LightInfoLightShaping shaping_value; public float volumetricRadianceScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightInfoRectEXT
    {
        public StructType sType; public IntPtr pNext; public Float3D position, xAxis; public float xSize;
        public Float3D yAxis; public float ySize; public Float3D direction;
        public uint shaping_hasvalue; public LightInfoLightShaping shaping_value; public float volumetricRadianceScale;
    }

    private static LightInfoLightShaping ToNative(in LightShaping s) => new()
    {
        direction = s.Direction.normalized, coneAngleDegrees = s.ConeAngleDegrees, coneSoftness = s.ConeSoftness, focusExponent = s.FocusExponent,
    };

    /// <summary>写した構造体の大きさとフィールドの位置を、本物のヘッダーで出した値（tools/remix-layout）と照らし合わせる。</summary>
    internal static class Layout
    {
        public static void Check()
        {
            Size<HardcodedVertex>(64);
            Size<MeshInfoSkinning>(40);
            Size<MeshInfoSurfaceTriangles>(88);
            Offset<MeshInfoSurfaceTriangles>(nameof(MeshInfoSurfaceTriangles.skinning_value), 40);
            Offset<MeshInfoSurfaceTriangles>(nameof(MeshInfoSurfaceTriangles.material), 80);
            Size<MeshInfo>(40);
            Size<InstanceInfo>(88);
            Offset<InstanceInfo>(nameof(InstanceInfo.transform), 32);
            Offset<InstanceInfo>(nameof(InstanceInfo.doubleSided), 80);
            Size<MaterialInfo>(80);
            Offset<MaterialInfo>(nameof(MaterialInfo.emissiveColorConstant), 60);
            Offset<MaterialInfo>(nameof(MaterialInfo.spriteSheetRow), 72);
            Size<MaterialInfoOpaqueEXT>(128);
            Offset<MaterialInfoOpaqueEXT>(nameof(MaterialInfoOpaqueEXT.albedoConstant), 36);
            Offset<MaterialInfoOpaqueEXT>(nameof(MaterialInfoOpaqueEXT.heightTexture), 72);
            Offset<MaterialInfoOpaqueEXT>(nameof(MaterialInfoOpaqueEXT.alphaReferenceValue), 104);
            Offset<MaterialInfoOpaqueEXT>(nameof(MaterialInfoOpaqueEXT.displaceOut), 108);
            Size<CameraInfo>(152);
            Offset<CameraInfo>(nameof(CameraInfo.view), 20);
            Size<LightInfo>(48);
            Offset<LightInfo>(nameof(LightInfo.radiance), 24);
            Offset<LightInfo>(nameof(LightInfo.isDynamic), 36);
            Size<LightInfoDistantEXT>(40);
            Offset<LightInfoDistantEXT>(nameof(LightInfoDistantEXT.angularDiameterDegrees), 28);
            Size<MaterialInfoOpaqueAlbedoTintEXT>(32);
            Size<MaterialInfoOpaqueSpecGlossEXT>(48);
            Offset<MaterialInfoOpaqueSpecGlossEXT>(nameof(MaterialInfoOpaqueSpecGlossEXT.specularTint), 36);
            Size<MeshInfoRefreshGeometryEXT>(16);
            Size<RetainedInstanceUpdate>(64);
            Size<MaterialInfoTranslucentEXT>(56);
            Size<LightInfoDomeEXT>(72);
            Offset<LightInfoDomeEXT>(nameof(LightInfoDomeEXT.colorTexture), 64);
            Offset<MaterialInfoTranslucentEXT>(nameof(MaterialInfoTranslucentEXT.transmittanceColor), 28);
            Offset<MaterialInfoTranslucentEXT>(nameof(MaterialInfoTranslucentEXT.useDiffuseLayer), 52);
            Size<LightInfoLightShaping>(24);
            Size<LightInfoSphereEXT>(64);
            Offset<LightInfoSphereEXT>(nameof(LightInfoSphereEXT.radius), 28);
            Offset<LightInfoSphereEXT>(nameof(LightInfoSphereEXT.shaping_value), 36);
            Offset<LightInfoSphereEXT>(nameof(LightInfoSphereEXT.volumetricRadianceScale), 60);
            Size<LightInfoRectEXT>(104);
            Offset<LightInfoRectEXT>(nameof(LightInfoRectEXT.xSize), 40);
            Offset<LightInfoRectEXT>(nameof(LightInfoRectEXT.ySize), 56);
            Offset<LightInfoRectEXT>(nameof(LightInfoRectEXT.direction), 60);
            Offset<LightInfoRectEXT>(nameof(LightInfoRectEXT.shaping_value), 76);
            Offset<LightInfoRectEXT>(nameof(LightInfoRectEXT.volumetricRadianceScale), 100);
            Size<InitializeLibraryInfo>(24);
            if (IntPtr.Size * (SlotSetConfigVariable + 1) > InterfaceSize) throw new InvalidOperationException("関数表の番号が大きさを超えている");
        }

        private static void Size<T>(int expected) where T : unmanaged
        {
            if (sizeof(T) != expected) throw new InvalidOperationException($"{typeof(T).Name} の大きさが {sizeof(T)}（正しくは {expected}）");
        }

        private static void Offset<T>(string field, int expected)
        {
            var actual = Marshal.OffsetOf<T>(field).ToInt32();
            if (actual != expected) throw new InvalidOperationException($"{typeof(T).Name}.{field} の位置が {actual}（正しくは {expected}）");
        }
    }
}
