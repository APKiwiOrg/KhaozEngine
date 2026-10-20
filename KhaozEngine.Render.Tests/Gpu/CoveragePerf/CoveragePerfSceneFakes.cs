using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu;

// All time is numeric and injected. No GLB import, native factory, payload capture or reference computation.
internal sealed class CoveragePerfSceneProbe : IDisposable
{
    readonly ProbeDevice _device;
    readonly Participant[] _participants;
    internal CoveragePerfNativeSession Session { get; }
    internal List<string> Events { get; } = [];
    internal int UploadCalls;
    internal long TickAtStart, TickAtPresentEntry, Ticks;
    internal bool HistoryCommittedAtPresent;
    internal int ParticipantsAtReadback;

    CoveragePerfSceneProbe(int actors, bool covered)
    {
        _device = new(this);
        _participants = Enumerable.Range(0, 8).Select(i => new Participant(i == 0 ? () => Trace("history-submitted") : null)).ToArray();
        var manifest = CoveragePerfManifest.Parse(new CoveragePerfFakeRun(actors == 32 ? "C1" : "C0").Json);
        var block = manifest.Blocks.First(b => b.Actors == actors && b.Condition ==
            (covered ? CoveragePerfCondition.SharedOn : CoveragePerfCondition.SharedOff));
        Session = new(manifest, block, (_, _) => new(_device, Equipment, true, _device.Dispose)
        {
            Observe = Observe,
        }, new CoveragePerfClock(() => Ticks, 1000));
    }

    internal static CoveragePerfSceneProbe Open(int actors, bool covered) => new(actors, covered);
    internal CoveragePerfFrameSample[] Warm()
    {
        var samples = new CoveragePerfFrameSample[CoveragePerfManifest.WarmupFrameCount];
        int batchStart = 0;
        for (int frame = 0; frame < samples.Length; frame++)
        {
            samples[frame] = Frame(frame);
            if (frame + 1 - batchStart != CoveragePerfManifest.BatchFrameCount && frame + 1 != samples.Length) continue;
            Session.CompleteBatch(samples.AsSpan(batchStart, frame + 1 - batchStart));
            batchStart = frame + 1;
        }
        return samples;
    }

    internal CoveragePerfFrameSample Frame(int frame)
    {
        Events.Clear(); UploadCalls = 0;
        CoveragePerfFrameSample sample = default;
        Session.Frame(frame, ref sample);
        return sample;
    }

    void Observe(string name, Frame frame)
    {
        if (name == "aggregate-start") TickAtStart = Ticks;
        if (name == "aggregate-stop")
        {
            TickAtPresentEntry = Ticks;
            HistoryCommittedAtPresent = Session.Typical.Actors.All(a =>
                !Session.Typical.Covered || a.Frame.Preparation!.MotionPresented);
        }
        Trace(name);
        if (name == "readbacks")
        {
            // Observe remaining capacity without reflecting private fields. Production owns the first participant.
            int accepted = 0;
            foreach (var participant in _participants) if (frame.Submission.TryRegister(participant)) accepted++;
            ParticipantsAtReadback = 8 - accepted;
        }
    }

    internal void Trace(string name) { Events.Add(name); Ticks += name == "present-forward" ? 10000 : 10; }
    public void Dispose() => Session.Dispose();

    sealed class Participant(Action? submitted) : IGpuFrameSubmissionParticipant
    {
        public void Submitted() => submitted?.Invoke();
        public void Abandoned() { }
    }

    static CoveragePerfEquipment Equipment(CoverageCostGeometry geometry)
    {
        var plate = CoverageCostGeometry.EquipmentPlatePositions;
        var indices = CoverageCostGeometry.EquipmentPlateIndices;
        int inwardTriangles = geometry.Commands[0].InwardSupport.Sum(range => range.Count);
        var positions = new List<Vector3>(); var vertices = new List<CoverageSourceVertex>();
        var triangles = new List<CoverageSourceTriangle>();
        var meshes = new GltfMesh[2];
        for (int slice = 0; slice < 2; slice++)
        {
            var meshVertices = new List<ModelVertex>();
            int ordinal = 0;
            foreach (var region in geometry.Regions)
                for (int t = slice == 0 ? 0 : inwardTriangles; t < (slice == 0 ? inwardTriangles : indices.Length / 3); t++)
                {
                    int start = positions.Count;
                    for (int c = 0; c < 3; c++)
                    {
                        var p = plate[indices[t * 3 + c]] + new Vector3(region.Center, 0);
                        vertices.Add(new(0, slice, meshVertices.Count, 0, slice, meshVertices.Count));
                        positions.Add(p); meshVertices.Add(new(p, Vector3.UnitZ, Vector4.One));
                    }
                    triangles.Add(new(0, slice, ordinal++, 0, slice, start, start + 1, start + 2));
                }
            meshes[slice] = new(meshVertices.ToArray(), Enumerable.Range(0, meshVertices.Count).Select(i => (uint)i).ToArray());
        }
        return new(new CoverageSourceTopology("synthetic-equipment", CoverageCostGeometry.Hash,
            positions.ToArray(), vertices, triangles), meshes);
    }

    sealed class ProbeDevice : IGpuDevice
    {
        readonly CoveragePerfSceneProbe _probe;
        readonly FakeGpuDevice _inner = new(GpuBackendKind.VulkanNative);
        readonly ProbeFactory _factory;
        internal ProbeDevice(CoveragePerfSceneProbe probe)
        { _probe = probe; _factory = new(new CoverageRecordingFactory((FakeGpuResourceFactory)_inner.Factory, false), probe); }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => new(false, true, deviceName: "semantic coverage probe", supportsCompute: true)
        { MaxColorAttachments = 8 };
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get; set; }
        public void Submit(IGpuCommandList commands) => throw new InvalidOperationException("Expected the production span submit.");
        public void Submit(IGpuCommandList commands, IGpuFence fence) => throw new InvalidOperationException("Expected the production span submit.");
        public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions) => _probe.Trace("submit-span");
        public void WaitForIdle() { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> values) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offset, T[] values) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T value) where T : unmanaged { }
        public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height) { }
        public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height, uint mip, uint layer) { }
        public MappedData Map(IGpuTexture texture, GpuMapMode mode) => throw new NotSupportedException();
        public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => throw new NotSupportedException();
        public void Unmap(IGpuTexture texture) { }
        public void Unmap(IGpuBuffer buffer) { }
        public void ResizeSwapchain(uint width, uint height) => throw new NotSupportedException();
        public void Present() => _probe.Trace("present-forward");
        public void Dispose() => _inner.Dispose();
    }

    sealed class ProbeFactory(IGpuResourceFactory source, CoveragePerfSceneProbe probe) : IGpuResourceFactory
    {
        public IGpuBuffer CreateBuffer(in GpuBufferDescription description) { return source.CreateBuffer(description); }
        public IGpuTexture CreateTexture(in GpuTextureDescription description) { return source.CreateTexture(description); }
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) { return source.CreateFramebuffer(depth, colour); }
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription description) { return source.CreateResourceSet(description); }
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription description) { return source.CreateResourceLayout(description); }
        public IGpuSampler CreateSampler(in GpuSamplerDescription description) { return source.CreateSampler(description); }
        public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) { return source.CreateShadersFromSpirv(vertex, fragment); }
        public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options) { return source.CreateShadersFromSpirv(vertex, fragment, options); }
        public IGpuComputeShader CreateComputeShaderFromSpirv(string compute) { return source.CreateComputeShaderFromSpirv(compute); }
        public IGpuComputeShader CreateComputeShaderFromSpirv(string compute, GpuShaderCompileOptions options) { return source.CreateComputeShaderFromSpirv(compute, options); }
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription description) { return source.CreateGraphicsPipeline(description); }
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription description) { return source.CreateComputePipeline(description); }
        public IGpuCommandList CreateCommandList() { return new ProbeCommands(probe); }
        public IGpuTimestampPool CreateTimestampPool(uint capacity) { return source.CreateTimestampPool(capacity); }
        public IGpuFence CreateFence() { return source.CreateFence(); }
    }

    sealed class ProbeCommands(CoveragePerfSceneProbe probe) : IGpuCommandList
    {
        public void Begin() => probe.Trace("commands-begin");
        public void End() => probe.Trace("commands-end");
        public void SetFramebuffer(IGpuFramebuffer fb) { }
        public void ClearColorTarget(uint index, Color rgba) { }
        public void ClearDepthStencil(float depth) { }
        public void SetPipeline(IGpuPipeline p) { }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
        public void SetVertexBuffer(uint slot, IGpuBuffer b) { }
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { }
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { }
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { }
        public void SetFullScissorRects() { }
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { }
        public void Draw(uint vertexCount) { }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { probe.UploadCalls++; probe.Trace("upload"); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { probe.UploadCalls++; probe.Trace("upload"); }
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        { }
        public void GenerateMipmaps(IGpuTexture texture) => MipGenerations.Add(texture);

        /// <summary>Every texture a GenerateMipmaps call named, so a test can pin that a single-level upload
        /// never asks for a generation pass.</summary>
        internal List<IGpuTexture> MipGenerations { get; } = new();
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
        public void SetComputePipeline(IGpuComputePipeline p) { }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
        public void Dispose() { }
    }
}
