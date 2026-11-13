using System;
using System.Runtime.InteropServices;

namespace HeberekeBunnyGardenRemix.Remix;

/// <summary>
/// ゲームの IDXGISwapChain::Present の直前に割り込む。Remix Plus の D3D11 の道では、Present のとき
/// 「最後に RenderD3D11 で渡された先がスワップチェーンのバッファ」でないとフレームが締まらない（d3d11_swapchain.cpp の FinishRemixFrame）。
/// Unity は中間のテクスチャ（R8G8B8A8_TYPELESS）に描いて、Present の直前にスワップチェーンへ写すので、
/// 本物のバッファ（GetBuffer(0)）は Present の中でしか確かめられない。そこでここで締め先として渡す。
/// Unity はスワップチェーンを C# に出さないので、使い捨てのスワップチェーンを隠し窓で 1 つ作り、
/// 全部のスワップチェーンで共通の vtable の Present と Present1 を差し替える。
/// </summary>
internal static unsafe class PresentHook
{
    // IDXGISwapChain の vtable の番号（dxgi.h の宣言順）
    private const int SlotPresent = 8, SlotGetBuffer = 9, SlotPresent1 = 22;
    // IDXGIObject::GetParent、IDXGIDevice::GetAdapter、IDXGIFactory::CreateSwapChain
    private const int SlotGetParent = 6, SlotGetAdapter = 7, SlotCreateSwapChain = 10, SlotQueryInterface = 0;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiFactory = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentFn(IntPtr self, uint syncInterval, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Present1Fn(IntPtr self, uint syncInterval, uint flags, IntPtr parameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, Guid* iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetParentFn(IntPtr self, Guid* iid, out IntPtr parent);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSwapChainFn(IntPtr self, IntPtr device, SwapChainDesc* desc, out IntPtr swapChain);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferFn(IntPtr self, uint index, Guid* iid, out IntPtr surface);

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public uint SwapEffect, Flags;
    }

    // 差し替えた関数は GC に回収されないよう static に持つ
    private static readonly PresentFn s_present = OnPresent;
    private static readonly Present1Fn s_present1 = OnPresent1;
    private static PresentFn s_originalPresent;
    private static Present1Fn s_originalPresent1;
    private static Action<IntPtr> s_beforePresent;
    private static bool s_reportedError;

    /// <summary>
    /// Present の直前に beforePresent(スワップチェーンのバッファ) を呼ぶようにする。device はゲームの ID3D11Device* か、そのデバイスの物。
    /// beforePresent は Unity の描画スレッドで呼ばれる。中で投げた例外は握りつぶして 1 回だけ報告する（Present を止めないため）。
    /// </summary>
    public static void Install(IntPtr d3d11Device, Action<IntPtr> beforePresent, Action<string> report)
    {
        if (s_originalPresent != null) throw new InvalidOperationException("PresentHook は 1 回だけ");
        s_beforePresent = beforePresent;
        s_report = report;

        var window = CreateWindowExW(0, "STATIC", "HbgRemixProbe", 0x80000000 /* WS_POPUP */, 0, 0, 8, 8, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException($"隠し窓を作れません（Win32 エラー {Marshal.GetLastWin32Error()}）");
        IntPtr dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero, factory = IntPtr.Zero, swapChain = IntPtr.Zero;
        try
        {
            var iidDevice = IidDxgiDevice;
            Check(Method<QueryInterfaceFn>(d3d11Device, SlotQueryInterface)(d3d11Device, &iidDevice, out dxgiDevice), "QueryInterface(IDXGIDevice)");
            Check(Method<GetAdapterFn>(dxgiDevice, SlotGetAdapter)(dxgiDevice, out adapter), "IDXGIDevice::GetAdapter");
            var iidFactory = IidDxgiFactory;
            Check(Method<GetParentFn>(adapter, SlotGetParent)(adapter, &iidFactory, out factory), "IDXGIAdapter::GetParent(IDXGIFactory)");
            var desc = new SwapChainDesc
            {
                Width = 8, Height = 8, Format = 28 /* R8G8B8A8_UNORM */, SampleCount = 1, BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */,
                BufferCount = 2, OutputWindow = window, Windowed = 1, SwapEffect = 4 /* FLIP_DISCARD */,
            };
            Check(Method<CreateSwapChainFn>(factory, SlotCreateSwapChain)(factory, d3d11Device, &desc, out swapChain), "IDXGIFactory::CreateSwapChain");

            var vtbl = *(IntPtr**)swapChain;
            s_originalPresent = Marshal.GetDelegateForFunctionPointer<PresentFn>(vtbl[SlotPresent]);
            s_originalPresent1 = Marshal.GetDelegateForFunctionPointer<Present1Fn>(vtbl[SlotPresent1]);
            Patch(vtbl, SlotPresent, Marshal.GetFunctionPointerForDelegate(s_present));
            Patch(vtbl, SlotPresent1, Marshal.GetFunctionPointerForDelegate(s_present1));
        }
        finally
        {
            foreach (var p in new[] { swapChain, factory, adapter, dxgiDevice }) if (p != IntPtr.Zero) D3D11Native.ReleaseRef(p);
            DestroyWindow(window);
        }
    }

    private static Action<string> s_report;

    private static int OnPresent(IntPtr self, uint syncInterval, uint flags)
    {
        BeforePresent(self);
        return s_originalPresent(self, syncInterval, flags);
    }

    private static int OnPresent1(IntPtr self, uint syncInterval, uint flags, IntPtr parameters)
    {
        BeforePresent(self);
        return s_originalPresent1(self, syncInterval, flags, parameters);
    }

    private static void BeforePresent(IntPtr swapChain)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var iid = IidTexture2D;
            if (Method<GetBufferFn>(swapChain, SlotGetBuffer)(swapChain, 0, &iid, out buffer) < 0 || buffer == IntPtr.Zero) return;
            s_beforePresent(buffer);
        }
        catch (Exception e)
        {
            if (!s_reportedError) { s_reportedError = true; s_report?.Invoke($"Present の割り込みで失敗しました: {e}"); }
        }
        finally
        {
            if (buffer != IntPtr.Zero) D3D11Native.ReleaseRef(buffer);
        }
    }

    private static void Patch(IntPtr* vtbl, int slot, IntPtr function)
    {
        var at = (IntPtr)(vtbl + slot);
        if (!VirtualProtect(at, (UIntPtr)IntPtr.Size, 0x04 /* PAGE_READWRITE */, out var old)) throw new InvalidOperationException($"VirtualProtect（Win32 エラー {Marshal.GetLastWin32Error()}）");
        vtbl[slot] = function;
        VirtualProtect(at, (UIntPtr)IntPtr.Size, old, out _);
    }

    private static T Method<T>(IntPtr self, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>((*(IntPtr**)self)[index]);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} が失敗しました（HRESULT 0x{hr:X8}）");
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);
}
