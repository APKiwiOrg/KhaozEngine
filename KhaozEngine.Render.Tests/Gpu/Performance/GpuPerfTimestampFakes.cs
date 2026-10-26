using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu.Performance;

// Numeric native-boundary doubles. They never manufacture native proof or clock durations.
internal sealed class GpuPerfTimestampTestPool(uint capacity) : IGpuTimestampPool
{
    public uint Capacity => capacity;
    internal int Begins, Resolves, Disposals;
    internal uint Count, Written;
    internal bool Ended, Invalid, Disjoint, Hold;
    internal double Period = 2.5;
    internal IGpuFence? ResolvedFence;
    internal Exception? ResolveFailure { get; set; }
    internal Action? OnBegin { get; set; }
    internal ulong[]? RawCounters { get; set; }
    internal ulong[]? ElapsedTicks { get; set; }
    internal List<SubmissionTestFence>? Owners { get; set; }
    internal IGpuFence? SubmittedFence { get; set; }
    internal Action? OnDispose { get; set; }
    internal void Begin(uint count)
    {
        if (Begins != 0 && !Consumed) throw new InvalidOperationException("Unconsumed timestamp pool reused.");
        OnBegin?.Invoke();
        if (count > capacity) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count; Written = 0; Ended = Consumed = false; Begins++;
    }
    bool Consumed;
    internal void Write(uint index)
    {
        if (index != Written || index >= Count || Ended) throw new InvalidOperationException("Timestamp order differs.");
        Written++;
    }
    internal void End()
    {
        if (Written != Count) throw new InvalidOperationException("Timestamp batch is incomplete.");
        Ended = true;
    }
    public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds)
    {
        Resolves++; ResolvedFence = completion; tickPeriodNanoseconds = 0;
        if (ResolveFailure is not null) throw ResolveFailure;
        if (Owners is not null && (completion is not SubmissionTestFence fence || !Owners.Contains(fence)))
            throw new ArgumentException("Foreign device fence.", nameof(completion));
        if (!ReferenceEquals(completion, SubmittedFence)) throw new ArgumentException("Foreign submission fence.", nameof(completion));
        if (!Ended) throw new InvalidOperationException("An unended timestamp batch was polled.");
        if (!completion.Signaled || Hold) return GpuTimestampStatus.Pending;
        Consumed = true;
        if (Invalid || Disjoint) return GpuTimestampStatus.Invalid;
        if (RawCounters is not null)
        {
            if (!GpuTimestampResults.TryNormalize(RawCounters, 64, ticks)) return GpuTimestampStatus.Invalid;
        }
        else for (int i = 0; i < Count; i++) ticks[i] = ElapsedTicks is null ? (ulong)i * 10 : ElapsedTicks[i];
        tickPeriodNanoseconds = Period;
        return GpuTimestampStatus.Ready;
    }
    public void Dispose() { Disposals++; OnDispose?.Invoke(); }
}

internal sealed class GpuPerfTimestampTestDevice : IGpuDevice, IGpuResourceFactory
{
    readonly FakeGpuDevice _inner = new();
    internal FakeGpuResourceFactory Resources => (FakeGpuResourceFactory)_inner.Factory;
    internal readonly List<GpuPerfTimestampTestPool> Pools = [];
    internal readonly List<SubmissionTestFence> Fences = [];
    internal readonly IGpuFence[] SubmittedFences = new IGpuFence[8];
    internal int Submissions, Waits, CompletionCount;
    internal bool FailSubmit;
    internal Action<IGpuFence>? OnSubmittedFence { get; set; }
    internal Func<uint, IGpuTimestampPool>? PoolFactory { get; set; }
    internal GpuCapabilities Features { get; set; } = new(false, true, supportsCompletionFences: true) { SupportsTimestamps = true };
    internal bool Software { get; set; }
    internal Action<string>? Trace { get; set; }
    public GpuDeviceDiagnostics Diagnostics => new(softwareAdapter: Software);
    public IGpuResourceFactory Factory => this;
    public GpuBackendKind Backend => GpuBackendKind.VulkanNative;
    public GpuCapabilities Capabilities => Features;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => _inner.PointSampler;
    public IGpuSampler LinearSampler => _inner.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList commands) => Submit(commands, ReadOnlySpan<IGpuFence>.Empty);
    public void Submit(IGpuCommandList commands, IGpuFence completion) => Submit(commands, [completion]);
    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        Trace?.Invoke("submit");
        Submissions++; CompletionCount = completions.Length;
        completions.CopyTo(SubmittedFences);
        foreach (IGpuFence completion in completions) OnSubmittedFence?.Invoke(completion);
        if (commands is GpuPerfTimestampTestCommands { Pool: { } pool }) pool.SubmittedFence = completions[0];
        if (FailSubmit) throw new InvalidOperationException("submit failure");
    }
    public void WaitForIdle() { Trace?.Invoke("drain"); Waits++; foreach (var fence in Fences) fence.Signaled = true; }
    public IGpuTimestampPool CreateTimestampPool(uint capacity)
    {
        if (PoolFactory is not null) return PoolFactory(capacity);
        var pool = new GpuPerfTimestampTestPool(capacity) { Owners = Fences }; Pools.Add(pool); return pool;
    }
    public IGpuFence CreateFence() { var fence = new SubmissionTestFence(); Fences.Add(fence); return fence; }
    public IGpuCommandList CreateCommandList() => new GpuPerfTimestampTestCommands { Trace = op => Trace?.Invoke(op) };
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => new SubmissionTestBuffer(d.SizeInBytes, d.Usage);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => _inner.Factory.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => _inner.Factory.CreateFramebuffer(depth, colour);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => _inner.Factory.CreateResourceSet(d);
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => _inner.Factory.CreateResourceLayout(d);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.Factory.CreateSampler(d);
    public IGpuShaderSet CreateShadersFromSpirv(string v, string f) => _inner.Factory.CreateShadersFromSpirv(v, f);
    public IGpuComputeShader CreateComputeShaderFromSpirv(string c) => _inner.Factory.CreateComputeShaderFromSpirv(c);
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => _inner.Factory.CreateGraphicsPipeline(d);
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => _inner.Factory.CreateComputePipeline(d);
    public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
    {
        if (!completion.Signaled) return false;
        ((SubmissionTestBuffer)staging).Bytes.AsSpan((int)offsetBytes, destination.Length).CopyTo(destination);
        return true;
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> d) where T : unmanaged => _inner.UpdateBuffer(b, o, d);
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] d) where T : unmanaged => _inner.UpdateBuffer(b, o, d);
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T d) where T : unmanaged => _inner.UpdateBuffer(b, o, d);
    public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h) => _inner.UpdateTexture(t, d, x, y, w, h);
    public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h, uint m, uint l) => _inner.UpdateTexture(t, d, x, y, w, h, m, l);
    public MappedData Map(IGpuTexture t, GpuMapMode m) => throw new NotSupportedException();
    public MappedData Map(IGpuBuffer b, GpuMapMode m) => throw new NotSupportedException();
    public void Unmap(IGpuTexture t) => throw new NotSupportedException();
    public void Unmap(IGpuBuffer b) => throw new NotSupportedException();
    public void ResizeSwapchain(uint w, uint h) => throw new NotSupportedException();
    public void Present() => Trace?.Invoke("present");
    public void Dispose() => _inner.Dispose();
}

internal sealed class GpuPerfTimestampTestCommands : IGpuCommandList, IGpuCommandListTimestampEnd
{
    GpuPerfTimestampTestPool? _pool;
    internal GpuPerfTimestampTestPool? Pool => _pool;
    internal Action<string>? Trace { get; set; }
    uint _tail;
    bool _armed;
    public void Begin() { Trace?.Invoke("begin"); _pool = null; _armed = false; }
    public void BeginTimestamps(IGpuTimestampPool pool, uint count) { _pool = (GpuPerfTimestampTestPool)pool; _pool.Begin(count); }
    public void WriteTimestamp(IGpuTimestampPool pool, uint index) { Trace?.Invoke("marker:" + index); ((GpuPerfTimestampTestPool)pool).Write(index); }
    public void EndTimestamps(IGpuTimestampPool pool) => ((GpuPerfTimestampTestPool)pool).End();
    public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
    {
        if (!ReferenceEquals(pool, _pool) || _armed || _pool.Written != finalIndex || finalIndex != _pool.Count - 1)
            throw new InvalidOperationException("Incorrect timestamp tail.");
        _tail = finalIndex; _armed = true;
    }
    public void End() { Trace?.Invoke("native.finalize"); if (_armed) { Trace?.Invoke("marker:" + _tail); _pool!.Write(_tail); _pool.End(); _armed = false; } }
    public void SetFramebuffer(IGpuFramebuffer fb) => Trace?.Invoke("bind");
    public void ClearColorTarget(uint index, Color rgba) => Trace?.Invoke("clear");
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
    public void Draw(uint vertexCount) => Trace?.Invoke("draw");
    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
    public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
        => ((SubmissionTestBuffer)src).Bytes.AsSpan((int)srcOffsetBytes, (int)sizeInBytes).CopyTo(((SubmissionTestBuffer)dst).Bytes.AsSpan((int)dstOffsetBytes));
    public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
        IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
    { }
    public void GenerateMipmaps(IGpuTexture texture) { }
    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
    public void SetComputePipeline(IGpuComputePipeline p) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
    public void Dispose() { }
}
