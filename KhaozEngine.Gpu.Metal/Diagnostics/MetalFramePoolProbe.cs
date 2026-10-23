using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Diagnostics;

/// <summary>Private archive probe. NEVER MERGE into a production engine.</summary>
public static class MetalFramePoolProbe
{
    /// <summary>Share one native pool within this synchronous, same-thread lexical frame only.</summary>
    public static Scope Enter(bool enabled) => enabled ? MetalFramePoolState.Enter() : default;

    /// <summary>Non-flowing cumulative pool counts on the calling thread.</summary>
    public static MetalFramePoolCounters Counters => MetalFramePoolState.Counters;

    /// <summary>A stack-only frame boundary. Dispose in LIFO order before returning or awaiting.</summary>
    public ref struct Scope
    {
        long _token;
        readonly long _previous;
        readonly int _thread;
        readonly IntPtr _pool;

        internal Scope(long token, long previous, IntPtr pool)
        {
            _token = token;
            _previous = previous;
            _thread = Environment.CurrentManagedThreadId;
            _pool = pool;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            MetalFramePoolState.Leave(_token, _previous, _thread);
            _token = 0;
            // Leave cleared the root before native deallocation can reenter a public Metal call.
            if (_pool != IntPtr.Zero) MetalPoolNative.Pop(_pool);
        }
    }
}

/// <summary>Cumulative returned native calls on the current thread, without execution timings.</summary>
public readonly record struct MetalFramePoolCounters(long NativePushCount, long NativePopCount,
    long BypassedLocalScopeCount, long ExplicitFrameScopeCount);
