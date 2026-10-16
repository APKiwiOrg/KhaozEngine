using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer
{
    internal (IGpuTexture Full, IGpuTexture Visible) CoverageTargetsForTests =>
        (_fullCoverage ?? throw new System.InvalidOperationException("No full outline target."),
         _visibleCoverage ?? throw new System.InvalidOperationException("No visible outline target."));

    internal void EnqueuePrepared(IGpuBuffer vertices, IGpuBuffer indices, int indexCount,
        GpuIndexFormat format, IGpuResourceSet? material, int drawIndex, Matrix4x4 world,
        float alphaCutoff, float dissolve, bool complement, Vector3 origin, int baseVertex)
    {
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, complement, origin);
        _queue.Add(new QueuedDraw(QueuedGeometry.Rigid, vertices, indices, indexCount, format,
            material ?? _defaultMaterialSet, drawIndex, -1, baseVertex, 0, 0));
    }
}
