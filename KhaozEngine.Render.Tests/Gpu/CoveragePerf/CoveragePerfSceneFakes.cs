using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    readonly bool _completeOutcomes;
    readonly CoveragePerfTimerLayout _layout;
    readonly List<SubmissionTestFence> _submittedFences = [];
    IGpuCommandList? _frameCommands;
    internal CoveragePerfNativeSession Session { get; }
    internal List<string> Events { get; } = [];
    internal int UploadCalls;
    internal long TickAtStart, TickAtPresentEntry, TickAfterPresentForward, Ticks;
    internal bool HistoryCommittedAtPresent;
    internal int ParticipantsAtReadback;
    internal int LastSubmittedFenceCount;
    internal Action<string>? OnTrace;
    internal Action<Frame>? OnReadbacks;
    internal bool RefuseOutcomes;
    internal List<CoveragePerfTimestampTestPool> TimestampPools { get; } = [];
    internal FakeGpuResourceFactory Resources => _device.Resources;
    internal GpuCapabilities? ReportedCapabilities { get; set; }

    CoveragePerfSceneProbe(int actors, bool covered, bool completeOutcomes, CoveragePerfTimerLayout layout, bool direct = false)
    {
        _completeOutcomes = completeOutcomes;
        _layout = layout;
        _device = new(this);
        var manifest = CoveragePerfManifest.Parse(new CoveragePerfFakeRun(actors == 32 ? "C1" : "C0").Json);
        var block = manifest.Blocks.First(b => (direct || b.Actors == actors) && b.Condition ==
            (direct ? CoveragePerfCondition.DirectOff : covered ? CoveragePerfCondition.SharedOn : CoveragePerfCondition.SharedOff)) with
        { TimerLayout = layout, Actors = actors };
        Session = new(manifest, block, (_, _) => new(_device, Equipment, true, _device.Dispose)
        {
            Observe = Observe,
        }, new CoveragePerfClock(() => Ticks, 1000));
    }

    internal static CoveragePerfSceneProbe Open(int actors, bool covered, bool completeOutcomes = false,
        CoveragePerfTimerLayout layout = CoveragePerfTimerLayout.CpuOnly)
        => new(actors, covered, completeOutcomes, layout);
    internal static CoveragePerfSceneProbe OpenDirect(int actors) => new(actors, false, false, CoveragePerfTimerLayout.CpuOnly, true);
    internal void CompletePendingOutcomes()
    {
        foreach (var fence in _submittedFences) fence.Signaled = true;
        _submittedFences.Clear();
    }

    internal GpuFrameSubmission RepeatCurrentOutcomes()
    {
        var submission = new GpuFrameSubmission(Session.Device);
        submission.Begin();
        Session.Typical.Scene.RecordCoverageReadbacks(new ProbeCommands(this), submission);
        submission.Submit(new ProbeCommands(this));
        return submission;
    }
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
            _frameCommands = frame.Commands;
            OnReadbacks?.Invoke(frame);
            ParticipantsAtReadback = CoveragePerfSubmissionProbe.Capture(frame).Participants;
            // Decorate an existing production notification only in this fake. Adding audit participants would
            // contaminate the counts we are now reporting and could hide registration regressions.
            var participants = (IGpuFrameSubmissionParticipant?[])typeof(GpuFrameSubmission)
                .GetField("participants", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame.Submission)!;
            if (participants[0] is { } participant)
                participants[0] = new Participant(participant, () => Trace("history-submitted"));
        }
    }

    internal void Trace(string name)
    {
        Events.Add(name); Ticks += name == "present-forward" ? 10000 : 10;
        if (name == "present-forward") TickAfterPresentForward = Ticks;
        OnTrace?.Invoke(name);
    }
    public void Dispose() => Session.Dispose();

    sealed class Participant(IGpuFrameSubmissionParticipant inner, Action submitted) : IGpuFrameSubmissionParticipant
    {
        public void Submitted() { inner.Submitted(); submitted(); }
        public void Abandoned() => inner.Abandoned();
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
        internal FakeGpuResourceFactory Resources => (FakeGpuResourceFactory)_inner.Factory;
        internal ProbeDevice(CoveragePerfSceneProbe probe)
        { _probe = probe; _factory = new(new CoverageRecordingFactory((FakeGpuResourceFactory)_inner.Factory, false), probe); }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _probe.ReportedCapabilities ?? new(false, true, deviceName: "semantic coverage probe", supportsCompute: true,
            supportsCompletionFences: _probe._completeOutcomes)
        { MaxColorAttachments = 8, SupportsTimestamps = _probe._layout != CoveragePerfTimerLayout.CpuOnly };
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get; set; }
        public void Submit(IGpuCommandList commands) => _probe.Trace("submit-direct");
        public void Submit(IGpuCommandList commands, IGpuFence fence)
        {
            if (!_probe._completeOutcomes) throw new InvalidOperationException("Expected the production span submit.");
            _probe._submittedFences.Add((SubmissionTestFence)fence);
            _probe.Trace("retire-submit");
        }
        public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
        {
            _probe.LastSubmittedFenceCount = completions.Length;
            foreach (var fence in completions) _probe._submittedFences.Add((SubmissionTestFence)fence);
            _probe.Trace("submit-span");
        }
        public void WaitForIdle() { _probe.Trace("drain"); _probe.CompletePendingOutcomes(); }
        public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        {
            if (!completion.Signaled) return false;
            _probe.Trace("outcome-read");
            ((SubmissionTestBuffer)staging).Bytes.AsSpan((int)offsetBytes, destination.Length).CopyTo(destination);
            return true;
        }
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
        public void Dispose() { _probe.Trace("host-release"); _inner.Dispose(); }
    }

    sealed class ProbeFactory(IGpuResourceFactory source, CoveragePerfSceneProbe probe) : IGpuResourceFactory
    {
        public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
        {
            return probe._completeOutcomes && description.Usage == GpuBufferUsage.Staging
                ? new SubmissionTestBuffer(description.SizeInBytes, description.Usage) : source.CreateBuffer(description);
        }
        public IGpuTexture CreateTexture(in GpuTextureDescription description)
        {
            var texture = source.CreateTexture(description);
            if (probe.Resources.Textures.Count == 1)
                ((FakeTexture)texture).OnDispose = () => probe.Trace("colour-dispose");
            return texture;
        }
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            var target = source.CreateFramebuffer(depth, colour);
            return probe.Resources.Framebuffers.Count == 1 ? new ProbeTarget(target, probe) : target;
        }
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
        public IGpuTimestampPool CreateTimestampPool(uint capacity)
        {
            int index = probe.TimestampPools.Count;
            var pool = new CoveragePerfTimestampTestPool(capacity)
            { OnDispose = () => probe.Trace("timestamp-dispose-" + index) };
            probe.TimestampPools.Add(pool);
            return pool;
        }
        public IGpuFence CreateFence()
        {
            if (!probe._completeOutcomes) return source.CreateFence();
            return new SubmissionTestFence();
        }
    }

    sealed class ProbeTarget(IGpuFramebuffer target, CoveragePerfSceneProbe probe) : IGpuFramebuffer
    {
        public uint Width => target.Width;
        public uint Height => target.Height;
        public GpuOutputDescription Outputs => target.Outputs;
        public void Dispose() { probe.Trace("target-dispose"); target.Dispose(); }
    }

    sealed class ProbeCommands(CoveragePerfSceneProbe probe) : IGpuCommandList, IGpuCommandListTimestampEnd
    {
        CoveragePerfTimestampTestPool? _timestampPool;
        uint _tail;
        bool _armed;
        public void Begin() { _armed = false; _timestampPool = null; probe.Trace("commands-begin"); }
        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        { _timestampPool = (CoveragePerfTimestampTestPool)pool; _timestampPool.Begin(count); }
        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        { ((CoveragePerfTimestampTestPool)pool).Write(index); probe.Trace("timestamp-" + index); }
        public void EndTimestamps(IGpuTimestampPool pool) => ((CoveragePerfTimestampTestPool)pool).End();
        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            if (!ReferenceEquals(pool, _timestampPool) || _armed || _timestampPool.Written != finalIndex)
                throw new InvalidOperationException("Incorrect timestamp tail.");
            _tail = finalIndex; _armed = true;
        }
        public void End()
        {
            if (_armed) { WriteTimestamp(_timestampPool!, _tail); EndTimestamps(_timestampPool!); _armed = false; }
            probe.Trace("commands-end");
        }
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
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
        {
            if (dst is not SubmissionTestBuffer staging) return;
            // Synthetic completion records only immutable identities, never numerical GPU/reference results.
            staging.Bytes.AsSpan().Clear();
            foreach (var actor in probe.Session.Typical.Actors)
            {
                var p = actor.Frame.Preparation!;
                var words = MemoryMarshal.Cast<byte, uint>(staging.Bytes.AsSpan()).Slice(p.GpuReservation.ActorSlot * 16, 16);
                Pair(words, 0, p.Generation); Pair(words, 2, p.View); Pair(words, 4, p.Actor.Value); Pair(words, 6, p.Id);
                if (probe.RefuseOutcomes) words[8] = uint.MaxValue;
            }
            static void Pair(Span<uint> words, int offset, ulong value)
            { words[offset] = (uint)value; words[offset + 1] = (uint)(value >> 32); }
        }
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
        public void Dispose()
        { if (ReferenceEquals(this, probe._frameCommands)) probe.Trace("commands-dispose"); }
    }
}
