using System;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Shared geometric helpers keep their original implementation, but reference work is never production work.
internal sealed class FrozenWaterCounterScope : IDisposable
{
    readonly bool _enabled;
#if WATER_PRISM_CENSUS
    readonly Action<string>? _count;
#endif

    FrozenWaterCounterScope()
    {
        _enabled = TileWaterQueryWork.Enabled;
        TileWaterQueryWork.Enabled = false;
#if WATER_PRISM_CENSUS
        _count = TileWaterCensus.Count;
        TileWaterCensus.Count = null;
#endif
    }

    internal static FrozenWaterCounterScope Suppress() => new();

    public void Dispose()
    {
        TileWaterQueryWork.Enabled = _enabled;
#if WATER_PRISM_CENSUS
        TileWaterCensus.Count = _count;
#endif
    }
}
