using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render2D;

internal sealed class SpriteBatchMaskedTestRig : IDisposable
{
    internal static readonly GpuOutputDescription Output = new(null, new[] { GpuPixelFormat.R8G8B8A8UNorm });
    internal FakeGpuDevice InnerDevice { get; } = new();
    internal RecordingDevice Device { get; }
    internal ForwardingFactory Forwarder { get; }
    internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)InnerDevice.Factory;
    internal SpriteBatch Batch { get; }
    internal RecordingCommands Commands { get; } = new();
    internal List<VertexUpload> VertexUploads => Device.Uploads;
    internal List<DrawBinding> Draws => Commands.Draws;

    SpriteBatchMaskedTestRig()
    {
        Forwarder = new(Factory);
        Device = new(InnerDevice, Forwarder);
        Batch = new(Device, Output);
    }

    internal static SpriteBatchMaskedTestRig Create() => new();

    internal static Texture2D Sprite() => Texture2D.Wrap(new FakeTexture(4, 4, 1, 1,
        GpuPixelFormat.R8G8B8A8UNorm), 4, 4);

    internal static RecordedVertex[] Vertices(VertexUpload upload) =>
        MemoryMarshal.Cast<byte, RecordedVertex>(upload.Data).ToArray();

    // Grouping fixtures encode submission indices in each quad's X position.
    internal static int[] OriginalQuadIndices(VertexUpload upload) => Vertices(upload)
        .Where((_, index) => index % 6 == 0).Select(vertex => (int)vertex.Pos.X).ToArray();

    internal static int[] OriginalQuadIndices(DrawBinding draw, IEnumerable<VertexUpload> uploads)
    {
        uint start = draw.VertexBufferOffset + draw.VertexStart * 64;
        uint end = start + draw.VertexCount * 64;
        // A grouped draw spans multiple source-run uploads in consecutive buffer ranges.
        return uploads.Where(upload => ReferenceEquals(upload.Buffer, draw.VertexBuffer) &&
                upload.Offset >= start && upload.Offset < end)
            .OrderBy(upload => upload.Offset).SelectMany(OriginalQuadIndices).ToArray();
    }

    internal string ShaderName(DrawBinding draw)
    {
        string fragment = ((FakePipeline)draw.Pipeline!).Request!.Value.FragmentGlsl;
        if (fragment == Factory.ShaderRequests[0].FragmentGlsl) return "Sprite2D";
        if (fragment == MaskedSpriteShader.FragmentSource) return "Sprite2DMasked";
        throw new InvalidOperationException("Unexpected sprite fragment shader.");
    }

    internal MaskedSpriteBatchResources? MaskedOwner =>
        (MaskedSpriteBatchResources?)typeof(SpriteBatch).GetField("_maskedResources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Batch);

    object Runs => typeof(SpriteBatch).GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Batch)!;

    internal bool HasGroup(object key) => (bool)Runs.GetType()
        .GetMethod("HasGroup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Runs, new[] { key })!;

    internal IReadOnlyList<object> GroupOrder => (IReadOnlyList<object>)Runs.GetType()
        .GetField("_groupOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runs)!;

    internal MaskedSpriteBatchResources EnsureMaskedResources()
    {
        try
        {
            return (MaskedSpriteBatchResources)typeof(SpriteBatch)
                .GetMethod("EnsureMaskedResources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Batch, null)!;
        }
        catch (TargetInvocationException error) when (error.InnerException is { } original)
        {
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    internal void Frame(Action<SpriteBatch>? draw = null, SamplerMode sourceSampler = SamplerMode.Linear, bool group = false)
    {
        VertexUploads.Clear();
        Draws.Clear();
        Commands.UniformUploads.Clear();
        using var recording = GpuRecording.Open(Device, Commands, "masked sprite test frame");
        Batch.NewFrame(Commands, 64, 64);
        Batch.Begin(sourceSampler);
        Batch.GroupByTexture = group;
        draw?.Invoke(Batch);
        Batch.End();
    }

    public void Dispose()
    {
        Batch.Dispose();
        Commands.Dispose();
        Device.Dispose();
    }

    internal enum ConstructionFailure { None, Layout, Shader, FirstPipeline, SecondPipeline }
    internal readonly record struct VertexUpload(IGpuBuffer Buffer, uint Offset, byte[] Data);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct RecordedVertex(Vector2 Pos, Vector2 Uv, Vector4 Color,
        Vector2 Local, Vector4 Shape, Vector2 Mode);
    internal readonly record struct DrawBinding(IGpuPipeline? Pipeline, IGpuResourceSet? Set0,
        IGpuResourceSet? Set1, uint DynamicOffset, IGpuBuffer? VertexBuffer,
        uint VertexBufferOffset, uint VertexCount, uint InstanceCount, uint VertexStart, uint InstanceStart);

    internal sealed class TrackedLayout(IGpuResourceLayout inner, GpuResourceLayoutDescription description) : IGpuResourceLayout
    {
        internal GpuResourceLayoutDescription Description { get; } = description;
        internal bool Disposed { get; private set; }
        internal Exception? DisposalFailure { get; set; }
        internal Action? OnDispose { get; set; }
        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
            OnDispose?.Invoke();
            if (DisposalFailure is { } error) throw error;
        }
    }

    internal sealed class TrackedSampler(IGpuSampler inner) : IGpuSampler
    {
        internal bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    internal sealed class ForwardingFactory(IGpuResourceFactory inner, bool recordCommands = false) : IGpuResourceFactory
    {
        int _pipelineRequests;
        ConstructionFailure _failure;
        internal ConstructionFailure Failure
        {
            get => _failure;
            set { _failure = value; _pipelineRequests = 0; }
        }
        internal Exception CreationFailure { get; } = new InvalidOperationException("planned masked construction failure");
        internal Exception? LayoutDisposalFailure { get; set; }
        internal List<TrackedLayout> Layouts { get; } = new();
        internal int CommandListRequests { get; private set; }
        internal List<RecordingCommands> CommandLists { get; } = new();

        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
        {
            if (Failure == ConstructionFailure.Layout) throw CreationFailure;
            // Native factories require their own layout handles in pipeline and descriptor creation.
            if (inner is not FakeGpuResourceFactory) return inner.CreateResourceLayout(d);
            var layout = new TrackedLayout(inner.CreateResourceLayout(d), d) { DisposalFailure = LayoutDisposalFailure };
            Layouts.Add(layout);
            return layout;
        }
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            if (Failure == ConstructionFailure.Shader) throw CreationFailure;
            return inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
        }
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
        {
            _pipelineRequests++;
            if ((Failure == ConstructionFailure.FirstPipeline && _pipelineRequests == 1) ||
                (Failure == ConstructionFailure.SecondPipeline && _pipelineRequests == 2)) throw CreationFailure;
            return inner.CreateGraphicsPipeline(d);
        }
        public IGpuCommandList CreateCommandList()
        {
            CommandListRequests++;
            var commands = inner.CreateCommandList();
            if (!recordCommands) return commands;
            var recorded = new RecordingCommands(commands);
            CommandLists.Add(recorded);
            return recorded;
        }
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => inner.CreateBuffer(d);
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => inner.CreateTexture(d);
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => inner.CreateFramebuffer(depth, colour);
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => inner.CreateSampler(d);
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => inner.CreateResourceSet(d);
        public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => inner.CreateComputeShaderFromSpirv(source);
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => inner.CreateComputePipeline(d);
        public IGpuFence CreateFence() => inner.CreateFence();
    }

    internal sealed class RecordingDevice : IGpuDevice
    {
        readonly IGpuDevice _inner;
        readonly bool _ownsDevice;
        internal RecordingDevice(FakeGpuDevice inner, IGpuResourceFactory factory) : this((IGpuDevice)inner, factory)
        {
            _ownsDevice = true;
            PointSampler = new TrackedSampler(inner.PointSampler);
            LinearSampler = new TrackedSampler(inner.LinearSampler);
        }
        // Live devices keep their native sampler handles and remain owned by the GPU context.
        internal RecordingDevice(IGpuDevice inner, IGpuResourceFactory factory)
        {
            _inner = inner;
            Factory = factory;
            PointSampler = inner.PointSampler;
            LinearSampler = inner.LinearSampler;
        }
        internal List<VertexUpload> Uploads { get; } = new();
        internal int MapCalls { get; private set; }
        internal int TextureUploadCalls { get; private set; }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory { get; }
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler { get; }
        public IGpuSampler LinearSampler { get; }
        public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }
        static IGpuCommandList Native(IGpuCommandList cl) => cl is RecordingCommands recorded ? recorded.Inner : cl;
        public void Submit(IGpuCommandList cl) => _inner.Submit(Native(cl));
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(Native(cl), fence);
        public void WaitForIdle() => _inner.WaitForIdle();
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            Uploads.Add(new(b, offsetBytes, MemoryMarshal.AsBytes(data).ToArray()));
            _inner.UpdateBuffer(b, offsetBytes, data);
        }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged =>
            UpdateBuffer(b, offsetBytes, (ReadOnlySpan<T>)data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged =>
            UpdateBuffer(b, offsetBytes, new ReadOnlySpan<T>(in data));
        public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h)
        { TextureUploadCalls++; _inner.UpdateTexture(t, data, x, y, w, h); }
        public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer)
        { TextureUploadCalls++; _inner.UpdateTexture(t, data, x, y, w, h, mip, layer); }
        public MappedData Map(IGpuTexture t, GpuMapMode mode) { MapCalls++; return _inner.Map(t, mode); }
        public MappedData Map(IGpuBuffer b, GpuMapMode mode) { MapCalls++; return _inner.Map(b, mode); }
        public void Unmap(IGpuTexture t) => _inner.Unmap(t);
        public void Unmap(IGpuBuffer b) => _inner.Unmap(b);
        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();
        public void Dispose() { if (_ownsDevice) _inner.Dispose(); }
    }

    internal sealed class RecordingCommands(IGpuCommandList? inner = null) : IGpuCommandList
    {
        readonly IGpuCommandList _inner = inner ?? new NullGpuCommandList();
        internal IGpuCommandList Inner => _inner;
        IGpuPipeline? _pipeline;
        IGpuResourceSet? _set0, _set1;
        IGpuBuffer? _vertexBuffer;
        uint _dynamicOffset, _vertexBufferOffset;
        internal List<DrawBinding> Draws { get; } = new();
        internal List<VertexUpload> UniformUploads { get; } = new();
        internal int CopyOperations { get; private set; }
        public void SetPipeline(IGpuPipeline p) { _pipeline = p; _inner.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => SetGraphicsResourceSet(slot, set, 0);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            if (slot == 0) _set0 = set;
            if (slot == 1) { _set1 = set; _dynamicOffset = dynamicOffset; }
            _inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => SetVertexBuffer(slot, b, 0);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
        {
            if (slot == 0) { _vertexBuffer = b; _vertexBufferOffset = offsetBytes; }
            _inner.SetVertexBuffer(slot, b, offsetBytes);
        }
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            Draws.Add(new(_pipeline, _set0, _set1, _dynamicOffset, _vertexBuffer, _vertexBufferOffset,
                vertexCount, instanceCount, vertexStart, instanceStart));
            _inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        }
        public void Draw(uint vertexCount) => Draw(vertexCount, 1, 0, 0);
        public void Begin()
        {
            _pipeline = null; _set0 = null; _set1 = null; _vertexBuffer = null;
            _dynamicOffset = 0; _vertexBufferOffset = 0;
            _inner.Begin();
        }
        public void End() => _inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => _inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color rgba) => _inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => _inner.ClearDepthStencil(depth);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => _inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => _inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => _inner.SetFullScissorRects();
        public void DrawIndexed(uint count, uint instances, uint start, int offset, uint instanceStart) =>
            _inner.DrawIndexed(count, instances, start, offset, instanceStart);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged =>
            UpdateBuffer(b, offset, new ReadOnlySpan<T>(in data));
        public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            UniformUploads.Add(new(b, offset, MemoryMarshal.AsBytes(data).ToArray()));
            _inner.UpdateBuffer(b, offset, data);
        }
        public void CopyBuffer(IGpuBuffer src, uint srcOffset, IGpuBuffer dst, uint dstOffset, uint bytes)
        { CopyOperations++; _inner.CopyBuffer(src, srcOffset, dst, dstOffset, bytes); }
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        { CopyOperations++; _inner.CopyTexture(src, dst); }
        public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint w, uint h)
        { CopyOperations++; _inner.CopyTextureSubresource(src, mip, layer, dst, w, h); }
        public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint dstMip, uint dstLayer, uint w, uint h)
        { CopyOperations++; _inner.CopyTextureSubresource(src, mip, layer, dst, dstMip, dstLayer, w, h); }
        public void GenerateMipmaps(IGpuTexture t) => _inner.GenerateMipmaps(t);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { CopyOperations++; _inner.ResolveTexture(src, dst); }
        public void SetComputePipeline(IGpuComputePipeline p) => _inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => _inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) => _inner.SetComputeResourceSet(slot, set, offset);
        public void Dispatch(uint x, uint y, uint z) => _inner.Dispatch(x, y, z);
        public void Dispose() => _inner.Dispose();
    }
}
