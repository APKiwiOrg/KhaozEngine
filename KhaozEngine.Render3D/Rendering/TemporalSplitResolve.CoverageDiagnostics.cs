using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TemporalSplitResolve
{
    internal IGpuResourceSet CoverageAccumulateSetForTests(int readIndex) =>
        _accumulateSets[readIndex] ?? throw new InvalidOperationException("No bound accumulation set.");
}
