using System;
using KhaozEngine.Gpu.Metal.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC;

// Probe-only state. Never attach this lifetime to a recording lease or a setup batch.
internal static class MetalFramePoolState
{
    [ThreadStatic] static long _next;
    [ThreadStatic] static long _active;
    [ThreadStatic] static long _bypassed;
    [ThreadStatic] static long _frames;

    internal static MetalFramePoolCounters Counters => new(MetalPoolNative.PushCount, MetalPoolNative.PopCount,
        _bypassed, _frames);

    internal static MetalFramePoolProbe.Scope Enter()
    {
        if (!MetalPoolNative.Supported) return default;
        long previous = _active;
        long token = _next = checked(_next + 1);
        IntPtr pool = previous == 0 ? MetalPoolNative.Push() : IntPtr.Zero;
        if (previous == 0 && pool == IntPtr.Zero)
            throw new InvalidOperationException("The private Metal frame-pool probe could not acquire its native pool.");
        // Publishing after acquisition keeps reentry and failed acquisition on the standalone path.
        _active = token;
        _frames++;
        return new MetalFramePoolProbe.Scope(token, previous, pool);
    }

    internal static void Leave(long token, long previous, int thread)
    {
        if (thread != Environment.CurrentManagedThreadId || _active != token)
            throw new InvalidOperationException("Metal frame-pool probe scopes must close on their entering thread in LIFO order.");
        _active = previous;
    }

    internal static IntPtr EnterLocal()
    {
        if (_active != 0)
        {
            _bypassed++;
            return IntPtr.Zero;
        }
        return MetalPoolNative.Supported ? MetalPoolNative.Push() : IntPtr.Zero;
    }
}
