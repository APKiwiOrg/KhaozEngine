using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

// A scene supplies its completion queue. Standalone callers retain their existing drain contract.
internal static class ResourceRetirement
{
    internal static void Free(IDisposable? resource, GpuRetireQueue? retired)
    {
        if (retired is null) resource?.Dispose();
        else retired.Retire(resource);
    }
}
