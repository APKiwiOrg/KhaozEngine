using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Borrowed shadow and colour resources, with initialized neutral resources for standalone water.</summary>
internal sealed class WaterColumnBindings : IDisposable
{
    readonly IGpuDevice _device;
    readonly GpuRetireQueue _retired;
    IGpuTexture? _ownedAtlas;
    IGpuSampler? _ownedSampler;
    IGpuBuffer? _ownedUniforms;
    IGpuTexture? _ownedColor;
    IGpuTexture? _sceneColor;
    IGpuSampler? _colorSampler;

    internal IGpuTexture ShadowAtlas { get; private set; }
    internal IGpuSampler ShadowSampler { get; private set; }
    internal IGpuBuffer FrameUniforms { get; private set; }
    internal IGpuTexture SceneColor => _sceneColor ??= CreateNeutralColor();
    internal IGpuSampler ColorSampler => _colorSampler ??= _device.Factory.CreateSampler(new GpuSamplerDescription(
        GpuSamplerFilter.MinPointMagPointMipPoint,
        GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));
    internal int ColorGeneration { get; private set; }

    internal WaterColumnBindings(IGpuDevice device, GpuRetireQueue retired,
        (IGpuTexture Atlas, IGpuSampler Sampler, IGpuBuffer FrameUniforms)? shadows = null)
    {
        _device = device;
        _retired = retired;
        try
        {
            if (shadows is { } borrowed)
            {
                ShadowAtlas = borrowed.Atlas;
                ShadowSampler = borrowed.Sampler;
                FrameUniforms = borrowed.FrameUniforms;
                return;
            }
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
            _sceneColor = CreateNeutralColor();
            _ = ColorSampler;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    IGpuTexture CreateNeutralColor()
    {
        // A scene with no water never builds a water set, so it never needs these colour resources.
        IGpuTexture color = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        try { _device.UpdateTexture(color, new byte[4], 0, 0, 1, 1); }
        catch { color.Dispose(); throw; }
        return _ownedColor = color;
    }

    internal void SetColor(IGpuTexture texture, int generation)
    {
        if (_ownedColor is not null && !ReferenceEquals(_ownedColor, texture))
        {
            _retired.Retire(_ownedColor);
            _ownedColor = null;
        }
        _sceneColor = texture;
        ColorGeneration = generation;
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
        _ownedColor?.Dispose();
        _colorSampler?.Dispose();
    }
}
