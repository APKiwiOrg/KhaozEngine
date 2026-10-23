using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PointShadowRenderer
{
    IGpuPipeline? _preparedFoliagePipeline;
    IGpuShaderSet? _preparedFoliageShaders;
    internal void DrawPreparedFoliage(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indices,
        GpuIndexFormat format, IGpuBuffer instances, uint start, uint count, IGpuResourceSet anchor,
        IGpuResourceLayout anchorLayout)
    {
        PrepareFoliagePipeline(anchorLayout);
        cl.SetPipeline(_preparedFoliagePipeline!);
        cl.SetGraphicsResourceSet(1, anchor);
        cl.SetVertexBuffer(0, vb); cl.SetVertexBuffer(1, instances); cl.SetIndexBuffer(ib, format);
        cl.DrawIndexed((uint)indices, count, 0, 0, start);
    }
    internal void PrepareFoliagePipeline(IGpuResourceLayout anchorLayout)
    {
        if (_preparedFoliagePipeline is null)
        {
            _preparedFoliageShaders = _gd.Factory.CreateShadersFromSpirv(
                ShaderSources.PreparedPointFoliageVert, ShaderSources.PointShadowRigidFrag);
            _preparedFoliagePipeline = _gd.Factory.CreateGraphicsPipeline(new()
            {
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [_layout, anchorLayout],
                ShaderSet = _preparedFoliageShaders,
                VertexLayouts = new(FoliageVertexLayouts.Create()),
                Outputs = new(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R32Float)
            });
        }
    }
    void DisposePreparedFoliage()
    { _preparedFoliagePipeline?.Dispose(); _preparedFoliageShaders?.Dispose(); }
}
