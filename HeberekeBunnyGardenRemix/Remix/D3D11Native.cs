using System;
using System.Runtime.InteropServices;

namespace HeberekeBunnyGardenRemix.Remix;

/// <summary>
/// D3D11 の COM を vtable の番号で直接呼ぶ小道具（Unity が出さない物を取るため）。番号は d3d11.h の宣言順。
/// </summary>
internal static unsafe class D3D11Native
{
    private const int Release = 2, DeviceChildGetDevice = 3, Texture2DGetDesc = 10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetPtrFn(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDescFn(IntPtr self, Texture2DDesc* desc);

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    private static T Method<T>(IntPtr self, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>((*(IntPtr**)self)[index]);

    public static void ReleaseRef(IntPtr unknown) => Method<ReleaseFn>(unknown, Release)(unknown);

    /// <summary>child（テクスチャなど）のデバイス。参照を 1 つ持って返すので、使い終えたら ReleaseRef する。</summary>
    public static IntPtr Device(IntPtr child)
    {
        Method<GetPtrFn>(child, DeviceChildGetDevice)(child, out var device);
        return device;
    }

    public static Texture2DDesc Describe(IntPtr texture2D)
    {
        Texture2DDesc desc;
        Method<GetDescFn>(texture2D, Texture2DGetDesc)(texture2D, &desc);
        return desc;
    }
}
