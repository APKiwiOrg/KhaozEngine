using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class FoliageShadowRenderer
{
    IGpuPipeline? _preparedOpaque, _preparedCutout;
    IGpuShaderSet? _preparedOpaqueShaders, _preparedCutoutShaders;
    IGpuResourceLayout? _preparedAnchorLayout, _preparedEmptyMaterialLayout;
    GpuOutputDescription _preparedOutputs;

    internal void DrawPrepared(IGpuCommandList cl, int cascade, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat format, IGpuBuffer instances, uint start, uint count, IGpuResourceSet foliageSet,
        uint foliageSlot, IGpuResourceSet? cutout, IGpuResourceSet anchorSet, IGpuResourceLayout anchorLayout)
    {
        PrepareAnchoredPipelines(anchorLayout);
        cl.SetPipeline(cutout is null ? _preparedOpaque! : _preparedCutout!);
        _shadows.SetCascadeScissor(cl, cascade);
        cl.SetGraphicsResourceSet(0, _set, (uint)cascade * CascadeSlotBytes);
        cl.SetGraphicsResourceSet(1, foliageSet, foliageSlot * ModelRenderer.FoliageUniforms.SlotBytes);
        if (cutout is not null) cl.SetGraphicsResourceSet(2, cutout);
        cl.SetGraphicsResourceSet(3, anchorSet);
        cl.SetVertexBuffer(0, vb); cl.SetVertexBuffer(1, instances); cl.SetIndexBuffer(ib, format);
        cl.DrawIndexed((uint)indexCount, count, 0, 0, start);
    }
    internal void PrepareAnchoredPipelines(IGpuResourceLayout anchorLayout)
    {
        var outputs = _shadows.Outputs;
        if (_preparedOpaque is null || !_preparedOutputs.Equals(outputs))
        {
            if (_preparedOpaque is not null) _retired.Retire(_preparedOpaque);
            if (_preparedCutout is not null) _retired.Retire(_preparedCutout);
            _preparedAnchorLayout = anchorLayout;
            // Opaque depth has no material resources. Keep an empty set 2 so both shaders retain anchor set 3.
            _preparedEmptyMaterialLayout ??= _device.Factory.CreateResourceLayout(new(Array.Empty<GpuResourceLayoutElement>()));
            _preparedOpaqueShaders ??= _device.Factory.CreateShadersFromSpirv(
                ShaderSources.PreparedFoliage(ShaderSources.FoliageDepthVert, 3), ShaderSources.ShadowDepthFrag);
            _preparedCutoutShaders ??= _device.Factory.CreateShadersFromSpirv(
                ShaderSources.PreparedFoliage(ShaderSources.FoliageDepthVert, 3), ShaderSources.FoliageDepthCutoutFrag);
            _preparedOpaque = PreparedPipeline(_preparedOpaqueShaders, false, outputs);
            _preparedCutout = PreparedPipeline(_preparedCutoutShaders, true, outputs);
            _preparedOutputs = outputs;
        }
    }
    IGpuPipeline PreparedPipeline(IGpuShaderSet shaders, bool cutout, GpuOutputDescription outputs) =>
        _device.Factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new(cutout ? GpuFaceCull.None : GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, true),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_layout, _foliageLayout, cutout ? _shadows.CutoutMaterialLayout : _preparedEmptyMaterialLayout!, _preparedAnchorLayout!],
            ShaderSet = shaders,
            VertexLayouts = new(FoliageVertexLayouts.Create()),
            Outputs = outputs
        });
    void DisposePrepared()
    { _preparedOpaque?.Dispose(); _preparedCutout?.Dispose(); _preparedOpaqueShaders?.Dispose(); _preparedCutoutShaders?.Dispose(); _preparedEmptyMaterialLayout?.Dispose(); }
}
