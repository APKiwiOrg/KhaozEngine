using System;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.MapDoc;

internal sealed class NativeUploadTestScene : IDisposable
{
    readonly IGpuTexture _colour;
    readonly IGpuFramebuffer _target;
    internal NativeUploadTestScene(IGpuDevice? device = null)
    {
        Device = new(device ?? new FakeGpuDevice());
        _colour = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(64, 48,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = Device.Factory.CreateFramebuffer(null, _colour);
        Scene = new(Device, _target.Outputs);
        Scene.Post.Starfield = false;
        Scene.Post.RenderWidth = 64;
        Scene.Post.RenderHeight = 48;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Scene.Camera.Frame(Vector3.Zero, new Vector3(4));
        Device.ResetCounters();
    }
    internal NativeUploadTestDevice Device { get; }
    internal Scene3D Scene { get; }
    internal void Render()
    {
        Scene.PrepareFrame();
        using var commands = Device.Factory.CreateCommandList();
        commands.Begin();
        Scene.RenderInternal(commands, 64, 48, _target);
        commands.End();
        Device.Submit(commands);
    }
    internal byte[] Capture() { Render(); return GpuReadback.ToRgba(Device, _colour, 64, 48); }
    public void Dispose() { Scene.Dispose(); _target.Dispose(); _colour.Dispose(); Device.Dispose(); }
}

internal sealed class NativeUploadTestDevice(IGpuDevice inner) : IGpuDevice
{
    internal IGpuDevice Inner => inner;
    internal long UploadBytes { get; private set; }
    internal void ResetCounters() { UploadBytes = 0; BufferWrites = 0; TextureWrites = 0; TextureCopies.Clear(); BufferCopies.Clear(); }
    internal List<(uint X, uint Y, uint Width, uint Mip, byte[] Bytes)> TextureCopies { get; } = new();
    internal Dictionary<IGpuBuffer, byte[]> BufferCopies { get; } = new();
    internal int BufferWrites { get; private set; }
    internal int TextureWrites { get; private set; }
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => inner.Factory;
    public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList cl) => inner.Submit(cl);
    public void Submit(IGpuCommandList cl, IGpuFence fence) => inner.Submit(cl, fence);
    public void WaitForIdle() => inner.WaitForIdle();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        UploadBytes += data.Length * Marshal.SizeOf<T>(); BufferWrites++;
        if (!BufferCopies.TryGetValue(b, out var image)) BufferCopies.Add(b, image = new byte[b.SizeInBytes]);
        MemoryMarshal.AsBytes(data).CopyTo(image.AsSpan(checked((int)offsetBytes)));
        inner.UpdateBuffer(b, offsetBytes, data);
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => UpdateBuffer(b, offsetBytes, (ReadOnlySpan<T>)data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
    { UploadBytes += Marshal.SizeOf<T>(); BufferWrites++; inner.UpdateBuffer(b, offsetBytes, in data); }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => UpdateTexture(t, data, x, y, w, h, 0, 0);
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer)
    { UploadBytes += data.Length; TextureWrites++; TextureCopies.Add((x, y, w, mip, (byte[])data.Clone())); inner.UpdateTexture(t, data, x, y, w, h, mip, layer); }
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => inner.Map(t, mode);
    public void Unmap(IGpuTexture t) => inner.Unmap(t);
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => inner.Map(b, mode);
    public void Unmap(IGpuBuffer b) => inner.Unmap(b);
    public void ResizeSwapchain(uint w, uint h) => inner.ResizeSwapchain(w, h);
    public void Present() => inner.Present();
    public void Dispose() => inner.Dispose();
}
