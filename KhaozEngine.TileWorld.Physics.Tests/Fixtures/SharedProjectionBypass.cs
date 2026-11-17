using System;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Sets the process-wide shared projection bypass and restores it. Writers run only in serialized collections,
// and the bypass changes no output, so a concurrent reader can only observe a different sharing count.
internal sealed class SharedProjectionBypass : IDisposable
{
    readonly bool _previous = TileWaterSharedProjections.BypassForTests;
    internal SharedProjectionBypass(bool value) => TileWaterSharedProjections.BypassForTests = value;
    public void Dispose() => TileWaterSharedProjections.BypassForTests = _previous;
}
