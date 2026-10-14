using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Displaced foliage depth pipelines sharing the cascade atlas and retained foliage stream.</summary>
internal sealed class FoliageShadowRenderer : IDisposable
{
    internal const int CascadePayloadBytes = 144;
    internal const uint CascadeSlotBytes = 256;
    readonly IGpuDevice _device;
    readonly ShadowMapRenderer _shadows;
    readonly IGpuResourceLayout _foliageLayout;
    readonly GpuRetireQueue _retired;
    readonly IGpuResourceLayout _layout;
    readonly IGpuBuffer _buffer;
    readonly IGpuResourceSet _set;
    readonly byte[] _image = new byte[ShadowSettings.MaxCascades * CascadeSlotBytes];
    IGpuShaderSet? _opaqueShaders, _cutoutShaders;
    IGpuPipeline? _opaque, _cutout;
    GpuOutputDescription _outputs;

    internal FoliageShadowRenderer(IGpuDevice device, ShadowMapRenderer shadows,
        IGpuResourceLayout foliageLayout, GpuRetireQueue retired)
    {
        _device = device;
        _shadows = shadows;
        _foliageLayout = foliageLayout;
        _retired = retired;
        _layout = device.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        _buffer = device.Factory.CreateBuffer(
            new GpuBufferDescription((uint)_image.Length, GpuBufferUsage.UniformBuffer));
        _set = device.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
            new GpuBufferRange(_buffer, 0, CascadeSlotBytes)));
    }

    internal void UploadCascades(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> lightViewProjections,
        in Matrix4x4 cameraViewProjection, Vector3 renderOrigin)
    {
        if (lightViewProjections.Length > ShadowSettings.MaxCascades)
            throw new ArgumentOutOfRangeException(nameof(lightViewProjections));
        var origin = new Vector4(renderOrigin, 0f);
        for (int i = 0; i < lightViewProjections.Length; i++)
        {
            Span<byte> slot = _image.AsSpan(i * (int)CascadeSlotBytes, (int)CascadeSlotBytes);
            MemoryMarshal.Write(slot, in lightViewProjections[i]);
            MemoryMarshal.Write(slot.Slice(64), in cameraViewProjection);
            MemoryMarshal.Write(slot.Slice(128), in origin);
        }
        // Whole-buffer upload keeps the native D3D11 uniform path off partial-write staging.
        cl.UpdateBuffer<byte>(_buffer, 0, _image);
    }

    internal void Draw(IGpuCommandList cl, int cascade, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat indexFormat, IGpuBuffer instances, uint start, uint count,
        IGpuResourceSet foliageSet, uint foliageSlot, IGpuResourceSet? cutoutMaterial)
    {
        EnsurePipelines();
        cl.SetPipeline(cutoutMaterial is null ? _opaque! : _cutout!);
        _shadows.SetCascadeScissor(cl, cascade);
        cl.SetGraphicsResourceSet(0, _set, (uint)cascade * CascadeSlotBytes);
        cl.SetGraphicsResourceSet(1, foliageSet, foliageSlot * ModelRenderer.FoliageUniforms.SlotBytes);
        if (cutoutMaterial is not null) cl.SetGraphicsResourceSet(2, cutoutMaterial);
        cl.SetVertexBuffer(0, vb);
        cl.SetVertexBuffer(1, instances);
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, count, 0, 0, start);
    }

    void EnsurePipelines()
    {
        GpuOutputDescription outputs = _shadows.Outputs;
        if (_opaque is not null && _outputs.Depth == outputs.Depth && _outputs.SampleCount == outputs.SampleCount
            && _outputs.Colour.AsSpan().SequenceEqual(outputs.Colour)) return;
        if (_opaque is not null) _retired.Retire(_opaque);
        if (_cutout is not null) _retired.Retire(_cutout);
        _opaqueShaders ??= _device.Factory.CreateShadersFromSpirv(ShaderSources.FoliageDepthVert, ShaderSources.ShadowDepthFrag);
        _cutoutShaders ??= _device.Factory.CreateShadersFromSpirv(ShaderSources.FoliageDepthVert, ShaderSources.FoliageDepthCutoutFrag);
        _outputs = outputs;
        _opaque = Pipeline(_opaqueShaders, cutout: false);
        _cutout = Pipeline(_cutoutShaders, cutout: true);
    }

    IGpuPipeline Pipeline(IGpuShaderSet shaders, bool cutout) =>
        _device.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new GpuRasterizerState(cutout ? GpuFaceCull.None : GpuFaceCull.Front,
                GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = cutout
                ? [_layout, _foliageLayout, _shadows.CutoutMaterialLayout]
                : [_layout, _foliageLayout],
            ShaderSet = shaders,
            VertexLayouts = new(FoliageVertexLayouts.Create()),
            Outputs = _outputs,
        });

    public void Dispose()
    {
        _cutout?.Dispose();
        _opaque?.Dispose();
        _cutoutShaders?.Dispose();
        _opaqueShaders?.Dispose();
        _set.Dispose();
        _buffer.Dispose();
        _layout.Dispose();
    }
}
