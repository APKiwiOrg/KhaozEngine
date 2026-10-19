using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>A cold-created retain whose retirement covers the final migration copy or history draw.</summary>
internal sealed class CoverageGpuHistoryLease(CoverageGpuBatch batch, GpuRetireQueue retired) : IDisposable
{
    bool retained, queued;

    internal void Activate()
    {
        if (queued) throw new InvalidOperationException("A retired coverage history lease cannot be reused.");
        if (retained) return;
        batch.RetainHistory(); retained = true;
    }

    internal void Retire()
    {
        if (!retained || queued) return;
        queued = true; retired.Retire(this);
    }

    public void Dispose()
    {
        if (!retained) return;
        retained = false; batch.ReleaseHistory();
    }
}
