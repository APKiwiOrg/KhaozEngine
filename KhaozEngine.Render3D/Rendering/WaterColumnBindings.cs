using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Borrowed model shadow resources, with initialized neutral resources for standalone water.</summary>
internal sealed class WaterColumnBindings : IDisposable
{
    readonly GpuRetireQueue _retired;
    IGpuTexture? _ownedAtlas;
    IGpuSampler? _ownedSampler;
    IGpuBuffer? _ownedUniforms;

    internal IGpuTexture ShadowAtlas { get; private set; }
    internal IGpuSampler ShadowSampler { get; private set; }
    internal IGpuBuffer FrameUniforms { get; private set; }

    internal WaterColumnBindings(IGpuDevice device, GpuRetireQueue retired)
    {
        _retired = retired;
        try
        {
            _ownedAtlas = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
            device.UpdateTexture(_ownedAtlas, BitConverter.GetBytes(1f), 0, 0, 1, 1);
            _ownedSampler = device.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.MinPointMagPointMipPoint,
                GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));
            // The shader reads the complete 992-byte prefix. Pad its standalone binding to 256-byte alignment.
            _ownedUniforms = device.Factory.CreateBuffer(new GpuBufferDescription(1024, GpuBufferUsage.UniformBuffer));
            device.UpdateBuffer(_ownedUniforms, 0, new byte[1024]);
            ShadowAtlas = _ownedAtlas;
            ShadowSampler = _ownedSampler;
            FrameUniforms = _ownedUniforms;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void SetShadows(IGpuTexture atlas, IGpuSampler sampler, IGpuBuffer frameUniforms)
    {
        if (_ownedAtlas is not null && !ReferenceEquals(_ownedAtlas, atlas))
        {
            _retired.Retire(_ownedAtlas);
            _ownedAtlas = null;
        }
        if (_ownedSampler is not null && !ReferenceEquals(_ownedSampler, sampler))
        {
            _retired.Retire(_ownedSampler);
            _ownedSampler = null;
        }
        if (_ownedUniforms is not null && !ReferenceEquals(_ownedUniforms, frameUniforms))
        {
            _retired.Retire(_ownedUniforms);
            _ownedUniforms = null;
        }
        ShadowAtlas = atlas;
        ShadowSampler = sampler;
        FrameUniforms = frameUniforms;
    }

    public void Dispose()
    {
        _ownedAtlas?.Dispose();
        _ownedSampler?.Dispose();
        _ownedUniforms?.Dispose();
    }
}
