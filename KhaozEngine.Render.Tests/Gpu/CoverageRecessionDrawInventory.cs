using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Counts requested graphics commands at their innermost scene pass, without a device execution.
/// Changes compare logical arguments across the recording, not backend encoder state or native bind costs.</summary>
internal sealed class CoverageRecessionDrawInventory(CoverageRecessionObservedDevice device) : IGpuCommandList, IRenderPassObserver
{
    internal sealed class DrawCount
    {
        public int DrawCalls { get; set; }
        public ulong SubmittedElements { get; set; }
    }
    internal readonly record struct DrawKind(bool Indexed, bool Coverage, bool VertexPreciseMath);
    internal sealed class PassCount
    {
        internal readonly Dictionary<DrawKind, DrawCount> Draws = [];
        public int PipelineSets { get; set; }
        public int PipelineChanges { get; set; }
        public int ResourceSetBinds { get; set; }
        public int ResourceSetChanges { get; set; }
        public int VertexBufferBinds { get; set; }
        public int IndexBufferBinds { get; set; }
    }
    internal readonly Dictionary<RenderPassKey, PassCount> Passes = [];
    readonly Stack<RenderPassKey> stack = [];
    readonly Dictionary<uint, (IGpuResourceSet, uint)> bindings = [];
    IGpuPipeline? pipeline;
    PassCount Current => Count(stack.TryPeek(out var key) ? key : new("unscoped"));
    PassCount Count(RenderPassKey key)
    {
        if (!Passes.TryGetValue(key, out var count)) Passes.Add(key, count = new());
        return count;
    }
    public void BeginPass(IGpuCommandList list, RenderPassKey key) { Count(key); stack.Push(key); }
    public void EndPass(IGpuCommandList list, RenderPassKey key)
    { if (stack.Pop() != key) throw new InvalidOperationException("Inventory scopes are unbalanced."); }
    public void SkipPass(IGpuCommandList list, RenderPassKey key) => Count(key);
    public void BeginCpuPhase(string id) { }
    public void EndCpuPhase(string id) { }
    public void Begin() { Passes.Clear(); stack.Clear(); bindings.Clear(); pipeline = null; }
    public void End() { if (stack.Count != 0) throw new InvalidOperationException("Inventory scopes are unbalanced."); }
    public void SetPipeline(IGpuPipeline value)
    { Current.PipelineSets++; if (!ReferenceEquals(pipeline, value)) Current.PipelineChanges++; pipeline = value; }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => SetGraphicsResourceSet(slot, set, 0);
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint offset)
    {
        Current.ResourceSetBinds++;
        if (!bindings.TryGetValue(slot, out var previous) || previous != (set, offset)) Current.ResourceSetChanges++;
        bindings[slot] = (set, offset);
    }
    void DrawElements(uint count, uint instances, bool indexed)
    {
        var policy = device.DrawPolicy(pipeline ?? throw new InvalidOperationException("An inventory draw has no pipeline."));
        var kind = new DrawKind(indexed, policy.Coverage, policy.Precise);
        if (!Current.Draws.TryGetValue(kind, out var draws)) Current.Draws.Add(kind, draws = new());
        draws.DrawCalls++; draws.SubmittedElements += (ulong)count * instances;
    }
    public void Draw(uint count, uint instances, uint start, uint first) => DrawElements(count, instances, false);
    public void Draw(uint count) => DrawElements(count, 1, false);
    public void DrawIndexed(uint count, uint instances, uint start, int offset, uint first) => DrawElements(count, instances, true);
    public void SetVertexBuffer(uint slot, IGpuBuffer b) => Current.VertexBufferBinds++;
    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offset) => Current.VertexBufferBinds++;
    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat format) => Current.IndexBufferBinds++;
    public void SetFramebuffer(IGpuFramebuffer framebuffer) { }
    public void ClearColorTarget(uint index, Color rgba) { }
    public void ClearDepthStencil(float depth) { }
    public void SetScissorRect(uint index, uint x, uint y, uint width, uint height) { }
    public void SetFullScissorRects() { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged { }
    public void CopyBuffer(IGpuBuffer src, uint srcOffset, IGpuBuffer dst, uint dstOffset, uint count) { }
    public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
    public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint width, uint height) { }
    public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint dstMip, uint dstLayer, uint width, uint height) { }
    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
    public void GenerateMipmaps(IGpuTexture texture) { }
    public void SetComputePipeline(IGpuComputePipeline value) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) { }
    public void Dispatch(uint x, uint y, uint z) { }
    public void Dispose() { }
}
