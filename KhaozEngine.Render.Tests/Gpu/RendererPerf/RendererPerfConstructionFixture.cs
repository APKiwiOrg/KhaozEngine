using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu.Performance;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfConstructionFixture : IGpuDevice, IGpuResourceFactory
{
    readonly GpuPerfTimestampTestDevice _inner = new();
    readonly FakeGpuResourceFactory _resources = new();
    internal int FactoryAttempts, ShaderAttempts, LayoutAttempts, GraphicsAttempts, ComputeAttempts, CompilerReads;
    internal int TextureAttempts;
    internal Exception? FactoryFailure;
    internal IGpuPipeline? LastGraphicsBind;
    internal IGpuComputePipeline? LastComputeBind;
    internal Action<string>? Trace;
    internal GpuCapabilities Features = new(false, true, "NVIDIA T4", supportsCompute: true, supportsCompletionFences: true)
    { SupportsTimestamps = true, MaxStorageBufferRangeBytes = 536870912 };
    internal bool Software;
    internal int Submissions => _inner.Submissions;
    internal int Waits => _inner.Waits;
    internal RendererPerfConstructionFixture() { _inner.Features = Features; _inner.Trace = op => Trace?.Invoke(op); }
    public GpuDeviceDiagnostics Diagnostics { get { ColdAction?.Invoke("diagnostics"); return new(softwareAdapter: Software); } }
    public IGpuResourceFactory Factory => this;
    public GpuBackendKind Backend => ActualBackend;
    public GpuCapabilities Capabilities => Features;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => _inner.PointSampler;
    public IGpuSampler LinearSampler => _inner.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList commands) => Submit(commands, ReadOnlySpan<IGpuFence>.Empty);
    public void Submit(IGpuCommandList commands, IGpuFence completion) => Submit(commands, [completion]);
    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        _inner.Submit(((Commands)commands).Native, completions);
    }
    public void WaitForIdle() => _inner.WaitForIdle();
    public IGpuTimestampPool CreateTimestampPool(uint capacity) => _inner.CreateTimestampPool(capacity);
    public IGpuFence CreateFence() => _inner.CreateFence();
    public IGpuCommandList CreateCommandList() => new Commands(this);
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => new SubmissionTestBuffer(d.SizeInBytes, d.Usage);
    public IGpuTexture CreateTexture(in GpuTextureDescription d)
    {
        TextureAttempts++;
        var texture = _resources.CreateTexture(d);
        if (texture is FakeTexture fake) fake.OnDispose = () => ColdAction?.Invoke("dispose.texture");
        return texture;
    }
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) { ColdAction?.Invoke("framebuffer"); return _resources.CreateFramebuffer(depth, colour); }
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => _resources.CreateResourceSet(d);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _resources.CreateSampler(d);
    void Attempt() { ColdAction?.Invoke("factory"); FactoryAttempts++; if (FactoryFailure is { } failure) throw failure; }
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
    { LayoutAttempts++; ColdAction?.Invoke("layout"); Attempt(); return new Layout((GpuResourceLayoutElement[])d.Elements.Clone()) { Owner = this }; }
    public IGpuShaderSet CreateShadersFromSpirv(string v, string f)
    { ShaderAttempts++; ColdAction?.Invoke("shader"); Attempt(); return new Shaders(v, f) { Owner = this }; }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source)
    { ShaderAttempts++; ColdAction?.Invoke("shader"); Attempt(); return new ComputeShader(source) { Owner = this }; }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
    { GraphicsAttempts++; ColdAction?.Invoke("pipeline"); Attempt(); return new GraphicsPipeline(this, Witness(d)); }
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
    { ComputeAttempts++; ColdAction?.Invoke("pipeline"); Attempt(); return new ComputePipeline(this, Witness(d)); }
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
    public void Dispose() { ColdAction?.Invoke("context.dispose"); _inner.Dispose(); }
    sealed class Commands : IGpuCommandList, IGpuCommandListTimestampEnd
    {
        readonly RendererPerfConstructionFixture owner;
        internal readonly GpuPerfTimestampTestCommands Native;
        internal Commands(RendererPerfConstructionFixture owner)
        { this.owner = owner; Native = new() { Trace = op => owner.Trace?.Invoke(op) }; }
        public void Begin() { Native.Begin(); }
        public void End() { Native.End(); }
        public void SetFramebuffer(IGpuFramebuffer fb) { Native.SetFramebuffer(fb); }
        public void ClearColorTarget(uint index, Color rgba) { Native.ClearColorTarget(index, rgba); }
        public void ClearDepthStencil(float depth) { Native.ClearDepthStencil(depth); }
        public void SetPipeline(IGpuPipeline p) { owner.LastGraphicsBind = p; Native.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { Native.SetGraphicsResourceSet(slot, set); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { Native.SetGraphicsResourceSet(slot, set, dynamicOffset); }
        public void SetVertexBuffer(uint slot, IGpuBuffer b) { Native.SetVertexBuffer(slot, b); }
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { Native.SetVertexBuffer(slot, b, offsetBytes); }
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { Native.SetIndexBuffer(b, fmt); }
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { Native.SetScissorRect(index, x, y, w, h); }
        public void SetFullScissorRects() { Native.SetFullScissorRects(); }
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { Native.Draw(vertexCount, instanceCount, vertexStart, instanceStart); }
        public void Draw(uint vertexCount) { Native.Draw(vertexCount); }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { Native.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { Native.UpdateBuffer<T>(b, offsetBytes, in data); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { Native.UpdateBuffer<T>(b, offsetBytes, data); }
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { Native.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes); }
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) { Native.CopyTexture(src, dst); }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { Native.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height); }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height) { Native.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer, width, height); }
        public void GenerateMipmaps(IGpuTexture texture) { Native.GenerateMipmaps(texture); }
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { Native.ResolveTexture(src, dst); }
        public void SetComputePipeline(IGpuComputePipeline p) { owner.LastComputeBind = p; Native.SetComputePipeline(p); }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { Native.SetComputeResourceSet(slot, set); }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { Native.SetComputeResourceSet(slot, set, dynamicOffset); }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { owner.Trace?.Invoke("dispatch"); Native.Dispatch(groupCountX, groupCountY, groupCountZ); }
        public void Dispose() { Native.Dispose(); }
        public void BeginTimestamps(IGpuTimestampPool pool, uint count) => Native.BeginTimestamps(pool, count);
        public void WriteTimestamp(IGpuTimestampPool pool, uint index) => Native.WriteTimestamp(pool, index);
        public void EndTimestamps(IGpuTimestampPool pool) => Native.EndTimestamps(pool);
        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint index) => Native.FinishTimestampsOnEnd(pool, index);
    }
    internal sealed record Layout(GpuResourceLayoutElement[] Elements) : IGpuResourceLayout
    { internal RendererPerfConstructionFixture? Owner { get; init; } public void Dispose() => Owner?.ColdAction?.Invoke("dispose.layout"); }
    sealed record Shaders(string Vertex, string Fragment) : IGpuShaderSet
    { internal RendererPerfConstructionFixture? Owner { get; init; } public void Dispose() => Owner?.ColdAction?.Invoke("dispose.shader"); }
    sealed class ComputeShader : IGpuComputeShader
    {
        internal RendererPerfConstructionFixture? Owner { get; init; }
        internal readonly string Source;
        internal ComputeShader(string source)
        {
            Source = source;
            uint Size(string axis)
            {
                var m = Regex.Match(source, @"local_size_" + axis + @"\s*=\s*(\d+)");
                return m.Success ? uint.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            }
            ThreadGroupSizeX = Size("x"); ThreadGroupSizeY = Size("y"); ThreadGroupSizeZ = Size("z");
        }
        public uint ThreadGroupSizeX { get; }
        public uint ThreadGroupSizeY { get; }
        public uint ThreadGroupSizeZ { get; }
        public void Dispose() => Owner?.ColdAction?.Invoke("dispose.shader");
    }
    sealed record GraphicsPipeline(RendererPerfConstructionFixture Owner, GpuPipelineConstructionIdentity ConstructionIdentity)
        : IGpuPipeline, IGpuPipelineConstructionDiagnostics, IGpuPipelineDiagnostics, IGpuBudgetedPipelineDiagnostics
    {
        public GpuPipelineCompilerReadResult ReadCompilerReport(GpuPipelineCompilerReadRequest request, Action<string> check, System.Threading.CancellationToken cancellation)
            => Owner.ColdRead(ConstructionIdentity, request, check, cancellation);
        public void Dispose() => Owner.ColdAction?.Invoke("dispose.pipeline");
        public GpuPipelineCompilerReport ReadCompilerReport() { Owner.CompilerReads++; throw new InvalidOperationException("Forbidden live compiler read."); }
    }
    sealed record ComputePipeline(RendererPerfConstructionFixture Owner, GpuPipelineConstructionIdentity ConstructionIdentity)
        : IGpuComputePipeline, IGpuPipelineConstructionDiagnostics, IGpuPipelineDiagnostics, IGpuBudgetedPipelineDiagnostics
    {
        public GpuPipelineCompilerReadResult ReadCompilerReport(GpuPipelineCompilerReadRequest request, Action<string> check, System.Threading.CancellationToken cancellation)
            => Owner.ColdRead(ConstructionIdentity, request, check, cancellation);
        public void Dispose() => Owner.ColdAction?.Invoke("dispose.pipeline");
        public GpuPipelineCompilerReport ReadCompilerReport() { Owner.CompilerReads++; throw new InvalidOperationException("Forbidden live compiler read."); }
    }
    // Explicitly synthetic device facts, used only by headless correspondence cases.
    internal string Flags { get; set; } = "synthetic optimized";
    internal static GpuEffectiveDiagnosticConfiguration Production => RendererPerfCompilerFixture.Configuration;
    internal static GpuEffectiveDiagnosticConfiguration Diagnostic => Production with
    { Requested = new(true, false), CompilerMetadataEnabled = true, PipelineExecutableFeatureEnabled = true, PipelineCaptureFlags = 0x40 };
    GpuPipelineStageIdentity Stage(string stage, string source) => new(stage, "main",
        GpuDiagnosticIdentityEncoding.Text(source, "glsl.utf8/1"), null,
        GpuDiagnosticIdentityEncoding.Text("synthetic compiled bytes " + ArtifactSalt + source, "spirv.bytes/1"));
    static string LayoutHash(IGpuResourceLayout[] layouts) => Hash(w =>
    {
        w.Write(layouts.Length);
        foreach (var layout in layouts.Cast<Layout>())
        {
            w.Write(layout.Elements.Length);
            foreach (var e in layout.Elements) { w.Write(e.Name); w.Write((int)e.Kind); w.Write((int)e.Stages); w.Write(e.Dynamic); }
        }
    });
    internal GpuPipelineConstructionIdentity Witness(in GpuComputePipelineDescription d)
    {
        var s = (ComputeShader)d.Shader;
        var stages = ImmutableArray.Create(Stage("compute", s.Source)); string layout = LayoutHash(d.ResourceLayouts);
        string pipeline = Hash(w =>
        {
            w.Write("compute,main,no-specialization"); w.Write(stages[0].ShaderArtifactSha256.Sha256!); w.Write(layout);
            w.Write(s.ThreadGroupSizeX); w.Write(s.ThreadGroupSizeY); w.Write(s.ThreadGroupSizeZ);
        });
        return Identity(stages, pipeline, layout, s.ThreadGroupSizeX, s.ThreadGroupSizeY, s.ThreadGroupSizeZ);
    }
    GpuPipelineConstructionIdentity Witness(in GpuPipelineDescription description)
    {
        var d = description; var s = (Shaders)d.ShaderSet;
        var stages = ImmutableArray.Create(Stage("vertex", s.Vertex), Stage("fragment", s.Fragment));
        string layout = LayoutHash(d.ResourceLayouts);
        string pipeline = Hash(w =>
        {
            w.Write("graphics,vertex=main,fragment=main,no-specialization,dynamic=viewport+scissor");
            w.Write(GpuDiagnosticIdentityEncoding.Stages("artifact", stages).Sha256!); w.Write(layout); w.Write((int)d.Topology);
            var bindings = VulkanVertexInput.Build(d.VertexLayouts, out var attributes);
            w.Write(bindings.Length); foreach (var b in bindings) { w.Write(b.Binding); w.Write(b.Stride); w.Write(b.PerInstance); }
            w.Write(attributes.Length); foreach (var a in attributes) { w.Write(a.Location); w.Write(a.Binding); w.Write((int)a.Format); w.Write(a.Offset); }
            var blends = d.BlendAttachments;
            w.Write(blends.Length);
            foreach (var b in blends)
            {
                w.Write(b.BlendEnabled); w.Write((int)b.SourceColorFactor); w.Write((int)b.DestinationColorFactor); w.Write((int)b.ColorFunction);
                w.Write((int)b.SourceAlphaFactor); w.Write((int)b.DestinationAlphaFactor); w.Write((int)b.AlphaFunction);
            }
            w.Write(d.BlendFactor.X); w.Write(d.BlendFactor.Y); w.Write(d.BlendFactor.Z); w.Write(d.BlendFactor.W);
            w.Write((int)d.Rasterizer.CullMode); w.Write((int)d.Rasterizer.FillMode); w.Write((int)d.Rasterizer.FrontFace);
            w.Write(d.Rasterizer.DepthClipEnabled); w.Write(d.Rasterizer.ScissorTestEnabled);
            w.Write(d.DepthStencil.DepthTestEnabled); w.Write(d.DepthStencil.DepthWriteEnabled); w.Write((int)d.DepthStencil.Comparison);
            w.Write(d.Outputs.Colour.Length); foreach (var f in d.Outputs.Colour) w.Write((int)f);
            w.Write(d.Outputs.Depth.HasValue); if (d.Outputs.Depth is { } depth) w.Write((int)depth); w.Write(d.Outputs.SampleCount);
        });
        return Identity(stages, pipeline, layout, 0, 0, 0);
    }
    GpuPipelineConstructionIdentity Identity(ImmutableArray<GpuPipelineStageIdentity> stages, string pipeline, string layout, uint x, uint y, uint z)
        => new(GpuDiagnosticIdentityEncoding.Known(pipeline, "vulkan.pipeline/1"), GpuDiagnosticIdentityEncoding.Stages("source", stages),
            GpuDiagnosticIdentityEncoding.Stages("artifact", stages), GpuDiagnosticIdentityEncoding.Known(layout, "vulkan.layout/1"),
            GpuDiagnosticIdentityEncoding.Configuration(Configuration), GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.VulkanNative, []),
            Flags, x, y, z, stages);
    static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) write(w);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
