using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    // CPU-skinned previous positions reserved before recording while the motion resources do not exist yet. The
    // renderer owns this buffer until EnsureMotionResources hands it to the motion resources it creates.
    IGpuBuffer? _cpuPreviousReserve;
    uint _cpuPreviousReserveCapacity;

    internal (uint Capacity, IGpuBuffer? Buffer) CpuPreviousReservationForTests =>
        _motion is { } motion ? (motion.CpuPreviousCapacity, motion.CpuPreviousCapacity == 0 ? null : motion.CpuPrevious)
            : (_cpuPreviousReserveCapacity, _cpuPreviousReserve);

    internal void ReserveCoveredSkinning(int vertices, int instances, int previous)
    {
        IGpuBuffer? nextVertices = null, nextInstances = null;
        uint vc = Math.Max((uint)vertices, _skinnedVertexCapacity), ic = Math.Max((uint)instances, _skinnedInstanceCapacity);
        try
        {
            if (_skinnedVertexBuffer is null || vc > _skinnedVertexCapacity)
                nextVertices = _gd.Factory.CreateBuffer(new(vc * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
            if (_skinnedInstanceBuffer is null || ic > _skinnedInstanceCapacity)
                nextInstances = _gd.Factory.CreateBuffer(new(ic * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }
        catch { nextVertices?.Dispose(); nextInstances?.Dispose(); throw; }
        if (nextVertices is not null)
        { _retired.Retire(_skinnedVertexBuffer); _skinnedVertexBuffer = nextVertices; _skinnedVertexCapacity = vc; }
        if (nextInstances is not null)
        { _retired.Retire(_skinnedInstanceBuffer); _skinnedInstanceBuffer = nextInstances; _skinnedInstanceCapacity = ic; }
        if (previous <= 0) return;
        if (_motion is { } motion) { motion.ReserveCpuPrevious((uint)previous); return; }
        if (_cpuPreviousReserve is not null && _cpuPreviousReserveCapacity >= previous) return;
        IGpuBuffer next = ModelMotionResources.CreateCpuPrevious(_gd, (uint)previous);
        _retired.Retire(_cpuPreviousReserve);
        _cpuPreviousReserve = next;
        _cpuPreviousReserveCapacity = (uint)previous;
    }

    void ReleaseCpuPreviousReserve(bool retire)
    {
        if (retire) _retired.Retire(_cpuPreviousReserve);
        else _cpuPreviousReserve?.Dispose();
        _cpuPreviousReserve = null;
        _cpuPreviousReserveCapacity = 0;
    }
}
