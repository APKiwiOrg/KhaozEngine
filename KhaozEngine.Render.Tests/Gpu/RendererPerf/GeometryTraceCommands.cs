using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Complete logical list trace. Resource IDs preserve aliasing, payload hashes preserve upload data.</summary>
internal sealed class GeometryTraceCommands(IGpuCommandList inner) : IGpuCommandList
{
    internal readonly record struct Event(string Op, int A, int B, ulong X, ulong Y, ulong Z, ulong W,
        ulong Data, IGpuPipeline? Pipeline, RenderPassKey? Owner);
    internal readonly List<Event> Events = new(4096);
    readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    IGpuPipeline? _pipeline;
    internal RenderPassKey? Owner;
    internal bool Capture = true;
    internal Exception? Failure;
    internal string? FailureVertex;
    int Id(object value)
    {
        if (!_ids.TryGetValue(value, out int id)) { id = _ids.Count + 1; _ids.Add(value, id); }
        return id;
    }
    internal object Resource(int id)
    {
        foreach (var pair in _ids)
            if (pair.Value == id) return pair.Key;
        throw new InvalidOperationException("The trace resource ID is not in this recording.");
    }
    void Note(string op, object? a = null, object? b = null, ulong x = 0, ulong y = 0, ulong z = 0, ulong w = 0, ulong data = 0)
    {
        if (Failure is { } failure && op.StartsWith("draw", StringComparison.Ordinal)
            && (FailureVertex is null || _pipeline is FakePipeline { Request: { } request } && request.VertexGlsl == FailureVertex)) throw failure;
        if (Capture) Events.Add(new(op, a is null ? 0 : Id(a), b is null ? 0 : Id(b), x, y, z, w, data, _pipeline, Owner));
    }
    static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes) hash = unchecked((hash ^ b) * 1099511628211UL);
        return hash;
    }
    internal void DeviceBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        => Note("device-buffer", buffer, x: offset, y: (uint)MemoryMarshal.AsBytes(data).Length,
            data: Capture ? Hash(MemoryMarshal.AsBytes(data)) : 0);
    internal void DeviceTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
        uint mip = 0, uint layer = 0)
        => Note("device-texture", texture, x: ((ulong)x << 32) | y, y: ((ulong)width << 32) | height,
            z: mip, w: layer, data: Capture ? Hash(data) : 0);
    public void Begin() { _ids.Clear(); _pipeline = null; Note("begin"); inner.Begin(); }
    public void End() { Note("end"); inner.End(); }
    public void SetFramebuffer(IGpuFramebuffer fb) { Note("framebuffer", fb); inner.SetFramebuffer(fb); }
    public void ClearColorTarget(uint index, Color rgba)
    { Note("clear-color", x: index, data: Hash(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in rgba, 1)))); inner.ClearColorTarget(index, rgba); }
    public void ClearDepthStencil(float depth) { Note("clear-depth", x: BitConverter.SingleToUInt32Bits(depth)); inner.ClearDepthStencil(depth); }
    public void SetPipeline(IGpuPipeline p)
    {
        _pipeline = p; Note("pipeline", p, data: Capture && p is FakePipeline { Request: { } request }
            ? Hash(MemoryMarshal.AsBytes(request.VertexGlsl.AsSpan())) ^ Hash(MemoryMarshal.AsBytes(request.FragmentGlsl.AsSpan())) : 0); inner.SetPipeline(p);
    }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { Note("set", set, x: slot); inner.SetGraphicsResourceSet(slot, set); }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint offset) { Note("set-offset", set, x: slot, y: offset); inner.SetGraphicsResourceSet(slot, set, offset); }
    public void SetVertexBuffer(uint slot, IGpuBuffer b) { Note("vertex", b, x: slot); inner.SetVertexBuffer(slot, b); }
    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offset) { Note("vertex-offset", b, x: slot, y: offset); inner.SetVertexBuffer(slot, b, offset); }
    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { Note("index", b, x: (uint)fmt); inner.SetIndexBuffer(b, fmt); }
    public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { Note("scissor", x: index, y: ((ulong)x << 32) | y, z: w, w: h); inner.SetScissorRect(index, x, y, w, h); }
    public void SetFullScissorRects() { Note("full-scissor"); inner.SetFullScissorRects(); }
    public void Draw(uint count) { Note("draw", x: count); inner.Draw(count); }
    public void Draw(uint count, uint instances, uint start, uint first) { Note("draw-instanced", x: count, y: instances, z: start, w: first); inner.Draw(count, instances, start, first); }
    public void DrawIndexed(uint count, uint instances, uint start, int offset, uint first)
    { Note("draw-indexed", x: count, y: instances, z: ((ulong)start << 32) | unchecked((uint)offset), w: first); inner.DrawIndexed(count, instances, start, offset, first); }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged
    { Note("update", b, x: offset, y: (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), data: Capture ? Hash(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1))) : 0); inner.UpdateBuffer(b, offset, in data); }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged
    { Note("update-span", b, x: offset, y: (uint)MemoryMarshal.AsBytes(data).Length, data: Capture ? Hash(MemoryMarshal.AsBytes(data)) : 0); inner.UpdateBuffer(b, offset, data); }
    public void CopyBuffer(IGpuBuffer a, uint ao, IGpuBuffer b, uint bo, uint bytes) { Note("copy-buffer", a, b, ao, bo, bytes); inner.CopyBuffer(a, ao, b, bo, bytes); }
    public void CopyTexture(IGpuTexture a, IGpuTexture b) { Note("copy-texture", a, b); inner.CopyTexture(a, b); }
    public void CopyTextureSubresource(IGpuTexture a, uint m, uint l, IGpuTexture b, uint w, uint h)
    { Note("copy-subresource", a, b, m, l, w, h); inner.CopyTextureSubresource(a, m, l, b, w, h); }
    public void CopyTextureSubresource(IGpuTexture a, uint am, uint al, IGpuTexture b, uint bm, uint bl, uint w, uint h)
    { Note("copy-subresources", a, b, ((ulong)am << 32) | al, ((ulong)bm << 32) | bl, w, h); inner.CopyTextureSubresource(a, am, al, b, bm, bl, w, h); }
    public void GenerateMipmaps(IGpuTexture texture) { Note("mips", texture); inner.GenerateMipmaps(texture); }
    public void ResolveTexture(IGpuTexture a, IGpuTexture b) { Note("resolve", a, b); inner.ResolveTexture(a, b); }
    public void SetComputePipeline(IGpuComputePipeline pipeline) { Note("compute", pipeline); inner.SetComputePipeline(pipeline); }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { Note("compute-set", set, x: slot); inner.SetComputeResourceSet(slot, set); }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) { Note("compute-set-offset", set, x: slot, y: offset); inner.SetComputeResourceSet(slot, set, offset); }
    public void Dispatch(uint x, uint y, uint z) { Note("dispatch", x: x, y: y, z: z); inner.Dispatch(x, y, z); }
    public void Dispose() => inner.Dispose();
}
