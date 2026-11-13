using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace HeberekeBunnyGardenRemix.Remix;

/// <summary>
/// 描画スレッドで、Remix が描いた絵（8 ビットの sRGB、露出 -2 段で 0〜1 に収めてある）を、ゲームと同じ尺度の HDR の絵に戻し、ゲームの霧を混ぜる。
/// D3D11 を直接呼ぶ（Unity に入っているシェーダーでは、この計算ができないため）。全画面の三角形 1 枚:
/// 出力 = 霧の色 × (1 - f) + Remix の絵（線形）× 倍率 × f。霧の濃さ（1 - f）は、Unity が先に別の絵に描いておく（<see cref="FogMaterials"/>）。
/// Remix の絵は sRGB のビューで読んで線形にする。出力の HDR の絵は、URP のパスがカメラの色に写し、その上にゲーム自身がキャラクターや半透明を描く。
/// Unity の描画の状態は、プラグインの合図のあとに Unity が張り直す（Remix の RenderD3D11 も同じ所で状態を変えている）。
/// </summary>
internal sealed unsafe class RemixResolve
{
    // ID3D11Device
    private const int CreateShaderResourceView = 7, CreateRenderTargetView = 9, CreateVertexShader = 12, CreatePixelShader = 15, GetImmediateContext = 40;
    // ID3D11DeviceContext
    private const int PSSetShaderResources = 8, PSSetShader = 9, VSSetShader = 11, Draw = 13, IASetInputLayout = 17, GSSetShader = 23, IASetPrimitiveTopology = 24,
        OMSetRenderTargets = 33, OMSetBlendState = 35, OMSetDepthStencilState = 36, RSSetState = 43, RSSetViewports = 44, HSSetShader = 60, DSSetShader = 64;
    private const uint TypelessRgba8 = 27, UnormRgba8 = 28, SrgbRgba8 = 29, TypelessRgba16 = 9, FloatRgba16 = 10;

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, IntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateViewFn(IntPtr self, IntPtr resource, void* desc, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShaderFn(IntPtr self, IntPtr code, IntPtr size, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetPtrFn(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPtrFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FlushFn(IntPtr self);
    private const int ContextFlush = 111;

    /// <summary>
    /// ID3D11DeviceContext::Flush。描画スレッドで、Remix に描かせる直前に呼ぶ。
    /// このランタイムの D3D11（DXVK）は、初期データの無い作りたてのバッファを「初期化用の別の列」でゼロ埋めし（d3d11_initializer.cpp: InitDeviceLocalBuffer）、
    /// その列は D3D11 の Flush のときにだけ、本体の列より先に送られる（FlushInitContext）。Remix が描くときの送り出し（D3D11ImmediateContext::RenderRemix の
    /// flushCommandList）はそれを通らないので、このフレームに作られて書かれたバッファが、あとから届くゼロ埋めで消されることがある。
    /// 当たるのは、毎フレーム作り直される物: 頬の赤み（mesh_face_blush_high。ゲームはアニメで毎フレーム無効にし、LateUpdate で有効に戻すので、
    /// スキニングの結果のバッファが毎フレーム新しい）。画面に実際に出たフレームを撮ると、ADV で 16 フレーム中 11 フレーム、頬の赤みが丸ごと消えていた
    /// （2026-11-11。ゲーム内のスクリーンショットには写るので、画面を直接撮って分かった: tools/deskburst.py、blushcount.py）。
    /// 切り分け: パスを挟むだけ・合図だけなら消えない／Remix の描画（RenderD3D11）を通すと消える／Unity の状態の取り直し、描き先の付け直し、
    /// ステンシルの入れ直し、深さの写し戻し、時間を止める、では直らない。先に Flush しておくと、ゼロ埋めが先に送られる。
    /// shortcut: Flush を入れた版は、実機でまだ確かめていない（Steam のクラウド同期の確認で起動できなかった）。効かなければ mod.passtest = 8 と比べる
    /// </summary>
    public void Flush(IntPtr anyTexture)
    {
        if (_context == IntPtr.Zero)
        {
            var device = D3D11Native.Device(anyTexture);
            Call<GetPtrFn>(device, GetImmediateContext)(device, out _context);
            D3D11Native.ReleaseRef(device);
        }
        Call<FlushFn>(_context, ContextFlush)(_context);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewsFn(IntPtr self, uint start, uint count, IntPtr* views);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr classes, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawFn(IntPtr self, uint count, uint start);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetPtrFn(IntPtr self, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetUIntFn(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTargetsFn(IntPtr self, uint count, IntPtr* views, IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetBlendFn(IntPtr self, IntPtr state, float* factor, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetDepthFn(IntPtr self, IntPtr state, uint stencilRef);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewportsFn(IntPtr self, uint count, float* viewports);

    private const string Source = @"
Texture2D<float4> Remix : register(t0);
Texture2D<float4> Factor : register(t1);
float4 VS(uint id : SV_VertexID) : SV_Position { return float4(id == 2 ? 3.0 : -1.0, id == 1 ? 3.0 : -1.0, 0.0, 1.0); }
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float3 c = Remix.Load(int3(int(pos.x), int(pos.y), 0)).rgb * SCALE;
#if FOG
    uint w, h; Factor.GetDimensions(w, h);
    // 霧の濃さの絵は Unity の向き（下の行が先）、Remix の絵は上の行が先
    float inv = Factor.Load(int3(int(pos.x), int(h) - 1 - int(pos.y), 0)).r;
    c = c * (1.0 - inv) + FOG_COLOR * inv;
#endif
    return float4(c, 1.0);
}";

    private IntPtr _device, _context, _vs, _ps, _remixSrv, _factorSrv, _rtv, _remixOf, _factorOf, _rtvOf;
    private string _psKey;
    private bool _broken;

    /// <summary>
    /// remix（Remix の描き先）を、scale 倍の線形の HDR にして hdr に書く。factor（1 - f の絵）が 0 でなければ、霧の色 fogColor（線形、ゲームの尺度）を混ぜる。
    /// </summary>
    public void Apply(IntPtr remix, IntPtr hdr, IntPtr factor, Color fogColor, float scale, Action<string> error)
    {
        if (_broken || remix == IntPtr.Zero || hdr == IntPtr.Zero) return;
        try
        {
            if (_device == IntPtr.Zero)
            {
                _device = D3D11Native.Device(remix);
                Call<GetPtrFn>(_device, GetImmediateContext)(_device, out _context);
                _vs = Shader("VS", "vs_5_0", Defines(false, default, 1f), isPixel: false);
            }
            bool fog = factor != IntPtr.Zero;
            string key = Defines(fog, fogColor, scale);
            if (key != _psKey)
            {
                if (_ps != IntPtr.Zero) D3D11Native.ReleaseRef(_ps);
                _ps = Shader("PS", "ps_5_0", key, isPixel: true);
                _psKey = key;
            }
            if (_remixOf != remix) { Release(ref _remixSrv); _remixSrv = View(remix, CreateShaderResourceView, ViewKind.SrgbRead); _remixOf = remix; }
            if (fog && _factorOf != factor) { Release(ref _factorSrv); _factorSrv = View(factor, CreateShaderResourceView, ViewKind.UnormRead); _factorOf = factor; }
            if (_rtvOf != hdr) { Release(ref _rtv); _rtv = View(hdr, CreateRenderTargetView, ViewKind.Write); _rtvOf = hdr; }

            var size = D3D11Native.Describe(hdr);
            var ctx = _context;
            Call<SetPtrFn>(ctx, IASetInputLayout)(ctx, IntPtr.Zero);
            Call<SetUIntFn>(ctx, IASetPrimitiveTopology)(ctx, 4); // D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST
            Call<SetShaderFn>(ctx, VSSetShader)(ctx, _vs, IntPtr.Zero, 0);
            Call<SetShaderFn>(ctx, HSSetShader)(ctx, IntPtr.Zero, IntPtr.Zero, 0);
            Call<SetShaderFn>(ctx, DSSetShader)(ctx, IntPtr.Zero, IntPtr.Zero, 0);
            Call<SetShaderFn>(ctx, GSSetShader)(ctx, IntPtr.Zero, IntPtr.Zero, 0);
            Call<SetShaderFn>(ctx, PSSetShader)(ctx, _ps, IntPtr.Zero, 0);
            var rtv = _rtv;
            Call<SetTargetsFn>(ctx, OMSetRenderTargets)(ctx, 1, &rtv, IntPtr.Zero);
            var srvs = stackalloc IntPtr[2];
            srvs[0] = _remixSrv; srvs[1] = fog ? _factorSrv : IntPtr.Zero;
            Call<SetViewsFn>(ctx, PSSetShaderResources)(ctx, 0, 2, srvs);
            Call<SetBlendFn>(ctx, OMSetBlendState)(ctx, IntPtr.Zero, null, 0xffffffff);
            Call<SetDepthFn>(ctx, OMSetDepthStencilState)(ctx, IntPtr.Zero, 0);
            Call<SetPtrFn>(ctx, RSSetState)(ctx, IntPtr.Zero);
            var viewport = stackalloc float[] { 0, 0, size.Width, size.Height, 0, 1 };
            Call<SetViewportsFn>(ctx, RSSetViewports)(ctx, 1, viewport);
            Call<DrawFn>(ctx, Draw)(ctx, 3, 0);
            // 次に Unity がこの絵を読むので、描き先と読み元を外しておく
            srvs[0] = IntPtr.Zero; srvs[1] = IntPtr.Zero;
            Call<SetViewsFn>(ctx, PSSetShaderResources)(ctx, 0, 2, srvs);
            Call<SetTargetsFn>(ctx, OMSetRenderTargets)(ctx, 0, null, IntPtr.Zero);
        }
        catch (Exception e)
        {
            _broken = true;
            error($"Remix の絵を HDR に戻せないので止めます: {e.Message}");
        }
    }

    private static string Defines(bool fog, Color c, float scale) =>
        FormattableString.Invariant($"#define FOG {(fog ? 1 : 0)}\n#define FOG_COLOR float3({c.r:R}, {c.g:R}, {c.b:R})\n#define SCALE {scale:R}\n");

    private IntPtr Shader(string entry, string target, string defines, bool isPixel)
    {
        var src = Encoding.ASCII.GetBytes(defines + Source);
        int hr = D3DCompile(src, (IntPtr)src.Length, "RemixFog", IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out var code, out var errors);
        if (hr < 0)
        {
            string message = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(Call<BlobPtrFn>(errors, 3)(errors)) : $"0x{hr:X8}";
            if (errors != IntPtr.Zero) D3D11Native.ReleaseRef(errors);
            throw new InvalidOperationException($"シェーダーを作れない: {message}");
        }
        if (errors != IntPtr.Zero) D3D11Native.ReleaseRef(errors);
        try
        {
            IntPtr ptr = Call<BlobPtrFn>(code, 3)(code), size = Call<BlobPtrFn>(code, 4)(code);
            Check(Call<CreateShaderFn>(_device, isPixel ? CreatePixelShader : CreateVertexShader)(_device, ptr, size, IntPtr.Zero, out var shader), entry);
            return shader;
        }
        finally { D3D11Native.ReleaseRef(code); }
    }

    private enum ViewKind { SrgbRead, UnormRead, Write }

    /// <summary>型の無いテクスチャ（Unity の RenderTexture）はビューの形式を決める必要がある。Remix の絵は sRGB で読み（線形になる）、霧の濃さはそのまま読む。</summary>
    private IntPtr View(IntPtr texture, int method, ViewKind kind)
    {
        uint format = D3D11Native.Describe(texture).Format;
        if (format is TypelessRgba8 or UnormRgba8 or SrgbRgba8) format = kind == ViewKind.SrgbRead ? SrgbRgba8 : UnormRgba8;
        else if (format == TypelessRgba16) format = FloatRgba16;
        bool srv = kind != ViewKind.Write;
        // SRV: Format, ViewDimension（TEXTURE2D = 4）, MostDetailedMip, MipLevels。RTV: Format, ViewDimension（TEXTURE2D = 4）, MipSlice
        var desc = stackalloc uint[6];
        desc[0] = format; desc[1] = 4; desc[2] = 0; desc[3] = srv ? 1u : 0u; desc[4] = 0; desc[5] = 0;
        Check(Call<CreateViewFn>(_device, method)(_device, texture, desc, out var view), srv ? "CreateShaderResourceView" : "CreateRenderTargetView");
        return view;
    }

    private static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) D3D11Native.ReleaseRef(p);
        p = IntPtr.Zero;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} が失敗（0x{hr:X8}）");
    }

    private static T Call<T>(IntPtr self, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>((*(IntPtr**)self)[index]);
}
