using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal class RendererPerfCommandList : IGpuCommandList
{
    readonly RendererPerfRecorder _recorder;
    readonly Binding[] _graphics = new Binding[256], _compute = new Binding[256], _vertices = new Binding[256];
    readonly Scissor[] _scissors = new Scissor[256];
    IGpuFramebuffer? _framebuffer;
    IGpuPipeline? _pipeline;
    IGpuComputePipeline? _computePipeline;
    Binding _index;
    bool _fullScissors;
    RendererPerfRecorder.ResourceFacts _graphicsFacts, _computeFacts;
    internal IGpuCommandList Native { get; }
    RendererPerfCommandList(IGpuCommandList native, RendererPerfRecorder recorder) { Native = native; _recorder = recorder; }

    internal static IGpuCommandList Wrap(IGpuCommandList native, RendererPerfRecorder recorder)
    {
        if (!recorder.Enabled && recorder.Capture is null) return native;
        int flags = (native is IGpuCommandListTimestampEnd ? 1 : 0) | (native is IVulkanRenderingScope ? 2 : 0)
            | (native is ID3D11SwapchainBindTracking ? 4 : 0);
        return flags switch
        {
            1 => new Cap1(native, recorder),
            2 => new Cap2(native, recorder),
            3 => new Cap3(native, recorder),
            4 => new Cap4(native, recorder),
            5 => new Cap5(native, recorder),
            6 => new Cap6(native, recorder),
            7 => new Cap7(native, recorder),
            _ => new RendererPerfCommandList(native, recorder)
        };
    }
    internal static IGpuCommandList Unwrap(IGpuCommandList commands) => commands is RendererPerfCommandList wrapper ? wrapper.Native : commands;
    void Count(RendererPerfCounter counter) => _recorder.Count(counter);
    public void Begin()
    {
        _recorder.BeginCpuPhase("begin.acquire");
        try
        {
            Native.Begin();
            _framebuffer = null; _pipeline = null; _computePipeline = null; _index = default;
            _graphicsFacts = _computeFacts = default; _fullScissors = false;
            Array.Clear(_graphics); Array.Clear(_compute); Array.Clear(_vertices); Array.Clear(_scissors);
            _recorder.BeginCommands(Native);
        }
        finally { _recorder.EndCpuPhase("begin.acquire"); }
    }
    public void End()
    {
        _recorder.BeginCpuPhase("command-list.end");
        try { _recorder.EndCommands(Native); Native.End(); }
        finally { _recorder.EndCpuPhase("command-list.end"); }
    }
    public void Dispose() => Native.Dispose();
    public void SetFramebuffer(IGpuFramebuffer fb)
    {
        Count(RendererPerfCounter.FramebufferBinds);
        if (ReferenceEquals(_framebuffer, fb)) Count(RendererPerfCounter.RedundantFramebuffers);
        else { _fullScissors = false; Array.Clear(_scissors); }
        _framebuffer = fb; _recorder.TargetFacts(fb); Native.SetFramebuffer(fb);
    }
    public void ClearColorTarget(uint index, Color rgba) { Count(RendererPerfCounter.ColorClears); Native.ClearColorTarget(index, rgba); }
    public void ClearDepthStencil(float depth) { Count(RendererPerfCounter.DepthClears); Native.ClearDepthStencil(depth); }
    public void SetPipeline(IGpuPipeline pipeline)
    {
        Count(RendererPerfCounter.GraphicsPipelineBinds);
        if (ReferenceEquals(_pipeline, pipeline)) Count(RendererPerfCounter.RedundantGraphicsPipelines);
        _pipeline = pipeline; _graphicsFacts = _recorder.PipelineFacts(pipeline, RendererPerfPipelineKind.Graphics); Native.SetPipeline(pipeline);
    }
    public void SetComputePipeline(IGpuComputePipeline pipeline)
    {
        Count(RendererPerfCounter.ComputePipelineBinds);
        if (ReferenceEquals(_computePipeline, pipeline)) Count(RendererPerfCounter.RedundantComputePipelines);
        _computePipeline = pipeline; _computeFacts = _recorder.PipelineFacts(pipeline, RendererPerfPipelineKind.Compute); Native.SetComputePipeline(pipeline);
    }
    void Bind(Binding[] bindings, uint slot, object resource, uint offset, RendererPerfCounter raw, RendererPerfCounter redundant)
    {
        Count(raw);
        if (slot >= bindings.Length) { _recorder.Fail("A binding slot exceeds the fixed trace capacity."); return; }
        var previous = bindings[slot];
        if (ReferenceEquals(previous.Resource, resource) && previous.Offset == offset) Count(redundant);
        bindings[slot] = new(resource, offset);
    }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
    {
        Bind(_graphics, slot, set, 0, RendererPerfCounter.GraphicsResources, RendererPerfCounter.RedundantGraphicsResources);
        Native.SetGraphicsResourceSet(slot, set);
    }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
    {
        Bind(_graphics, slot, set, dynamicOffset, RendererPerfCounter.GraphicsResources, RendererPerfCounter.RedundantGraphicsResources);
        Native.SetGraphicsResourceSet(slot, set, dynamicOffset);
    }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
    {
        Bind(_compute, slot, set, 0, RendererPerfCounter.ComputeResources, RendererPerfCounter.RedundantComputeResources);
        Native.SetComputeResourceSet(slot, set);
    }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
    {
        Bind(_compute, slot, set, dynamicOffset, RendererPerfCounter.ComputeResources, RendererPerfCounter.RedundantComputeResources);
        Native.SetComputeResourceSet(slot, set, dynamicOffset);
    }
    public void SetVertexBuffer(uint slot, IGpuBuffer buffer)
    {
        Bind(_vertices, slot, buffer, 0, RendererPerfCounter.VertexBuffers, RendererPerfCounter.RedundantVertexBuffers);
        Native.SetVertexBuffer(slot, buffer);
    }
    public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
    {
        Bind(_vertices, slot, buffer, offsetBytes, RendererPerfCounter.VertexBuffers, RendererPerfCounter.RedundantVertexBuffers);
        Native.SetVertexBuffer(slot, buffer, offsetBytes);
    }
    public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
    {
        Count(RendererPerfCounter.IndexBuffers);
        if (ReferenceEquals(_index.Resource, buffer) && _index.Offset == (uint)format) Count(RendererPerfCounter.RedundantIndexBuffers);
        _index = new(buffer, (uint)format); Native.SetIndexBuffer(buffer, format);
    }
    public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
    {
        Count(RendererPerfCounter.Scissors);
        var value = new Scissor(true, x, y, width, height);
        if (index >= _scissors.Length) _recorder.Fail("A scissor slot exceeds the fixed trace capacity.");
        else
        {
            if (_scissors[index] == value) Count(RendererPerfCounter.RedundantScissors);
            _scissors[index] = value;
        }
        _fullScissors = false; Native.SetScissorRect(index, x, y, width, height);
    }
    public void SetFullScissorRects()
    {
        Count(RendererPerfCounter.FullScissors);
        if (_fullScissors) Count(RendererPerfCounter.RedundantFullScissors);
        _fullScissors = true; Array.Clear(_scissors); Native.SetFullScissorRects();
    }
    void DrawCount(uint count, uint instances, bool indexed)
    {
        Count(indexed ? RendererPerfCounter.IndexedDraws : RendererPerfCounter.Draws);
        _recorder.Count(indexed ? RendererPerfCounter.Indices : RendererPerfCounter.Vertices, checked((long)count * instances));
        _recorder.Count(RendererPerfCounter.Instances, instances);
        if (_graphicsFacts.Topology is not { } topology) { Count(RendererPerfCounter.UnknownTopologyDraws); return; }
        long triangles = topology switch
        {
            GpuPrimitiveTopology.TriangleList => count / 3,
            GpuPrimitiveTopology.TriangleStrip => count > 2 ? count - 2 : 0,
            _ => 0
        };
        _recorder.Count(RendererPerfCounter.Triangles, checked(triangles * instances));
    }
    public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
    { DrawCount(vertexCount, instanceCount, false); Native.Draw(vertexCount, instanceCount, vertexStart, instanceStart); }
    public void Draw(uint vertexCount) { DrawCount(vertexCount, 1, false); Native.Draw(vertexCount); }
    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
    { DrawCount(indexCount, instanceCount, true); Native.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart); }
    public void Dispatch(uint x, uint y, uint z)
    {
        Count(RendererPerfCounter.Dispatches); _recorder.DispatchFacts(x, y, z, _computeFacts);
        long groups = checked((long)x * y * z);
        _recorder.Count(RendererPerfCounter.DispatchGroups, groups);
        _recorder.Count(RendererPerfCounter.DispatchGroupsX, x); _recorder.Count(RendererPerfCounter.DispatchGroupsY, y); _recorder.Count(RendererPerfCounter.DispatchGroupsZ, z);
        if (_computeFacts.LocalX == 0 || _computeFacts.LocalY == 0 || _computeFacts.LocalZ == 0) Count(RendererPerfCounter.UnknownLocalSizeDispatches);
        else _recorder.Count(RendererPerfCounter.DispatchInvocations, checked(groups * _computeFacts.LocalX * _computeFacts.LocalY * _computeFacts.LocalZ));
        Native.Dispatch(x, y, z);
    }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, in T data) where T : unmanaged
    {
        _recorder.BeginUpload(RendererPerfRecorder.UploadBytes<T>(1), false);
        try { Native.UpdateBuffer(buffer, offsetBytes, in data); } finally { _recorder.EndUpload(); }
    }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        _recorder.BeginUpload(RendererPerfRecorder.UploadBytes<T>(data.Length), false);
        try { Native.UpdateBuffer(buffer, offsetBytes, data); } finally { _recorder.EndUpload(); }
    }
    public void CopyBuffer(IGpuBuffer source, uint sourceOffset, IGpuBuffer destination, uint destinationOffset, uint bytes)
    { Count(RendererPerfCounter.BufferCopies); _recorder.CopyBytes(bytes); Native.CopyBuffer(source, sourceOffset, destination, destinationOffset, bytes); }
    public void CopyTexture(IGpuTexture source, IGpuTexture destination)
    { Count(RendererPerfCounter.TextureCopies); _recorder.CopyBytes(_recorder.TextureBytes(source)); Native.CopyTexture(source, destination); }
    void CopyRegion(IGpuTexture source, uint width, uint height)
    {
        Count(RendererPerfCounter.TextureSubresourceCopies);
        _recorder.CopyBytes(checked((long)width * height * source.SampleCount * RendererPerfRecorder.BytesPerPixel(source.Format)));
    }
    public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture destination, uint width, uint height)
    { CopyRegion(source, width, height); Native.CopyTextureSubresource(source, sourceMip, sourceLayer, destination, width, height); }
    public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture destination,
        uint destinationMip, uint destinationLayer, uint width, uint height)
    { CopyRegion(source, width, height); Native.CopyTextureSubresource(source, sourceMip, sourceLayer, destination, destinationMip, destinationLayer, width, height); }
    public void ResolveTexture(IGpuTexture source, IGpuTexture destination)
    {
        Count(RendererPerfCounter.Resolves);
        long bytes = checked((long)source.Width * source.Height * RendererPerfRecorder.BytesPerPixel(source.Format));
        _recorder.Count(RendererPerfCounter.ResolveReadBytes, checked(bytes * source.SampleCount));
        _recorder.Count(RendererPerfCounter.ResolveWriteBytes, bytes); Native.ResolveTexture(source, destination);
    }
    public void GenerateMipmaps(IGpuTexture texture) { Count(RendererPerfCounter.MipGenerations); Native.GenerateMipmaps(texture); }
    public void BeginTimestamps(IGpuTimestampPool pool, uint count) => Native.BeginTimestamps(pool, count);
    public void WriteTimestamp(IGpuTimestampPool pool, uint index) => Native.WriteTimestamp(pool, index);
    public void EndTimestamps(IGpuTimestampPool pool) => Native.EndTimestamps(pool);
    public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex) => ((IGpuCommandListTimestampEnd)Native).FinishTimestampsOnEnd(pool, finalIndex);
    public void EndActiveRendering() => ((IVulkanRenderingScope)Native).EndActiveRendering();
    public bool BoundSwapchainFramebuffer => ((ID3D11SwapchainBindTracking)Native).BoundSwapchainFramebuffer;

    readonly record struct Binding(object? Resource, uint Offset);
    readonly record struct Scissor(bool Known, uint X, uint Y, uint Width, uint Height);
    sealed class Cap1(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IGpuCommandListTimestampEnd;
    sealed class Cap2(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IVulkanRenderingScope;
    sealed class Cap3(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IGpuCommandListTimestampEnd, IVulkanRenderingScope;
    sealed class Cap4(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), ID3D11SwapchainBindTracking;
    sealed class Cap5(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IGpuCommandListTimestampEnd, ID3D11SwapchainBindTracking;
    sealed class Cap6(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IVulkanRenderingScope, ID3D11SwapchainBindTracking;
    sealed class Cap7(IGpuCommandList native, RendererPerfRecorder recorder) : RendererPerfCommandList(native, recorder), IGpuCommandListTimestampEnd, IVulkanRenderingScope, ID3D11SwapchainBindTracking;
}
