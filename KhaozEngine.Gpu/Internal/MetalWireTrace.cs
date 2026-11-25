using System;
using System.Diagnostics;

namespace KhaozEngine.Gpu.Internal;

internal static class MetalWireTrace
{
    internal const string Key = "KE.NativeMapWire";

    internal static void Stamp(string phase)
    {
        if (Activity.Current?.GetCustomProperty(Key) is Action<string> write)
            write(phase);
    }

    internal static void Cost(string phase, long started, string detail = "")
    {
        if (Activity.Current?.GetCustomProperty(Key) is Action<string> write)
            write(FormattableString.Invariant($"metal cost | {phase} | {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} | {detail}"));
    }
}
