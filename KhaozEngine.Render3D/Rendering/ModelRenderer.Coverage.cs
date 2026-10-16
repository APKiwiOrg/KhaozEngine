using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    internal void ReserveCoveredSkinning(int vertices, int instances)
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
    }
}
