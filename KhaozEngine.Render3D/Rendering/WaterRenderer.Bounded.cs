using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct BoundedWaterDraw(WaterSurfaceMesh Surface, IGpuBuffer Vertices, IGpuBuffer Indices,
    int IndexCount, GpuIndexFormat IndexFormat, float LocalSurfaceY);

internal sealed partial class WaterRenderer
{
    IGpuShaderSet? _boundedShaders;
    IGpuPipeline? _boundedPipe;
    IGpuResourceLayout? _boundedLayout;
    IGpuBuffer? _boundedTransforms;
    byte[] _boundedTransformImage = Array.Empty<byte>();
    int _boundedCapacity, _boundedGeneration;
    readonly Dictionary<MeshHandle, DomainGpu> _boundedDomains = new();
    sealed class DomainGpu(IGpuBuffer buffer, bool ownsBuffer = true)
    {
        internal readonly IGpuBuffer Buffer = buffer;
        internal readonly bool OwnsBuffer = ownsBuffer;
        internal IGpuResourceSet? Set;
        internal int Generation;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BoundedTransform
    {
        internal Matrix4x4 LocalToRender;
        internal Matrix4x4 RenderToLocal;
    }
    const uint BoundedSlotBytes = 256;

    internal static ReadOnlySpan<WaterPlane> LegacyPlanesWithBoundedDemand(ReadOnlySpan<WaterPlane> legacy,
        ReadOnlySpan<WaterSurfaceMesh> bounded)
    {
        if (bounded.IsEmpty) return legacy;
        var demand = new WaterPlane[checked(legacy.Length + bounded.Length)];
        legacy.CopyTo(demand);
        for (int i = 0; i < bounded.Length; i++) demand[legacy.Length + i] = new(0, 0, 0, 0, look: bounded[i].Look);
        return demand;
    }

    internal void RegisterBoundedDomain(MeshHandle handle, ReadOnlySpan<byte> bytes)
    {
        WaterSurfaceDomain.Validate(bytes);
        if (_boundedDomains.ContainsKey(handle)) throw new InvalidOperationException("Water domain is already registered for this mesh.");
        EnsureBoundedPipeline();
        var buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(checked((uint)bytes.Length), GpuBufferUsage.StructuredBufferReadOnly, 16));
        try { _gd.UpdateBuffer(buffer, 0, bytes); _boundedDomains.Add(handle, new(buffer)); }
        catch { buffer.Dispose(); throw; }
    }
    internal void RegisterPublishedDomain(MeshHandle handle, IGpuBuffer buffer)
    {
        // The shared fallback is immutable resource data. Initialize it before publication can admit draws.
        _ = ColumnShadows.SceneColor;
        _boundedDomains.Add(handle, new(buffer, false));
    }
    internal bool HasBoundedDomain(MeshHandle handle) => _boundedDomains.ContainsKey(handle);
    internal void ForgetBoundedDomain(MeshHandle handle)
    {
        if (!_boundedDomains.Remove(handle, out var domain)) return;
        _retired.Retire(domain.Set); if (domain.OwnsBuffer) _retired.Retire(domain.Buffer);
    }
    void EnsureBoundedPipeline()
    {
        if (_boundedLayout is null)
            _boundedLayout = _gd.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("BoundedWaterTransform", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true),
                new GpuResourceLayoutElement("BoundedWaterDomain", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment)));
        _boundedShaders ??= _gd.Factory.CreateShadersFromSpirv(ShaderSources.BoundedWaterVert, ShaderSources.BoundedWaterFrag);
        _boundedPipe ??= _gd.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
            DepthStencil = new GpuDepthStencilState(true, false, GpuComparison.Less),
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _layout, _boundedLayout },
            ShaderSet = _boundedShaders,
            VertexLayouts = new List<GpuVertexLayoutDescription> { new(ModelVertex.SizeInBytes, 0,
                new[] { new GpuVertexElement("Position", GpuVertexElementFormat.Float3) }) },
            Outputs = _outputs,
        });
    }
    void ResetBoundedOutputs()
    {
        _retired.Retire(_boundedPipe); _boundedPipe = null;
    }
    void PackBounded(int firstSlot, ReadOnlySpan<BoundedWaterDraw> draws, Matrix4x4 vp, Vector3 lightDirection,
        Color lightColor, Vector3 eye, WaterSettings settings, SkySettings sky, float time, Vector3 origin,
        OceanMaps ocean, ShoreMaps shore, bool atmosphere, uint columnNoisePhase)
    {
        if (draws.IsEmpty) return;
        EnsureBoundedPipeline();
        if (_boundedCapacity < draws.Length)
        {
            _boundedCapacity = Math.Max(draws.Length, Math.Max(4, _boundedCapacity * 2));
            if (_boundedTransforms is not null) _retired.Retire(_boundedTransforms);
            _boundedTransforms = _gd.Factory.CreateBuffer(new GpuBufferDescription(checked((uint)_boundedCapacity * BoundedSlotBytes), GpuBufferUsage.UniformBuffer));
            _boundedTransformImage = new byte[checked(_boundedCapacity * (int)BoundedSlotBytes)];
            _boundedGeneration++;
        }
        for (int i = 0; i < draws.Length; i++)
        {
            var draw = draws[i].Surface;
            var localToRender = new AnchoredRigidTransform(draw.LocalTransform, draw.Anchor).RelativeTo(origin);
            if (!Matrix4x4.Invert(localToRender, out var inverse)) throw new ArgumentException("Water transform is not invertible.");
            var transform = new BoundedTransform { LocalToRender = localToRender, RenderToLocal = inverse };
            MemoryMarshal.Write(_boundedTransformImage.AsSpan(i * (int)BoundedSlotBytes), in transform);
            var effective = draw.Look.ResolveInto(_effective, settings);
            var maps = effective.WaveSource == WaterWaveSource.FftOcean ? ocean : default;
            var ubo = PackUbo(GpuClip.Correct(vp, _gd.Capabilities), vp, lightDirection, lightColor, eye, effective, sky,
                time, maps, origin, shore, localToRender.M42 + draws[i].LocalSurfaceY * localToRender.M22, atmosphere, columnNoisePhase);
            PackSlot(firstSlot + i, in ubo);
        }
    }
    static bool BoundedWantsOcean(ReadOnlySpan<BoundedWaterDraw> draws, WaterSettings settings)
    {
        foreach (var draw in draws)
            if ((draw.Surface.Look.WaveSource ?? settings.WaveSource) == WaterWaveSource.FftOcean) return true;
        return false;
    }
    void DrawBounded(IGpuCommandList cl, int firstSlot, ReadOnlySpan<BoundedWaterDraw> draws)
    {
        if (draws.IsEmpty) return;
        cl.UpdateBuffer(_boundedTransforms!, 0, (ReadOnlySpan<byte>)_boundedTransformImage);
        cl.SetPipeline(_boundedPipe!);
        for (int i = 0; i < draws.Length; i++)
        {
            var draw = draws[i]; var domain = _boundedDomains[draw.Surface.Mesh];
            if (domain.Set is null || domain.Generation != _boundedGeneration)
            {
                if (domain.Set is not null) _retired.Retire(domain.Set);
                domain.Set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_boundedLayout!,
                    new GpuBufferRange(_boundedTransforms!, 0, BoundedSlotBytes), domain.Buffer));
                domain.Generation = _boundedGeneration;
            }
            cl.SetGraphicsResourceSet(0, _set!, checked((uint)(firstSlot + i) * SlotBytes));
            cl.SetGraphicsResourceSet(1, domain.Set, checked((uint)i * BoundedSlotBytes));
            cl.SetVertexBuffer(0, draw.Vertices); cl.SetIndexBuffer(draw.Indices, draw.IndexFormat);
            cl.DrawIndexed(checked((uint)draw.IndexCount), 1, 0, 0, 0);
        }
    }
    void DisposeBounded()
    {
        foreach (var domain in _boundedDomains.Values) { domain.Set?.Dispose(); if (domain.OwnsBuffer) domain.Buffer.Dispose(); }
        _boundedDomains.Clear(); _boundedTransforms?.Dispose(); _boundedPipe?.Dispose();
        _boundedShaders?.Dispose(); _boundedLayout?.Dispose();
    }
}
