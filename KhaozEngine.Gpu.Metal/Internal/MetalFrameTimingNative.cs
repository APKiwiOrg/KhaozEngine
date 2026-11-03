using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct FrameTimingClockMap(bool Available, long BeforeTimestamp, long AfterTimestamp, double MediaTime);

internal static unsafe partial class MetalFrameTimingNative
{
    static IntPtr _presentedBlock;

    // Called during recorder creation, before arming, to resolve selectors, P/Invoke and static native storage.
    internal static void Prepare()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PrepareOnMac();
    }

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PrepareOnMac()
    {
        _ = CACurrentMediaTime();
        _ = Selectors.GpuStartTime;
        if (Volatile.Read(ref _presentedBlock) != IntPtr.Zero) return;
        if (!NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out IntPtr system)
            || !NativeLibrary.TryGetExport(system, "_NSConcreteGlobalBlock", out IntPtr isa)) return;
        var descriptor = (MetalTimelineNative.BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(MetalTimelineNative.BlockDescriptor));
        descriptor->Size = (nuint)sizeof(MetalTimelineNative.BlockLiteral);
        var literal = (MetalTimelineNative.BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(MetalTimelineNative.BlockLiteral));
        literal->Isa = isa;
        literal->Flags = 1 << 28; // BLOCK_IS_GLOBAL. Metal copies no captures and owns no managed lifetime.
        literal->Invoke = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Presented;
        literal->Descriptor = (IntPtr)descriptor;
        if (Interlocked.CompareExchange(ref _presentedBlock, (IntPtr)literal, IntPtr.Zero) != IntPtr.Zero)
        {
            NativeMemory.Free(literal);
            NativeMemory.Free(descriptor);
        }
    }

    internal static FrameTimingClockMap ClockMap()
    {
        if (!OperatingSystem.IsMacOS()) return default;
        return ClockMapOnMac();
    }

    [SupportedOSPlatform("macos")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static FrameTimingClockMap ClockMapOnMac()
    {
        FrameTimingClockMap best = default;
        long width = long.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            long before = Stopwatch.GetTimestamp();
            double media = CACurrentMediaTime();
            long after = Stopwatch.GetTimestamp();
            if (after - before < width)
            {
                width = after - before;
                best = new(true, before, after, media);
            }
        }
        return best;
    }

    [SupportedOSPlatform("macos")]
    internal static void Submit(IntPtr buffer, FrameTimingCommandKind kind, IGpuCommandList? commands)
    {
        if (!MetalFrameTimingProbe.Sampling) return;
        MetalFrameTimingProbe.Submit(buffer, ObjCMsgSend.Send(buffer, Selectors.CommandQueue), kind, commands);
    }

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("macos")]
    static void Presented(IntPtr block, IntPtr drawable)
    {
        _ = block;
        if (!MetalFrameTimingProbe.Sampling) return;
        using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
        var reader = new Reader();
        MetalFrameTimingProbe.ObservePresented(drawable, ref reader);
    }

    [SupportedOSPlatform("macos")]
    internal struct Reader : IMetalFrameTimingReader
    {
        public void ReadGpuTimes(IntPtr buffer, out double start, out double end)
        {
            start = ObjCMsgSend.SendDouble(buffer, Selectors.GpuStartTime);
            end = ObjCMsgSend.SendDouble(buffer, Selectors.GpuEndTime);
        }
        public ulong DrawableId(IntPtr drawable) => ObjCMsgSend.SendULong(drawable, Selectors.DrawableId);
        public double PresentedTime(IntPtr drawable) => ObjCMsgSend.SendDouble(drawable, Selectors.PresentedTime);
        public bool AttachPresentedHandler(IntPtr drawable)
        {
            IntPtr block = Volatile.Read(ref _presentedBlock);
            if (block == IntPtr.Zero) return false;
            ObjCMsgSend.SendVoidPtr(drawable, Selectors.AddPresentedHandler, block);
            return true;
        }
    }

    [LibraryImport("/System/Library/Frameworks/QuartzCore.framework/QuartzCore")]
    [SupportedOSPlatform("macos")]
    private static partial double CACurrentMediaTime();

    [SupportedOSPlatform("macos")]
    static class Selectors
    {
        internal static readonly IntPtr GpuStartTime = ObjCRuntime.Sel("GPUStartTime");
        internal static readonly IntPtr GpuEndTime = ObjCRuntime.Sel("GPUEndTime");
        internal static readonly IntPtr DrawableId = ObjCRuntime.Sel("drawableID");
        internal static readonly IntPtr PresentedTime = ObjCRuntime.Sel("presentedTime");
        internal static readonly IntPtr AddPresentedHandler = ObjCRuntime.Sel("addPresentedHandler:");
        internal static readonly IntPtr CommandQueue = ObjCRuntime.Sel("commandQueue");
    }
}
