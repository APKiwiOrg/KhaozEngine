using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Shared CPU and GPU skinned caster entry points for either point-shadow atlas.</summary>
internal sealed partial class PointShadowRenderer
{
    internal const uint SkinnedCasterSlotBytes = 256;

    [StructLayout(LayoutKind.Sequential)]
    readonly struct SkinnedCasterHeader
    {
        internal readonly Matrix4x4 Model;
        internal readonly Vector4 Dissolve;

        internal SkinnedCasterHeader(in Matrix4x4 model, float threshold, float complement)
        {
            Model = model;
            Dissolve = new Vector4(threshold, complement, 0f, 0f);
        }
    }

    readonly IGpuResourceLayout _paletteLayout;
    IGpuShaderSet _skinnedShaders = null!;
    IGpuShaderSet _skinnedDissolveShaders = null!;
    IGpuShaderSet _skinnedDissolveInvertedShaders = null!;
    IGpuResourceLayout _skinnedCasterLayout = null!;
    IGpuPipeline _skinnedPipeline = null!;
    IGpuPipeline _skinnedDissolvePipeline = null!;
    IGpuPipeline _skinnedDissolveInvertedPipeline = null!;
    IGpuBuffer _skinnedCasterUbo = null!;
    IGpuResourceSet _skinnedCasterSet = null!;
    uint _skinnedCasterSlots;
    byte[] _skinnedCasterImage = Array.Empty<byte>();

    void InitializeSkinnedResources(IGpuResourceFactory factory, GpuOutputDescription outputs,
        List<IDisposable> built)
    {
        _skinnedShaders = Built(built, factory.CreateShadersFromSpirv(
            ShaderSources.PointShadowSkinnedVert, ShaderSources.PointShadowRigidFrag));
        _skinnedDissolveShaders = Built(built, factory.CreateShadersFromSpirv(
            ShaderSources.PointShadowSkinnedDissolveVert, ShaderSources.PointShadowRigidDissolveFrag));
        _skinnedDissolveInvertedShaders = Built(built, factory.CreateShadersFromSpirv(
            ShaderSources.PointShadowSkinnedDissolveVert, ShaderSources.PointShadowRigidDissolveInvertedFrag));
        _skinnedCasterLayout = Built(built, factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Caster", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex,
                dynamic: true))));
        _skinnedPipeline = Built(built, BuildSkinnedPipeline(factory, outputs, _skinnedShaders));
        _skinnedDissolvePipeline = Built(built, BuildSkinnedPipeline(factory, outputs, _skinnedDissolveShaders));
        _skinnedDissolveInvertedPipeline = Built(built,
            BuildSkinnedPipeline(factory, outputs, _skinnedDissolveInvertedShaders));
        (_skinnedCasterUbo, _skinnedCasterSet, _skinnedCasterSlots) = AllocateSkinnedCasterSlots(1);
        Built(built, _skinnedCasterUbo);
        Built(built, _skinnedCasterSet);
        _skinnedCasterImage = new byte[SkinnedCasterSlotBytes];
    }

    IGpuPipeline BuildSkinnedPipeline(IGpuResourceFactory factory, GpuOutputDescription outputs,
        IGpuShaderSet shaders)
    {
        var vertexLayout = new GpuVertexLayoutDescription(
            new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
            new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
            new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
            new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
            new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
        return factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _layout, _skinnedCasterLayout, _paletteLayout },
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
            Outputs = outputs,
        });
    }

    (IGpuBuffer Buffer, IGpuResourceSet Set, uint Slots) AllocateSkinnedCasterSlots(uint slots)
    {
        IGpuBuffer buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(
            slots * SkinnedCasterSlotBytes, GpuBufferUsage.UniformBuffer));
        try
        {
            IGpuResourceSet set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedCasterLayout, new GpuBufferRange(buffer, 0, SkinnedCasterSlotBytes)));
            return (buffer, set, slots);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    internal void EnsureSkinnedCasterCapacity(uint casterCount)
    {
        uint wanted = Math.Max(1u, casterCount);
        if (_skinnedCasterSlots >= wanted) return;
        uint grown = Math.Max(wanted, _skinnedCasterSlots * 2);
        (IGpuBuffer buffer, IGpuResourceSet set, uint slots) = AllocateSkinnedCasterSlots(grown);
        var image = new byte[checked((int)(slots * SkinnedCasterSlotBytes))];
        _skinnedCasterImage.AsSpan().CopyTo(image);
        _retired.Add(_skinnedCasterUbo);
        _retired.Add(_skinnedCasterSet);
        (_skinnedCasterUbo, _skinnedCasterSet, _skinnedCasterSlots) = (buffer, set, slots);
        _skinnedCasterImage = image;
    }

    internal void PackSkinnedCaster(uint slot, in Matrix4x4 model, float dissolveThreshold,
        float dissolveComplement)
    {
        Span<byte> destination = _skinnedCasterImage.AsSpan(
            checked((int)(slot * SkinnedCasterSlotBytes)), checked((int)SkinnedCasterSlotBytes));
        destination.Clear();
        var header = new SkinnedCasterHeader(model, dissolveThreshold, dissolveComplement);
        MemoryMarshal.Write(destination, in header);
    }

    internal void UploadSkinnedCasters(IGpuCommandList cl) =>
        cl.UpdateBuffer(_skinnedCasterUbo, 0, (ReadOnlySpan<byte>)_skinnedCasterImage);

    internal void BeginGpuSkinnedFace(IGpuCommandList cl, PointShadowAtlas atlas, int packedFaceIndex,
        int face, int row, ShadowCastKind kind)
    {
        cl.SetPipeline(kind switch
        {
            ShadowCastKind.Opaque => _skinnedPipeline,
            ShadowCastKind.Dissolving => _skinnedDissolvePipeline,
            ShadowCastKind.DissolvingInverted => _skinnedDissolveInvertedPipeline,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No caster pipeline exists for None."),
        });
        uint res = (uint)atlas.FaceResolution;
        cl.SetScissorRect(0, (uint)Math.Clamp(face, 0, PointShadowMath.FaceCount - 1) * res,
            (uint)Math.Clamp(row, 0, atlas.Rows - 1) * res, res, res);
        cl.SetGraphicsResourceSet(0, _set, (uint)packedFaceIndex * FaceSlotBytes);
    }

    internal void DrawGpuSkinnedCaster(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib,
        int indexCount, GpuIndexFormat indexFormat, uint casterSlot, IGpuResourceSet paletteSet,
        uint paletteOffset)
    {
        cl.SetGraphicsResourceSet(1, _skinnedCasterSet, casterSlot * SkinnedCasterSlotBytes);
        cl.SetGraphicsResourceSet(2, paletteSet, paletteOffset);
        cl.SetVertexBuffer(0, restVb);
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
    }

    internal void DrawCpuSkinnedCaster(IGpuCommandList cl, IGpuBuffer deformedVb,
        IGpuBuffer instanceBuffer, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
        int baseVertex, uint drawIndex)
    {
        cl.SetVertexBuffer(0, deformedVb);
        cl.SetVertexBuffer(1, instanceBuffer);
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, 1, 0, baseVertex, drawIndex);
    }

    void DisposeSkinnedResources()
    {
        _skinnedCasterSet.Dispose();
        _skinnedCasterUbo.Dispose();
        _skinnedDissolveInvertedPipeline.Dispose();
        _skinnedDissolvePipeline.Dispose();
        _skinnedPipeline.Dispose();
        _skinnedCasterLayout.Dispose();
        _skinnedDissolveInvertedShaders.Dispose();
        _skinnedDissolveShaders.Dispose();
        _skinnedShaders.Dispose();
    }
}
