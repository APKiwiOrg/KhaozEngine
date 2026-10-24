using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Resources stay native handles. Only command lists need unwrapping at native submission/capture boundaries.
internal partial class RendererPerfDevice : IGpuDevice, IGpuResourceFactory
{
    readonly IGpuDevice _native;
    readonly RendererPerfRecorder _recorder;
    IGpuFramebuffer? _frameTarget;
    internal void UseFrameTarget(IGpuFramebuffer target) => _frameTarget = target;
    RendererPerfDevice(IGpuDevice native, RendererPerfRecorder recorder)
    {
        _native = native; _recorder = recorder; recorder.AttachDevice(native);
    }

    internal static IGpuDevice Wrap(IGpuDevice native, RendererPerfRecorder recorder)
    {
        if (!recorder.Enabled) return native;
        return CreateWrapper(native, recorder);
    }

    static RendererPerfDevice CreateWrapper(IGpuDevice native, RendererPerfRecorder recorder)
    {
        int flags = (native is IGpuSwapchainCapture ? 1 : 0) | (native is IGpuFencedReadback ? 2 : 0)
            | (native is IGpuResourceMemoryDiagnostics ? 4 : 0) | (native is IGpuDeviceLifecycle ? 8 : 0)
            | (native is ID3D11RemovedReason ? 16 : 0);
        return flags switch
        {
            1 => new Cap1(native, recorder),
            2 => new Cap2(native, recorder),
            3 => new Cap3(native, recorder),
            4 => new Cap4(native, recorder),
            5 => new Cap5(native, recorder),
            6 => new Cap6(native, recorder),
            7 => new Cap7(native, recorder),
            8 => new Cap8(native, recorder),
            9 => new Cap9(native, recorder),
            10 => new Cap10(native, recorder),
            11 => new Cap11(native, recorder),
            12 => new Cap12(native, recorder),
            13 => new Cap13(native, recorder),
            14 => new Cap14(native, recorder),
            15 => new Cap15(native, recorder),
            16 => new Cap16(native, recorder),
            17 => new Cap17(native, recorder),
            18 => new Cap18(native, recorder),
            19 => new Cap19(native, recorder),
            20 => new Cap20(native, recorder),
            21 => new Cap21(native, recorder),
            22 => new Cap22(native, recorder),
            23 => new Cap23(native, recorder),
            24 => new Cap24(native, recorder),
            25 => new Cap25(native, recorder),
            26 => new Cap26(native, recorder),
            27 => new Cap27(native, recorder),
            28 => new Cap28(native, recorder),
            29 => new Cap29(native, recorder),
            30 => new Cap30(native, recorder),
            31 => new Cap31(native, recorder),
            _ => new RendererPerfDevice(native, recorder)
        };
    }

    public IGpuResourceFactory Factory => this;
    public GpuBackendKind Backend => _native.Backend;
    public GpuCapabilities Capabilities => _native.Capabilities;
    public GpuDeviceDiagnostics Diagnostics => _native.Diagnostics;
    public GpuDeviceCounters Counters => _native.Counters;
    public IGpuFramebuffer? SwapchainFramebuffer => _frameTarget ?? _native.SwapchainFramebuffer;
    public IGpuSampler PointSampler => _native.PointSampler;
    public IGpuSampler LinearSampler => _native.LinearSampler;
    public bool SyncToVerticalBlank { get => _native.SyncToVerticalBlank; set => _native.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList commands)
    {
        _recorder.BeginCpuPhase("submit");
        try { _recorder.Count(RendererPerfCounter.Submissions); _native.Submit(RendererPerfCommandList.Unwrap(commands)); }
        finally { _recorder.EndCpuPhase("submit"); }
    }
    public void Submit(IGpuCommandList commands, IGpuFence completion)
    {
        _recorder.BeginCpuPhase("submit");
        try { _recorder.Count(RendererPerfCounter.Submissions); _native.Submit(RendererPerfCommandList.Unwrap(commands), completion); }
        finally { _recorder.EndCpuPhase("submit"); }
    }
    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        _recorder.BeginCpuPhase("submit");
        try { _recorder.Count(RendererPerfCounter.Submissions); _native.Submit(RendererPerfCommandList.Unwrap(commands), completions); }
        finally { _recorder.EndCpuPhase("submit"); }
    }
    public void WaitForIdle() { _recorder.Count(RendererPerfCounter.WaitForIdleCalls); _native.WaitForIdle(); }
    public void Present()
    {
        _recorder.BeginCpuPhase("present.end-frame");
        try { _recorder.Count(RendererPerfCounter.Presents); _native.Present(); }
        finally { _recorder.EndCpuPhase("present.end-frame"); }
    }
    public void ResizeSwapchain(uint w, uint h) => _native.ResizeSwapchain(w, h);
    public void Dispose() => _native.Dispose();

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        _recorder.BeginUpload(RendererPerfRecorder.UploadBytes<T>(data.Length), true);
        try { _native.UpdateBuffer(b, offsetBytes, data); } finally { _recorder.EndUpload(); }
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
    {
        _recorder.BeginUpload(RendererPerfRecorder.UploadBytes<T>(data.Length), true);
        try { _native.UpdateBuffer(b, offsetBytes, data); } finally { _recorder.EndUpload(); }
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
    {
        _recorder.BeginUpload(RendererPerfRecorder.UploadBytes<T>(1), true);
        try { _native.UpdateBuffer(b, offsetBytes, in data); } finally { _recorder.EndUpload(); }
    }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint width, uint height)
    { _recorder.TextureUpload(t, width, height); _native.UpdateTexture(t, data, x, y, width, height); }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint width, uint height, uint mip, uint layer)
    { _recorder.TextureUpload(t, width, height); _native.UpdateTexture(t, data, x, y, width, height, mip, layer); }
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => _native.Map(t, mode);
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => _native.Map(b, mode);
    public void Unmap(IGpuTexture t) => _native.Unmap(t);
    public void Unmap(IGpuBuffer b) => _native.Unmap(b);
    public bool TryReadBuffer(IGpuBuffer b, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        => _native.TryReadBuffer(b, completion, offsetBytes, destination);

    void Created() => _recorder.Count(RendererPerfCounter.ResourceCreations);
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
    {
        var buffer = _native.Factory.CreateBuffer(d); Created();
        _recorder.Count(RendererPerfCounter.BufferCreations); _recorder.Count(RendererPerfCounter.RequestedBufferBytes, d.SizeInBytes);
        return buffer;
    }
    public IGpuTexture CreateTexture(in GpuTextureDescription d)
    {
        var texture = _native.Factory.CreateTexture(d); Created(); _recorder.Count(RendererPerfCounter.TextureCreations);
        _recorder.Register(texture, new(true, (d.Usage & GpuTextureUsage.Cubemap) != 0 ? checked(d.ArrayLayers * 6) : d.ArrayLayers));
        if (_recorder.TextureBytes(texture) is { } bytes) _recorder.Count(RendererPerfCounter.RequestedTextureBytes, bytes);
        return texture;
    }
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
    { var resource = _native.Factory.CreateFramebuffer(depth, colour); Created(); return resource; }
    public IGpuSampler CreateSampler(in GpuSamplerDescription d)
    { var resource = _native.Factory.CreateSampler(d); Created(); return resource; }
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
    { var resource = _native.Factory.CreateResourceSet(d); Created(); return resource; }
    public IGpuCommandList CreateCommandList()
    { var resource = _native.Factory.CreateCommandList(); Created(); return RendererPerfCommandList.Wrap(resource, _recorder); }
    public IGpuFence CreateFence() { var resource = _native.Factory.CreateFence(); Created(); return resource; }
    public IGpuTimestampPool CreateTimestampPool(uint capacity)
    { var resource = _native.Factory.CreateTimestampPool(capacity); Created(); return resource; }

    // These methods are exposed as interfaces only by the matching cold-selected subtype below.
    public bool SwapchainCopySourceRequested
    { get => ((IGpuSwapchainCapture)_native).SwapchainCopySourceRequested; set => ((IGpuSwapchainCapture)_native).SwapchainCopySourceRequested = value; }
    public bool SwapchainCopySupported => ((IGpuSwapchainCapture)_native).SwapchainCopySupported;
    public bool SwapchainCopyable => ((IGpuSwapchainCapture)_native).SwapchainCopyable;
    public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination)
    {
        _recorder.Count(RendererPerfCounter.TextureCopies);
        _recorder.CopyBytes(checked((long)destination.Width * destination.Height * RendererPerfRecorder.BytesPerPixel(destination.Format)));
        ((IGpuSwapchainCapture)_native).CopySwapchainColor(RendererPerfCommandList.Unwrap(commands), destination);
    }
    public MappedData MapForRead(IGpuTexture staging, IGpuFence fence) => ((IGpuFencedReadback)_native).MapForRead(staging, fence);
    public GpuResourceMemorySnapshot ReadMemorySnapshot() => ((IGpuResourceMemoryDiagnostics)_native).ReadMemorySnapshot();
    public void MarkDeviceDisposed() => ((IGpuDeviceLifecycle)_native).MarkDeviceDisposed();
    public int GetDeviceRemovedReason() => ((ID3D11RemovedReason)_native).GetDeviceRemovedReason();

    // Explicit variants preserve presence AND absence without reflection, proxies or runtime code generation.
    sealed class Cap1(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture;
    sealed class Cap2(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback;
    sealed class Cap3(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback;
    sealed class Cap4(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuResourceMemoryDiagnostics;
    sealed class Cap5(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuResourceMemoryDiagnostics;
    sealed class Cap6(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuResourceMemoryDiagnostics;
    sealed class Cap7(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuResourceMemoryDiagnostics;
    sealed class Cap8(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuDeviceLifecycle;
    sealed class Cap9(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuDeviceLifecycle;
    sealed class Cap10(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuDeviceLifecycle;
    sealed class Cap11(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuDeviceLifecycle;
    sealed class Cap12(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle;
    sealed class Cap13(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle;
    sealed class Cap14(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle;
    sealed class Cap15(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle;
    sealed class Cap16(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), ID3D11RemovedReason;
    sealed class Cap17(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, ID3D11RemovedReason;
    sealed class Cap18(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, ID3D11RemovedReason;
    sealed class Cap19(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, ID3D11RemovedReason;
    sealed class Cap20(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuResourceMemoryDiagnostics, ID3D11RemovedReason;
    sealed class Cap21(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuResourceMemoryDiagnostics, ID3D11RemovedReason;
    sealed class Cap22(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuResourceMemoryDiagnostics, ID3D11RemovedReason;
    sealed class Cap23(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuResourceMemoryDiagnostics, ID3D11RemovedReason;
    sealed class Cap24(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap25(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap26(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap27(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap28(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap29(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap30(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuFencedReadback, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class Cap31(IGpuDevice native, RendererPerfRecorder recorder) : RendererPerfDevice(native, recorder), IGpuSwapchainCapture, IGpuFencedReadback, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle, ID3D11RemovedReason;
}
