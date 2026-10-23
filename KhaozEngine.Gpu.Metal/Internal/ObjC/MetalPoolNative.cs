using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC;

// The archive's only native substitution seam. Tests install it on their own thread, never globally.
internal interface IMetalPoolNative
{
    IntPtr Push();
    void Pop(IntPtr pool);
}

internal static class MetalPoolNative
{
    [ThreadStatic] internal static IMetalPoolNative? Override;
    [ThreadStatic] internal static long PushCount;
    [ThreadStatic] internal static long PopCount;

    internal static bool Supported => OperatingSystem.IsMacOS() || Override != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IntPtr Push()
    {
        IntPtr pool;
        if (Override is { } native) pool = native.Push();
        else if (OperatingSystem.IsMacOS()) pool = ObjCRuntime.AutoreleasePoolPush();
        else return IntPtr.Zero;
        // Count returned calls, including nil acquisition. A throwing seam did not return a native call.
        PushCount++;
        return pool;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Pop(IntPtr pool)
    {
        if (Override is { } native) native.Pop(pool);
        else if (OperatingSystem.IsMacOS()) ObjCRuntime.AutoreleasePoolPop(pool);
        else return;
        PopCount++;
    }
}
