using System;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Hit contracts need a known token, independently of the host's native reader support.
// The seam is thread-static, so each tracing worker owns its own fixture scope.
internal sealed class StableWaterTraceControls : IDisposable
{
    readonly TileMediumSampler.ControlRead? _previous = TileMediumSampler.ControlReaderForTests;

    internal StableWaterTraceControls() => TileMediumSampler.ControlReaderForTests = Read;

    internal static bool Read(out ulong token)
    {
        token = 0x5344;
        return true;
    }

    public void Dispose() => TileMediumSampler.ControlReaderForTests = _previous;
}
