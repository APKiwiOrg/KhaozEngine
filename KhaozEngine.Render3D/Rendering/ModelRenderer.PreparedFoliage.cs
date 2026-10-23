using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    IGpuResourceLayout? _preparedAnchorLayout;
    IGpuPipeline? _preparedFoliagePipeline;
    IGpuShaderSet? _preparedFoliageShaders;
    GpuOutputDescription _preparedFoliageOutputs;
    internal IGpuResourceLayout PreparedAnchorLayout => _preparedAnchorLayout ??=
        _gd.Factory.CreateResourceLayout(new(new GpuResourceLayoutElement("SceneAnchor", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));

    internal IGpuResourceSet CreatePreparedFoliageAnchorSet(IGpuBuffer buffer) =>
        _gd.Factory.CreateResourceSet(new(PreparedAnchorLayout, buffer));

    internal void DrawPreparedFoliageMesh(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat format, IGpuBuffer instances, uint start, uint count, uint slot,
        IGpuResourceSet? material, IGpuResourceSet anchor)
    {
        EnsurePreparedFoliagePipeline();
        cl.SetPipeline(_preparedFoliagePipeline!);
        cl.SetGraphicsResourceSet(0, material ?? _defaultSet);
        cl.SetGraphicsResourceSet(1, _foliageSet!, slot * FoliageUniforms.SlotBytes);
        if (_motion is { } motion && MotionMath.IsTemporal(_foliageOutputs)) cl.SetGraphicsResourceSet(2, motion.FrameSet);
        cl.SetGraphicsResourceSet(MotionMath.IsTemporal(_foliageOutputs) ? 3u : 2u, anchor);
        cl.SetVertexBuffer(0, vb); cl.SetVertexBuffer(1, instances); cl.SetIndexBuffer(ib, format);
        cl.DrawIndexed((uint)indexCount, count, 0, 0, start);
    }
    void EnsurePreparedFoliagePipeline()
    {
        if (_preparedFoliagePipeline is not null && _preparedFoliageOutputs.Depth == _foliageOutputs.Depth &&
            _preparedFoliageOutputs.SampleCount == _foliageOutputs.SampleCount &&
            _preparedFoliageOutputs.Colour.AsSpan().SequenceEqual(_foliageOutputs.Colour)) return;
        if (_preparedFoliagePipeline is not null) _retired.Retire(_preparedFoliagePipeline);
        if (_preparedFoliageShaders is not null) _retired.Retire(_preparedFoliageShaders);
        bool motion = _motion is not null && MotionMath.IsTemporal(_foliageOutputs);
        _preparedFoliageShaders = null;
        string vertex = ShaderSources.PreparedFoliage(motion ? ShaderSources.FoliageMotionVert : ShaderSources.FoliageVert, motion ? 3 : 2);
        string fragment = motion ? ShaderSources.ModelMotionFrag : ShaderSources.ModelFrag;
        IGpuShaderSet shaders = _visibilityShaders.TrySelect(_gd.Factory, _foliageOutputs, vertex, fragment,
            preserve: false, out IGpuShaderSet visibilityShaders) ? visibilityShaders
                : (_preparedFoliageShaders = _gd.Factory.CreateShadersFromSpirv(vertex, fragment));
        _preparedFoliagePipeline = _gd.Factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = ModelTargetBlends.Opaque(_foliageOutputs),
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = motion ? [_layout, _foliageLayout!, _motion!.FrameLayout, PreparedAnchorLayout]
                : [_layout, _foliageLayout!, PreparedAnchorLayout],
            ShaderSet = shaders,
            VertexLayouts = new(FoliageVertexLayouts.Create()),
            Outputs = _foliageOutputs
        });
        _preparedFoliageOutputs = _foliageOutputs;
    }
    void DisposePreparedFoliage()
    { _preparedFoliagePipeline?.Dispose(); _preparedFoliageShaders?.Dispose(); _preparedAnchorLayout?.Dispose(); }
}
